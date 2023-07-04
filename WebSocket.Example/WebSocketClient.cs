using System.Net;
using System.Net.WebSockets;
using System.Threading.Tasks.Dataflow;

namespace WebSocket.Example;

public enum CloseReason
{
    Client,
    Server,
    Error,
}

public delegate void OnWebSocketError(WebSocketClient socket, Exception exception);
public delegate void OnWebSocketOpen(WebSocketClient socket);
public delegate void OnWebSocketClose(WebSocketClient socket, CloseReason reason);
public delegate void OnWebSocketMessage(WebSocketClient socket, Stream stream, int queued);

public class WebSocketClient : IDisposable
{
    private const int OPERATION_TIMEOUT_MS = 5_000;
    private const int RECEIVE_BUFFER_BYTES = 4_096;
    private const int SEND_BUFFER_BYTES = 4_096;

    private readonly Uri uri;
    private readonly IWebProxy proxy;

    private readonly ActionBlock<Func<Task>> requests;
    private readonly ActionBlock<Func<Task>> receiving;
    private readonly ActionBlock<Stream> messages;

    private ClientWebSocket? socket;

    public WebSocketClient(string url, IWebProxy webProxy)
    {
        uri = new Uri(url);
        proxy = webProxy;

        var requestsOptions = new ExecutionDataflowBlockOptions
        {
            EnsureOrdered = true,
            MaxDegreeOfParallelism = 1,
        };

        var messagesOptions = new ExecutionDataflowBlockOptions
        {
            EnsureOrdered = true,
            MaxDegreeOfParallelism = 1,
        };

        requests = new ActionBlock<Func<Task>>(OnProcessRequest, requestsOptions);
        receiving = new ActionBlock<Func<Task>>(OnProcessRequest, requestsOptions);
        messages = new ActionBlock<Stream>(OnProcessMessage, messagesOptions);
    }

    public event OnWebSocketOpen? OnOpen;
    public event OnWebSocketClose? OnClose;
    public event OnWebSocketError? OnError;
    public event OnWebSocketMessage? OnMessage;

    public Task ConnectAsync(CancellationToken cancellation)
    {
        var task = ConnectionTask(uri, cancellation);

        return requests.SendAsync(task);
    }

    public Task DisconnectAsync(CancellationToken cancellation)
    {
        var task = DisconnectionTask(cancellation);

        return requests.SendAsync(task);
    }

    public Task SendAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellation)
    {
        var task = SendTask(buffer, cancellation);

        return requests.SendAsync(task);
    }

    public void Dispose()
    {
        DisconnectAsync(CancellationToken.None);

        DisposeAsync();
    }

    protected virtual Task ReceiveAsync()
    {
        var task = ReceivingTask();

        return receiving.SendAsync(task);
    }

    protected virtual async void OnProcessRequest(Func<Task> task)
    {
        try
        {
            await task();
        }
        catch (Exception e)
        {
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

    protected Task DisposeAsync()
    {
        var task = DisposeTask();

        return requests.SendAsync(task);
    }

    protected virtual Func<Task> ConnectionTask(Uri uri, CancellationToken cancellation)
    {
        return async () =>
        {
            socket = new ClientWebSocket();

            socket.Options.Proxy = proxy;

            using var timeOut = new CancellationTokenSource(OPERATION_TIMEOUT_MS);
            using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellation, timeOut.Token);
            await socket.ConnectAsync(uri, operation.Token).ConfigureAwait(false);

            var receiveTask = ReceiveAsync().ConfigureAwait(false);
        };
    }

    protected virtual Func<Task> DisconnectionTask(CancellationToken cancellation)
    {
        return async () =>
        {
            if (socket is null) return;

            if (socket.State == WebSocketState.Open)
            {
                using var timeOut = new CancellationTokenSource(OPERATION_TIMEOUT_MS);
                using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellation, timeOut.Token);
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Close", operation.Token).ConfigureAwait(false);
            }
            else
            {
                await DisposeAsync();
            }
        };
    }

    protected virtual Func<Task> SendTask(ReadOnlyMemory<byte> buffer, CancellationToken cancellation)
    {
        return async () =>
        {
            if (socket is null || socket.State != WebSocketState.Open) return;

            using var timeOut = new CancellationTokenSource(OPERATION_TIMEOUT_MS);
            using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellation, timeOut.Token);
            await socket.SendAsync(buffer, WebSocketMessageType.Text, true, operation.Token).ConfigureAwait(false);
        };
    }

    protected virtual Func<Task> ReceivingTask()
    {
        return async () =>
        {
            var reason = CloseReason.Client;

            var memory = System.Net.WebSockets.WebSocket.CreateClientBuffer(RECEIVE_BUFFER_BYTES, SEND_BUFFER_BYTES);

            using var receiveCancellation = new CancellationTokenSource();

            while (receiveCancellation.IsCancellationRequested == false)
            {
                OnOpen?.Invoke(this);

                while (socket?.State == WebSocketState.Open)
                {
                    WebSocketReceiveResult? result = default;

                    var stream = new MemoryStream();
                    try
                    {
                        //using (var stream = new MemoryStream())
                        {
                            do
                            {
                                if (receiveCancellation == null) break;

                                result = await socket.ReceiveAsync(memory, receiveCancellation.Token);
                                await stream.WriteAsync(memory.AsMemory(memory.Offset, result.Count), receiveCancellation.Token);
                            }
                            while (receiveCancellation.IsCancellationRequested == false && result?.EndOfMessage == false);

                            stream.Position = 0;

                            await messages.SendAsync(stream);

                            /// NOTE [sg]: in this case using var stream = new MemoryStream() is possible
                            /// await messages.SendAsync(stream.ToArray());
                        }

                        if (socket?.State == WebSocketState.CloseReceived && result?.MessageType == WebSocketMessageType.Close)
                        {
                            await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Acknowledge Close", CancellationToken.None);

                            reason = CloseReason.Server;

                            receiveCancellation?.Cancel();
                        }
                    }
                    catch (Exception e)
                    {
                        stream.Dispose();

                        reason = CloseReason.Error;

                        OnError?.Invoke(this, e);

                        break;
                    }
                }

                OnClose?.Invoke(this, reason);

                break;
            }
        };
    }

    protected virtual Func<Task> DisposeTask()
    {
        return () =>
        {
            return new Task(() =>
            {
                /// socket?.Abort();
                socket?.Dispose();
                socket = null;

            });
        };
    }
}