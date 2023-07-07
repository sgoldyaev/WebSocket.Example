using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using WebSocket.Example.TPLEventsRecycleStream;

namespace WebSocket.Example;

public class BinanceClient : IDisposable
{
    private readonly int RECONNECT_DELAY_MS = 1_000;
    private readonly Uri uri = new Uri("wss://stream.binance.com/stream");
    private readonly IWebProxy fiddler = new WebProxy("http://127.0.0.1:8888");
    private readonly JsonSerializerOptions options = new JsonSerializerOptions { WriteIndented = false };
    private readonly CancellationToken none = CancellationToken.None;
    private readonly WebSocketClient socket;

    public BinanceClient()
    {
        socket = new WebSocketClient(uri, fiddler);
        socket.OnOpen += SocketOnOpened;
        socket.OnClose += SocketOnClosed;
        socket.OnError += SocketOnError;
        socket.OnMessage += SocketOnMessage;
    }
    
    public void Dispose()
    {
        socket.OnOpen -= SocketOnOpened;
        socket.OnClose -= SocketOnClosed;
        socket.OnError -= SocketOnError;
        socket.OnMessage -= SocketOnMessage;
        socket.Dispose();

        GC.SuppressFinalize(this);
    }

    public void Connect()
    {
        Log("Connect");

        socket.ConnectAsync(none);
    }

    public void Disconnect()
    {
        Log("Disconnect");

        socket.DisconnectAsync(none);
    }

    public void SubscribeTrades()
    {
        var request = "{ \"id\":1, \"method\":\"SUBSCRIBE\", \"params\":[\"btcusdt@aggTrade\", \"ethusdt@aggTrade\", \"maticusdt@aggTrade\"] }";

        var buffer = Encoding.UTF8.GetBytes(request);

        Log($"Subscribe:{request}");

        socket.SendAsync(buffer, none);
    }

    public void UnSubscribeTrades()
    {
        var request = "{ \"id\":2, \"method\":\"UNSUBSCRIBE\", \"params\":[\"btcusdt@aggTrade\", \"ethusdt@aggTrade\", \"maticusdt@aggTrade\"] }";

        var buffer = Encoding.UTF8.GetBytes(request);

        Log($"UnSubscribe:{request}");

        socket.SendAsync(buffer, none);
    }

    private void SocketOnMessage(WebSocketClient socket, Stream stream, int queued)
    {
        if (stream.Length > 0)
        {
            var node = JsonNode.Parse(stream);
            if (node is not null)
            {
                ///Log($"Queued:{queued}, Stream.Length {stream.Length}");
                Log($"Queued:{queued}, Message:{node.ToJsonString(options)}");
            }
        }
        else
        {
            Log($"Stream.Length {stream.Length}");
        }

        stream.Dispose();
    }

    private void SocketOnOpened(WebSocketClient socket)
    {
        Log("Opened");

        SubscribeTrades();
    }

    private void SocketOnClosed(WebSocketClient socket, CloseResult result)
    {
        if (result.Reason == CloseReason.Client)
        {
            Log("Closed");
        }
        else
        {
            Log($"Reconnecting in {RECONNECT_DELAY_MS} ms");

            Task.Delay(RECONNECT_DELAY_MS)
                .ContinueWith(x => socket.ConnectAsync(none));
        }
    }

    private void SocketOnError(WebSocketClient socket, Exception exception)
    {
        Log($"Error: {exception}");
    }

    private static async void Log(string message)
    {
        /// Thread.Sleep(200);
        /// ThreadPool.GetAvailableThreads(out var workerThreads, out var completionPortThreads);
        /// Console.Out.WriteLine("WORK:{0}, IOCP:{1}", maxWorkerThreads - workerThreads, maxCompletionPortThreads - completionPortThreads);
        
        /// await Console.Out.WriteLineAsync($"Thread:{Thread.CurrentThread.ManagedThreadId}, {message}");
        await Console.Out.WriteLineAsync(message);
    }
}
