﻿using CommunityToolkit.Diagnostics;
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
    private readonly IWebProxy? proxy;

    private readonly ActionBlock<Command> requests;
    private readonly ActionBlock<Command> receiving;
    private readonly ActionBlock<Stream> messages;

    private ClientWebSocket? socket;

    public WebSocketClient(Uri url, IWebProxy webProxy)
    {
        Guard.IsNotNull(url);

        uri = url;
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

    public event OnWebSocketOpen? OnOpen;
    public event OnWebSocketClose? OnClose;
    public event OnWebSocketError? OnError;
    public event OnWebSocketMessage? OnMessage;

    public void Dispose()
    {
        socket?.Dispose();
    }

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
        Guard.IsNotEmpty(buffer);

        var command = SendCommand(buffer, cancellation);

        return requests.SendAsync(command);
    }

    public Task SendAsync(string request, CancellationToken cancellation)
    {
        Guard.IsNotNullOrEmpty(request);

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

            OnClose?.Invoke(this, new CloseResult(CloseReason.Error, WebSocketCloseStatus.ProtocolError, e.Message));
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
        });
    }

    protected virtual Command DisconnectionCommand(CloseReason reason, CancellationToken cancellation)
    {
        return new Command(async () =>
        {
            /// NOTE [sg]: we want to process Disconnect only once. Skip Disconnection after CloseSend
            if (socket is null || !(socket.State == WebSocketState.Open || socket.State == WebSocketState.CloseReceived || socket.State == WebSocketState.CloseSent))
            {
                log.WriteLine($"WebSocket: Invalid state '{socket?.State}' for Disconnect operation.");
                return;
            };

            using var timeOut = new CancellationTokenSource(OPERATION_TIMEOUT_MS);
            using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellation, timeOut.Token);

            await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Close", operation.Token);
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
            var onOpen = OnOpen;

            var onClose = OnClose;

            var reason = CloseReason.Client;

            WebSocketCloseStatus? status = default;

            string? statusDescription = default;

            var memory = System.Net.WebSockets.WebSocket.CreateClientBuffer(RECEIVE_BUFFER_BYTES, SEND_BUFFER_BYTES);

            using var receiveCancellation = new CancellationTokenSource();

            while (receiveCancellation.IsCancellationRequested == false)
            {
                Stream stream = Stream.Null;

                WebSocketReceiveResult? result = default;

                try
                {
                    /// NOTE [sg]: we want to get messages after SendClose frame but before CloseReceived
                    while (socket?.State == WebSocketState.Open || socket?.State == WebSocketState.CloseSent)
                    {
                        onOpen?.Invoke(this);

                        onOpen = null;

                        stream = streamManager.GetStream("webSocketEvent", memory.Count);

                        do
                        {
                            result = await socket.ReceiveAsync(memory, receiveCancellation.Token);
                            await stream.WriteAsync(memory.AsMemory(memory.Offset, result.Count), receiveCancellation.Token);
                        }
                        while (result?.EndOfMessage == false);

                        stream.Position = 0;

                        await messages.SendAsync(stream);
                    }

                    if (socket?.State == WebSocketState.CloseReceived && result?.MessageType == WebSocketMessageType.Close)
                    {
                        reason = CloseReason.Server;

                        status = result?.CloseStatus;

                        statusDescription = result?.CloseStatusDescription;

                        await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Acknowledge Close", cancellation);
                    }
                }
                catch (Exception e)
                {
                    log.WriteLine($"WebSocket:OnException State:{socket?.State}, {e}");

                    stream.Dispose();

                    reason = CloseReason.Error;

                    status = WebSocketCloseStatus.ProtocolError;

                    statusDescription = e.Message;

                    OnError?.Invoke(this, e);
                }

                receiveCancellation.Cancel();
            }

            if (socket?.State == WebSocketState.Closed || socket?.State == WebSocketState.Aborted)
            {
                onClose?.Invoke(this, new CloseResult(reason, status, statusDescription));

                onClose = null;
            }
            else
            {
                log.WriteLine($"WebSocket:OnClosing invalid state State:{socket?.State}");
            }
        });
    }
}