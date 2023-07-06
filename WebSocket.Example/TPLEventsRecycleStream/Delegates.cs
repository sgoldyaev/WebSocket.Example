namespace WebSocket.Example.TPLEventsRecycleStream;

public delegate void OnWebSocketError(WebSocketClient socket, Exception exception);
public delegate void OnWebSocketOpen(WebSocketClient socket);
public delegate void OnWebSocketClose(WebSocketClient socket, CloseResult reason);
public delegate void OnWebSocketMessage(WebSocketClient socket, Stream stream, int queued);