# Transports and replica sets

ReverseTunnel.Yarp uses WebSocket transport by default. The tunnel logic above the transport stays stream-based:

```text
YARP Forwarder / Listener
  -> Tunnel Session
    -> MultiplexingStream
      -> ITunnelClientTransport / ITunnelServerTransport
        -> WebSocket
        -> optional gRPC
```

## WebSocket transport

Existing applications do not need to change. `AddTunnelClient`, `AddTunnelForwarder`, `MapTunnelHost`, and `MapTunnelForwarder` continue to use WebSockets unless another transport is registered and selected.

```csharp
builder.Services.AddTunnelClient(options => options with
{
    TunnelHost = "wss://relay.example.com",
    TunnelId = "customer-a",
    Transport = TunnelTransportKind.WebSocket
});
```

## gRPC transport

The gRPC transport is optional and lives in `ReverseTunnel.Yarp.Grpc`. It uses bidirectional streaming and carries tunnel bytes in `TunnelFrame.payload`. The first message on the stream is a small `TunnelConnect` handshake containing only the tunnel id and connection id; subsequent `TunnelFrame` messages carry only payload bytes and sequence data.

Client `RequestHeaders` are sent as native gRPC call metadata, which means HTTP/2 headers on the opening `Connect` call. ASP.NET Core gRPC hosting exposes those values on the server-side `HttpContext.Request.Headers`, so existing header-based callbacks and providers can read `Authorization`, `X-Tunnel-Key`, `X-Route-Id`, or other caller-supplied headers without custom proto metadata reconstruction.

Caller-supplied metadata must follow gRPC metadata rules. Header names and non-binary values must be ASCII, names are normalized to lowercase on the wire, HTTP/2 pseudo-headers such as `:path` are reserved, and `grpc-*` names are reserved by gRPC. Binary metadata must use a `-bin` name and a base64 string value. Invalid entries fail fast when the gRPC transport opens the tunnel.

```csharp
builder.Services.AddTunnelClient(options => options with
{
    TunnelHost = "https://relay.example.com",
    TunnelId = "customer-a",
    Transport = TunnelTransportKind.Grpc,
    RequestHeaders = new Dictionary<string, string>
    {
        ["Authorization"] = "Bearer <jwt>",
        ["X-Tunnel-Key"] = "client-key",
        ["X-Route-Id"] = "route-42"
    }
});

builder.Services.AddReverseTunnelGrpcTransport(options =>
{
    options.ClientKeepAlivePingDelay = TimeSpan.FromSeconds(30);
    options.ClientKeepAlivePingTimeout = TimeSpan.FromSeconds(15);
    options.ServerKeepAlivePingDelay = TimeSpan.FromSeconds(30);
    options.ServerKeepAlivePingTimeout = TimeSpan.FromSeconds(15);
});
```

The keepalive values above are the defaults. Keep the ping delay comfortably below the shortest idle timeout in front of Kestrel, for example Azure Container Apps ingress or another cloud L7 load balancer, so long-lived idle HTTP/2 tunnel connections are kept warm. The timeout controls how long each side waits for a ping acknowledgement before treating the HTTP/2 connection as broken and allowing the client reconnect loop to run.

A client that already owns a gRPC channel can let the tunnel transport reuse the same `CallInvoker` instead of creating and disposing an internal channel:

```csharp
builder.Services.AddSingleton(provider =>
    GrpcChannel.ForAddress("https://relay.example.com"));

builder.Services.AddReverseTunnelGrpcTransport(options =>
{
    options.CallInvokerFactory = provider =>
        provider.GetRequiredService<GrpcChannel>().CreateCallInvoker();
});
```

When `CallInvokerFactory` is configured, `GrpcTunnelClientTransport` does not dispose the external channel or invoker. Without the factory, the existing behavior remains: the transport creates and disposes its own `GrpcChannel`.

A server can expose WebSocket and gRPC tunnels on the same process and port during migration:

```csharp
builder.Services.AddTunnelForwarder();
builder.Services.AddReverseTunnelGrpcTransport();

var app = builder.Build();

app.MapTunnelHost();
app.MapReverseTunnelGrpcTransport();
app.MapTunnelForwarder();
app.Run();
```

HTTP/2 must be enabled on the endpoint that receives gRPC tunnel connections. TLS-enabled Kestrel endpoints normally negotiate HTTP/2 automatically through ALPN; if TLS is terminated upstream and Kestrel receives clear-text HTTP/2, configure h2c explicitly on that endpoint.

## Sample projects

The basic samples support both transports. WebSocket remains the default:

```bash
dotnet run --project samples/Sample.Server --launch-profile https
dotnet run --project samples/Sample.Client --launch-profile https
```

The gRPC sample uses the `grpc` launch profiles, which load `appsettings.Grpc.json` and select `TunnelTransportKind.Grpc` on the client:

```bash
dotnet run --project samples/Sample.Server --launch-profile grpc
dotnet run --project samples/Sample.Client --launch-profile grpc
```

The sample server maps both `MapTunnelHost()` and `MapReverseTunnelGrpcTransport()`, so WebSocket and gRPC tunnel clients can connect to the same server process during migration. `Sample.Blazor` also has a `grpc` launch profile and can be reached through the sample server at `/arty/BlazorSample/`.

### Shared channel chat sample

`Sample.Chat.Server`, `Sample.Chat.Client`, and `Sample.Chat.Shared` demonstrate a client app that reuses one `GrpcChannel` for the ReverseTunnel gRPC transport and normal chat gRPC services. The sample intentionally keeps the streams separate:

- `TunnelTransport.Connect` is the long-running bidirectional ReverseTunnel transport stream.
- `ChatStreamService.Connect` is a second bidirectional gRPC stream for chat events.
- `ChatRoomService` and `ChatMessageService` calls are regular unary gRPC calls.

All of them can use the same `GrpcChannel` / HTTP/2 connection pool when the client configures `ReverseTunnelGrpcTransportOptions.CallInvokerFactory`. Creating multiple `GrpcChannel` instances can create multiple HTTP/2/TCP connection pools. Shared channel usage is optional and does not merge chat traffic into the ReverseTunnel stream.

Resilience stays layered:

- the `GrpcChannel` should normally be registered as a singleton and configured with HTTP/2 keepalive and connection timeout settings;
- the ReverseTunnel client reconnect loop owns recovery for `TunnelTransport.Connect`;
- application-owned bidirectional streams need their own reconnect loop, because gRPC cannot transparently resume a broken stream;
- unary calls should use deadlines. Automatic gRPC retry policy is best limited to safe/idempotent calls unless write operations carry request ids or another duplicate-detection mechanism.
## Replica-set mode

A tunnel has exactly one owner instance. The owner is the server replica that accepted the long-running tunnel connection. Other replicas do not forward individual frames. Instead, if an HTTP request reaches a non-owner instance, that instance resolves the owner through `ITunnelRegistry` and forwards the whole HTTP request to the owner instance.

```text
Request lands on instance B
B does not have the local tunnel
B resolves TunnelId in ITunnelRegistry
Registry returns instance A and its internal endpoint
B forwards the HTTP request to A
A sends it through its local tunnel
Response flows back through B
```

The built-in `InMemoryTunnelRegistry` is for single-instance mode and tests. Multi-replica deployments should provide a distributed implementation, for example Redis, using the `ITunnelRegistry` abstraction. Registrations include tunnel id, instance id, internal endpoint, transport kind, last seen, expiry time, and optional connection id.

Configure each replica with a stable id and an internal endpoint reachable by other replicas:

```json
{
  "ReverseTunnel": {
    "InstanceId": "tunnel-server-1",
    "InternalEndpoint": "https://tunnel-server-1:8080",
    "RegistryTtl": "00:02:00"
  }
}
```

For Kubernetes, use a pod name or StatefulSet identity for `InstanceId` and an internal service DNS name for `InternalEndpoint`. For Aspire, expose the server project replicas and register a distributed `ITunnelRegistry` implementation, for example a Redis-backed registry. See also the [Aspire replica-set sketch](../samples/Aspire/README.md).

```csharp
var redis = builder.AddRedis("redis");

builder.AddProject<Projects.Sample_Server>("tunnel-server")
    .WithReference(redis)
    .WithReplicas(3);
```

## Limitations

There is no frame-level load balancing or backplane. A tunnel is owned by one server instance at a time. The built-in registry is in-memory only; production multi-replica deployments must provide a distributed `ITunnelRegistry`. Stale registry entries are treated as missing owners and can be removed by the registry implementation. Internal forwarding uses `X-ReverseTunnel-Forwarded-By` for loop prevention.
