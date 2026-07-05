# Aspire replica-set sketch

This repository does not include a buildable Aspire AppHost project. The tunnel pieces are prepared so an AppHost can run several server replicas, but production multi-replica mode still needs a distributed `ITunnelRegistry` implementation.

A typical AppHost shape looks like this:

```csharp
var builder = DistributedApplication.CreateBuilder(args);

var registry = builder.AddRedis("tunnel-registry");

var tunnelServer = builder.AddProject<Projects.Sample_Server>("tunnel-server")
    .WithReference(registry)
    .WithEnvironment("ReverseTunnel__Transport", "Grpc")
    .WithEnvironment("ReverseTunnel__RegistryTtl", "00:02:00")
    .WithReplicas(3);

builder.AddProject<Projects.Sample_Client>("tunnel-client")
    .WithReference(tunnelServer)
    .WithEnvironment("ReverseTunnel__Transport", "Grpc");

builder.Build().Run();
```

Each server replica needs a stable `ReverseTunnel:InstanceId` and an `ReverseTunnel:InternalEndpoint` reachable by the other replicas. In Kubernetes this is commonly derived from the pod identity and an internal service DNS name. In Aspire, wire those values through environment variables or service discovery, then register a Redis-backed implementation of `ITunnelRegistry` in the server process.
