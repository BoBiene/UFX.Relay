# Changelog

All notable changes to this project will be documented in this file.

This format is based on Keep a Changelog and follows Semantic Versioning principles.

## [Unreleased]

### Added
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
- `TunnelClientManager`: credentials-only options update (e.g. JWT refresh) while already connected no longer tears down and re-establishes the connection, preventing an infinite reconnect feedback loop.
- `TunnelClientManager`: replaced tunnel's `Completion` callback now guards against the stale tunnel incorrectly resetting state to `Disconnected` after a newer tunnel has taken over.
- `TunnelClientManager`: a credentials-only options update while **disconnected** no longer restarts the reconnect worker. Previously it reset the exponential backoff, so a repeated credentials refresh during an outage retried at the initial interval instead of backing off. Refreshed headers are still applied on the next scheduled attempt.
- `WorkerWithBackoff`: `Reset()` now hands the outgoing worker to its replacement as a predecessor, and the replacement awaits it before running the work function. This guarantees two worker generations never execute concurrently, so a superseded connection attempt can no longer race a fresh one (which could open a second connection with the same tunnel id).
- `TunnelClientManager`: the client `WebSocket` is now disposed on every connection path that does not hand it to a `TunnelClient` (connect failure, cancellation, or a failure during the multiplexing handshake), preventing a connected-but-unowned socket from leaking.
- `TunnelClientManager`: `TryFetchErrorResponseBodyAsync` now honors the reconnect cancellation token and caps its HTTP timeout, so a superseded attempt cannot linger on an unreachable endpoint for the default 100s.
- `TunnelClientManager`: a connection attempt that was cancelled because it was superseded no longer overwrites the connection state or disposes the active tunnel established by the newer attempt.

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
