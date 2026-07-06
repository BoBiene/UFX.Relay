# Samples

The basic sample pair can run with the default WebSocket tunnel or with the optional gRPC transport.

## WebSocket sample

Start the server and client with the existing HTTPS profiles:

```bash
dotnet run --project samples/Sample.Server --launch-profile https
dotnet run --project samples/Sample.Client --launch-profile https
```

Then call the server endpoint that is forwarded through the tunnel:

```bash
curl https://localhost:7200/arty/123/client
```

## gRPC sample

The `grpc` launch profiles set `ASPNETCORE_ENVIRONMENT=Grpc`, which loads `appsettings.Grpc.json` in both projects.

```bash
dotnet run --project samples/Sample.Server --launch-profile grpc
dotnet run --project samples/Sample.Client --launch-profile grpc
```

The server maps both `MapTunnelHost()` and `MapReverseTunnelGrpcTransport()` on the same Kestrel process. The client selects `TunnelTransportKind.Grpc` from configuration and connects to `https://localhost:7200`.

Useful checks:

```bash
curl https://localhost:7200/transport
curl https://localhost:7100/transport
curl https://localhost:7200/arty/123/client
```

## Blazor gRPC sample

The Blazor sample can also act as the tunnel client. Its `grpc` profile loads `appsettings.Grpc.json`, enables the tunnel, and selects the gRPC transport.

```bash
dotnet run --project samples/Sample.Server --launch-profile grpc
dotnet run --project samples/Sample.Blazor --launch-profile grpc
```

Then open or curl the Blazor app through the tunnel:

```bash
curl https://localhost:7200/arty/BlazorSample/
```

## Shared gRPC channel chat sample

The chat sample shows ReverseTunnel.Yarp gRPC transport next to regular gRPC services on one shared client-side `GrpcChannel`.

```bash
dotnet run --project samples/Sample.Chat.Server --launch-profile https
dotnet run --project samples/Sample.Chat.Client --launch-profile https
```

Open the client UI at `https://localhost:7301`. The client creates one shared `GrpcChannel` to `https://localhost:7300` and uses it for:

- `TunnelTransport.Connect`, the long-running ReverseTunnel bidirectional stream.
- `ChatRoomService.CreateRoom` and `ChatRoomService.ListRooms`, normal unary gRPC calls.
- `ChatMessageService.SendMessage`, a normal unary gRPC call.
- `ChatStreamService.Connect`, a second normal bidirectional gRPC stream for chat events.

`TunnelTransport.Connect` and `ChatStreamService.Connect` are separate bidirectional streams. They can share the same `GrpcChannel` and HTTP/2 connection pool, but chat events are not carried in `TunnelFrame.payload`.

The client sample also includes the resilience pieces expected for a long-running gRPC client:

- the shared `GrpcChannel` is a singleton with HTTP/2 keepalive and connection timeout settings;
- the ReverseTunnel listener reconnects the tunnel stream when `TunnelTransport.Connect` ends;
- the chat UI owns a separate stream supervisor that reopens `ChatStreamService.Connect` with bounded exponential backoff and jitter;
- chat stream writes are serialized and time-boxed, because gRPC request-stream writes must not run concurrently;
- only safe read calls (`ListRooms`, `GetRecentMessages`) use automatic gRPC retry policy. Write calls such as `CreateRoom` and `SendMessage` use deadlines and explicit error reporting; production write retries should include request ids or another idempotency strategy.

The server also forwards `/client-info` through ReverseTunnel to the client app. Use the UI button or call:

```bash
curl https://localhost:7300/client-info
```
## Replica-set note

The built-in `InMemoryTunnelRegistry` is meant for single-instance samples and tests. For a real multi-replica Aspire or Kubernetes deployment, register a distributed `ITunnelRegistry` implementation, set a stable `ReverseTunnel:InstanceId` per replica, and set `ReverseTunnel:InternalEndpoint` to the replica endpoint that other replicas can reach. See [Aspire replica-set sketch](Aspire/README.md).
