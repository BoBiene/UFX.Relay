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

## Replica-set note

The built-in `InMemoryTunnelRegistry` is meant for single-instance samples and tests. For a real multi-replica Aspire or Kubernetes deployment, register a distributed `ITunnelRegistry` implementation, set a stable `ReverseTunnel:InstanceId` per replica, and set `ReverseTunnel:InternalEndpoint` to the replica endpoint that other replicas can reach. See [Aspire replica-set sketch](Aspire/README.md).
