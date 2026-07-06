# Changelog

All notable changes to this project will be documented in this file.

This format is based on Keep a Changelog and follows Semantic Versioning principles.

## [Unreleased]

### Added
- Transport abstraction (`ITunnelClientTransport` / `ITunnelServerTransport`) with WebSocket lifted onto it; WebSocket stays the default. Transport is selectable via `ReverseTunnelOptions.Transport` / `TunnelClientOptions.Transport` or configuration.
- Optional gRPC tunnel transport in `ReverseTunnel.Yarp.Grpc` using bidirectional streaming (`AddReverseTunnelGrpcTransport` / `MapReverseTunnelGrpcTransport`). A server can expose WebSocket and gRPC tunnels on the same host/port for gradual migration.
- Shared gRPC channel support: `ReverseTunnelGrpcTransportOptions.CallInvokerFactory` lets the tunnel transport reuse an application-owned `GrpcChannel`/`CallInvoker` (and does not dispose it).
- Replica-set foundation: `ITunnelRegistry` with `InMemoryTunnelRegistry`, per-instance `InstanceId`/`InternalEndpoint`, and owner-instance HTTP forwarding (`InternalTunnelRequestForwarder`) with `X-ReverseTunnel-Forwarded-By` loop prevention.
- `samples/Sample.Chat.*`: shared-channel chat sample (ReverseTunnel gRPC transport plus unary and bidirectional chat gRPC services on one client `GrpcChannel`) with a Blazor UI.
- `samples/Migration`: a server offering WebSocket + gRPC simultaneously with a legacy WebSocket client and a modern gRPC client, demonstrating a client-by-client migration path.
- Documentation: `docs/transports-and-replicas.md` and sample READMEs covering transport selection, HTTP/2 requirements, shared channel, and replica-set mode.
- Copilot instructions file with coding, changelog, and release standards.
- `CHANGELOG.md` with policy and baseline entry.
- Unit test project `tests/ReverseTunnel.Yarp.Tests` covering `TunnelClientManager` reconnect feedback loop fix, `WorkerWithBackoff`, `TunnelClientOptionsStore`, `TunnelCollection`, `HttpContextExtensions`, and `TunnelClientOptions`.
- `InternalsVisibleTo("ReverseTunnel.Yarp.Tests")` on the main assembly so tests can access `internal` state without polluting the public API.
- CI workflow: publish test results via `dorny/test-reporter`.

### Changed
- CI workflow: restricted push/PR triggers to code-relevant paths (including `tests/**`) to avoid unnecessary runs.
- Publish workflow: replaced third-party `ncipollo/release-action` with native `gh release create --generate-notes`. Added `fetch-depth: 0` and NBGV setup so version is computed correctly from git history. Removed manual `Version` override.
- `version.json`: extended `publicReleaseRefSpec` to include tags (`refs/tags/v*`) so NBGV produces stable versions when building from a release tag.
- `TunnelClientManager`: `State` and `ActiveClient` are now `internal` properties (via `InternalsVisibleTo`) to support test assertions without changing the public API.

### Fixed
- Tunnel registry renewal: the owner instance now renews its registration at half the `RegistryTtl` while a tunnel is connected, and `InMemoryTunnelRegistry.RenewAsync` slides the expiry window forward. Long-running tunnels no longer expire from the registry, which previously broke cross-instance forwarding after `RegistryTtl`.
- `TunnelClientManager`: credentials-only options update (e.g. JWT refresh) while already connected no longer tears down and re-establishes the connection, preventing an infinite reconnect feedback loop.
- `TunnelClientManager`: replaced tunnel's `Completion` callback now guards against the stale tunnel incorrectly resetting state to `Disconnected` after a newer tunnel has taken over.

### Removed

### Security

## [0.5.2] - 2026-04-13

### Added
- Initial changelog baseline.
- Defined release-note categories for future entries.

## Changelog Policy

- Add entries under [Unreleased] as part of each pull request.
- Keep entries user-facing and concise.
- Group entries by Added, Changed, Fixed, Deprecated, Removed, and Security.
- Move [Unreleased] entries to a version section when creating a release tag.
- Use release tags in `vX.Y.Z` format to match publishing workflow conventions.
