# Example of using Microsoft's .NET 6 ClientWebSocket

## Motivation:
With the release of .NET 6, Microsoft introduced its own WebSocket library. However, finding a well-documented and practical example for using this library in real-world scenarios has been quite challenging. 

For years, developers have been working with WebSockets using the familiar event-based paradigm, relying on events such as OnOpened, OnClosed, OnError, and OnMessage. 
The shift to the new approach of working with WebSockets, which involves asynchronous programming and the use of async/await calls, can be confusing and requires specific skills and preparation. 
Building a reliable and efficient pipeline for practical usage has become a true endeavor. Furthermore, the official documentation lacks clear and comprehensive guidance on properly managing the WebSocket lifecycle, especially when it comes to closing the connection without encountering errors.

## Features:
- Thread-safe management of websockets (Connect/Disconnect)
- Implementation of the Actor Model using TPL DataFlow ActionBlock. A similar solution could be achieved with Tasks.Streams.
- Incoming command queue for control
- Outgoing message queue for efficient I/O Completion Ports thread utilization
- Reconnection in case of connection loss
- Old school OnOpened, OnClosed, OnError, OnMessage events for websocket life cycle management

## Inspired by:
- https://mcguirev10.com/2019/08/17/how-to-close-websocket-correctly.html
- https://mcguirev10.com/2019/01/15/simple-multi-client-websocket-server.html
- https://github.com/dotnet/runtime/issues/81762
- https://medium.com/draftkings-engineering/entering-actor-model-nirvana-with-f-and-tpl-dataflow-b8ab34b84656
- https://codereview.stackexchange.com/questions/276263/general-web-socket-client-with-auto-reconnect-capabilities
- https://adamsitnik.com/Array-Pool/
- https://github.com/dotnet/runtime/blob/main/src/libraries/System.Net.WebSockets/src/System/Net/WebSockets/ManagedWebSocket.cs

## Fiddler logs
  ![image](https://github.com/sgoldyaev/WebSocket.Example/assets/25266453/a2b68368-56e8-47c9-81e4-a097deddf683)

## Further steps
- ~~Replace MemoryStream with RecyclableMemoryStream https://github.com/microsoft/Microsoft.IO.RecyclableMemoryStream~~
- Replace TPL DataFlow with Task.Streams
