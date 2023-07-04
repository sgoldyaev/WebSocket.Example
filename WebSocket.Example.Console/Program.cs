using WebSocket.Example;

internal class Program
{
    private static void Main(string[] args)
    {
        var socket = new BinanceClient();

        ConsoleKeyInfo key = default;
        do
        {
            if (key == default || key.Key == ConsoleKey.C)
            {
                socket.Connect();
            }

            if (key.Key == ConsoleKey.D)
            {
                socket.UnSubscribeTrades();

                socket.Disconnect();
            }

            key = Console.ReadKey();
        }
        while (key.Key != ConsoleKey.Escape);
    }
}