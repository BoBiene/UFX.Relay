# Migration sample: WebSocket + gRPC side by side

This sample shows how a single tunnel server can offer the **WebSocket** and the **gRPC**
transport at the same time, so an existing fleet of clients can be migrated one client at a
time without breaking the clients that are still on WebSocket.

```text
                        Migration.Server (https://localhost:7400)
                        ├─ MapTunnelHost()                 WebSocket transport
                        ├─ MapReverseTunnelGrpcTransport() gRPC transport
                        └─ MapTunnelForwarder()            /tunnel/{tunnelId}/...
                              ▲                         ▲
        WebSocket tunnel      │                         │      gRPC tunnel
        (unchanged old code)  │                         │      (new code)
                              │                         │
      Migration.LegacyClient ─┘                         └─ Migration.ModernClient
      tunnelId "legacy"                                    tunnelId "modern"
      (no gRPC package reference)                          (Transport = Grpc)
```

## Why this works

* `MapTunnelHost()` and `MapReverseTunnelGrpcTransport()` both feed the same
  `ITunnelHostManager` and tunnel registry. Enabling gRPC is purely additive - the WebSocket
  transport keeps working exactly as before.
* Kestrel serves HTTP/1.1 (WebSocket) and HTTP/2 (gRPC) on the same HTTPS port via ALPN, so
  no extra port or endpoint is needed.
* A tunnel has exactly one owner instance regardless of transport, so the forwarder resolves
  and forwards requests the same way for legacy and modern clients.

`Migration.LegacyClient` deliberately references **only** `ReverseTunnel.Yarp` (no gRPC
package). That is the proof that an unchanged client in the field keeps working after the
server adds gRPC.

## Run it

Start all three projects (three terminals):

```bash
dotnet run --project samples/Migration/Migration.Server
dotnet run --project samples/Migration/Migration.LegacyClient
dotnet run --project samples/Migration/Migration.ModernClient
```

Then reach each client through the tunnel on the same server:

```bash
# Legacy client over the WebSocket transport
curl -k https://localhost:7400/tunnel/legacy/hello

# Modern client over the gRPC transport
curl -k https://localhost:7400/tunnel/modern/hello

# Which transports the server exposes
curl -k https://localhost:7400/transports
```

Both requests are served at the same time by the same server - one client over WebSocket, the
other over gRPC.

## Migration checklist for a real fleet

1. Deploy a server build that calls both `MapTunnelHost()` and
   `MapReverseTunnelGrpcTransport()`. Existing WebSocket clients keep connecting unchanged.
2. Roll out new clients (or update existing ones) with
   `AddReverseTunnelGrpcTransport()` and `Transport = TunnelTransportKind.Grpc`.
3. Once every client has moved to gRPC you may drop `MapTunnelHost()` - but there is no need
   to rush; keeping both mapped indefinitely is fully supported.
