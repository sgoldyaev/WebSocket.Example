using Microsoft.IO;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Threading.Tasks.Dataflow;
using Command = System.Threading.Tasks.Task<System.Threading.Tasks.Task>;

namespace WebSocket.Example.TPLEventsRecycleStream;

public class WebSocketClient
{
    private readonly TextWriter log = Console.Out;

    private const int OPERATION_TIMEOUT_MS = 5_000;
    private const int RECEIVE_BUFFER_BYTES = 4_096;
    private const int SEND_BUFFER_BYTES = 4_096;

    private readonly RecyclableMemoryStreamManager streamManager;
    private readonly Uri uri;
    private readonly IWebProxy proxy;

    private readonly ActionBlock<Command> requests;
    private readonly ActionBlock<Command> receiving;
    private readonly ActionBlock<Stream> messages;

    private ClientWebSocket? socket;

    public WebSocketClient(string url, IWebProxy webProxy)
    {
        uri = new Uri(url);
        proxy = webProxy;
        streamManager = new RecyclableMemoryStreamManager();

        var executionOptions = new ExecutionDataflowBlockOptions
        {
            EnsureOrdered = true,
            MaxDegreeOfParallelism = 1,
        };

        requests = new ActionBlock<Command>(OnProcessRequest, executionOptions);
        receiving = new ActionBlock<Command>(OnProcessRequest, executionOptions);
        messages = new ActionBlock<Stream>(OnProcessMessage, executionOptions);
    }

    public WebSocketState? State => socket?.State;

    public event OnWebSocketOpen? OnOpen;
    public event OnWebSocketClose? OnClose;
    public event OnWebSocketError? OnError;
    public event OnWebSocketMessage? OnMessage;

    public Task ConnectAsync(CancellationToken cancellation)
    {
        var command = ConnectionCommand(uri, cancellation);

        return requests.SendAsync(command);
    }

    public Task DisconnectAsync(CancellationToken cancellation)
    {
        var command = DisconnectionCommand(CloseReason.Client, cancellation);

        return requests.SendAsync(command);
    }

    public Task SendAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellation)
    {
        var command = SendCommand(buffer, cancellation);

        return requests.SendAsync(command);
    }

    public Task SendAsync(string request, CancellationToken cancellation)
    {
        var buffer = Encoding.UTF8.GetBytes(request);

        return SendAsync(buffer, cancellation);
    }

    protected virtual async void OnProcessRequest(Command command)
    {
        try
        {
            command.Start();

            await command.Unwrap();
        }
        catch (Exception e)
        {
            socket?.Abort();

            OnError?.Invoke(this, e);

            OnClose?.Invoke(this, CloseReason.Error);
        }
    }

    protected virtual void OnProcessMessage(Stream stream)
    {
        try
        {
            OnMessage?.Invoke(this, stream, messages.InputCount);
        }
        catch (Exception e)
        {
            OnError?.Invoke(this, e);
        }
    }

    protected virtual Command ConnectionCommand(Uri uri, CancellationToken cancellation)
    {
        return new Command(async () =>
        {
            if (socket?.State == WebSocketState.Open)
            {
                log.WriteLine($"WebSocket: Invalid state '{socket.State}' for Connect operation.");
                return;
            }

            socket = new ClientWebSocket();

            socket.Options.Proxy = proxy;

            using var timeOut = new CancellationTokenSource(OPERATION_TIMEOUT_MS);
            using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellation, timeOut.Token);
            await socket.ConnectAsync(uri, operation.Token);

            var receiveTask = ReceivingCommand(cancellation);
            await receiving.SendAsync(receiveTask, operation.Token);

            /// NOTE [sg]: eager notification, it is also possible to notify after socket?.State complete to Opened transition
            OnOpen?.Invoke(this);
        });
    }

    protected virtual Command DisconnectionCommand(CloseReason reason, CancellationToken cancellation)
    {
        return new Command(async () =>
        {
            /// NOTE [sg]: we want to process Disconnect only once. Skip Disconnection after CloseSend
            if (socket is null || !(socket.State == WebSocketState.Open || socket.State == WebSocketState.CloseReceived)) return;

            using var timeOut = new CancellationTokenSource(OPERATION_TIMEOUT_MS);
            using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellation, timeOut.Token);

            if (socket.State == WebSocketState.Open)
                await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Close", operation.Token);

            else if (socket.State == WebSocketState.CloseReceived)
                await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Acknowledge Close", operation.Token);

            else
                log.WriteLine($"WebSocket: Invalid state '{socket.State}' for Disconnect operation.");

            /// NOTE [sg]: eager notification, it is also possible to notify after socket?.State complete to Closed transition
            OnClose?.Invoke(this, reason);
        });
    }

    protected virtual Command SendCommand(ReadOnlyMemory<byte> buffer, CancellationToken cancellation)
    {
        return new Command(async () =>
        {
            if (socket is null || !(socket.State == WebSocketState.Open || socket.State == WebSocketState.CloseReceived))
            {
                log.WriteLine($"WebSocket: Invalid state '{socket?.State}' for Send operation");
                return;
            };

            using var timeOut = new CancellationTokenSource(OPERATION_TIMEOUT_MS);
            using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellation, timeOut.Token);
            await socket.SendAsync(buffer, WebSocketMessageType.Text, true, operation.Token);
        });
    }

    protected virtual Command ReceivingCommand(CancellationToken cancellation)
    {
        return new Command(async () =>
        {
            var reason = CloseReason.Client;

            var memory = System.Net.WebSockets.WebSocket.CreateClientBuffer(RECEIVE_BUFFER_BYTES, SEND_BUFFER_BYTES);

            using var receiveCancellation = new CancellationTokenSource();

            while (receiveCancellation.IsCancellationRequested == false)
            {
                /* 
                /// NOTE [sg]: It is also possible to notify OnOpen when socket.State == WebSocketState.Open
                while (socket?.State != WebSocketState.Open)
                    await Task.Delay(10);

                OnOpen?.Invoke(this);
                */

                /// NOTE [sg]: we don't want to process messages after SendClose frame
                while (socket?.State == WebSocketState.Open)

                /// NOTE [sg]: we want to get messages after SendClose frame but before CloseReceived
                //while (socket?.State == WebSocketState.Open || socket?.State == WebSocketState.CloseSent)
                {
                    WebSocketReceiveResult? result = default;

                    var stream = streamManager.GetStream("webSocketEvent", memory.Count);
                    try
                    {
                        do
                        {
                            result = await socket.ReceiveAsync(memory, receiveCancellation.Token);
                            await stream.WriteAsync(memory.AsMemory(memory.Offset, result.Count), receiveCancellation.Token);
                        }
                        while (result?.EndOfMessage == false);

                        stream.Position = 0;

                        await messages.SendAsync(stream);

                        if (socket?.State == WebSocketState.CloseReceived && result?.MessageType == WebSocketMessageType.Close)
                        {
                            reason = CloseReason.Server;

                            break;
                        }
                    }
                    catch (Exception e)
                    {
                        log.WriteLine($"WebSocket:OnException State:{socket?.State}, {e}");

                        stream.Dispose();

                        reason = CloseReason.Error;

                        OnError?.Invoke(this, e);

                        break;
                    }
                }

                /*
                /// NOTE [sg]: It is also possible to notify OnClose when socket.State == WebSocketState.Closed
                /// in this case DisconnectionCommand should be process after receiving result?.MessageType == WebSocketMessageType.Close 
                while (socket?.State != WebSocketState.Closed || socket?.State != WebSocketState.Abort)
                    await Task.Delay(10);

                OnClose?.Invoke(this, reason);
                */

                receiveCancellation.Cancel();
            }

            var disconnect = DisconnectionCommand(reason, cancellation);

            await requests.SendAsync(disconnect);
        });
    }
}