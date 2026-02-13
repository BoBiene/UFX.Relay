# Connection Aggregation

Connection aggregation helps when there are a large number of idle connections (such as WebSockets) that need to be maintained.

[Azure Web PubSub](https://azure.microsoft.com/en-gb/products/web-pubsub) is an example of a cloud service that provides WebSocket connection aggregation. Inverting the WebSocket connection direction of ReverseTunnel.Yarp provides an equivalent capability to Azure Web PubSub but with the added benefit of being self-contained and not requiring a 3rd party service.

Typically, the Forwarder would be hosted on the cloud and the listener on-prem, this allows for the cloud application to connect to the on-prem application. However, it is possible to have the forwarder on-prem and the listener in the cloud while still using an out-bound WebSocket connection from the on-prem instance to the cloud thus allowing for connection aggregation of multiple on-prem connections via a single connection to a cloud service.

## Aggregation Cloud End Example

```csharp
var builder = WebApplication.CreateBuilder(args);
builder.WebHost.AddTunnelListener(options =>{ options.DefaultTunnelId = "123"; });
var app = builder.Build();
app.MapTunnelHost();
app.Run();
```

## Aggregation On-Prem End Example

```csharp
var builder = WebApplication.CreateBuilder(args);
builder.Services.AddTunnelForwarder(options => { options.DefaultTunnelId = "123"; });
builder.Services.AddTunnelClient(options =>
    options with
    {
        TunnelHost = "wss://localhost:7100",
        TunnelId = "123"
    });
var app = builder.Build();
app.MapTunnelForwarder();
app.Run();
```
