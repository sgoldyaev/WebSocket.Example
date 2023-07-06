using System.Net.WebSockets;

namespace WebSocket.Example.TPLEventsRecycleStream;

public enum CloseReason
{
    Client,
    Server,
    Error,
}

public class CloseResult
{
    public CloseResult(CloseReason reason, WebSocketCloseStatus? status, string? description)
    {
        Reason = reason;
        Status = status;
        Description = description;
    }

    public CloseReason Reason { get; }
    public WebSocketCloseStatus? Status { get; }
    public string? Description { get; }
}
