# Multi-hop / Chained Routing

You can compose ReverseTunnel.Yarp with another reverse-proxy hop when you need to reach a second on-prem web app through the first on-prem app.

## Common Setup

1. SaaS app: `AddTunnelForwarder` + `MapTunnelHost`
2. On-prem gateway app: `AddTunnelListener` + `AddTunnelClient` (connects to SaaS)
3. On-prem gateway app exposes an additional route (for example via YARP or custom middleware) that proxies to another internal app reachable on the local network.

In this setup the SaaS app still uses a single relay tunnel to the on-prem gateway, while the gateway handles the second hop on the local network. If the second system is not directly reachable, you can also run a second ReverseTunnel.Yarp tunnel and route by path/tunnel id (for example with `TunnelPathPrefixTransformer`).

## Sample Implementation

A runnable sample is available under `samples/Aggregation`:

- `Sample.Aggregate.Cloud`: cloud endpoint (`MapTunnelHost`) for incoming on-prem tunnel connections.
- `Sample.Aggregate.Client`: on-prem app that opens the tunnel to cloud.
- `Sample.Aggregate.OnPrem`: on-prem gateway that exposes `/gateway` (own UI/API) and uses YARP reverse proxy for `/internal/*` with a `/internal` path-prefix removal transform.
- `Sample.Aggregate.InternalApp`: downstream on-prem app reachable from the gateway over local network.

Run all four samples and call:

- `https://localhost:7400/client` -> forwarded over tunnel to `Sample.Aggregate.Client`.
- `https://localhost:7400/internal` -> forwarded over tunnel to gateway, then proxied to `Sample.Aggregate.InternalApp`.

For a runtime-configurable UI sample, see `samples/Sample.Blazor`:

- Open `/gateway-routes` to manage route entries in-memory (add/edit/delete).
- A default route maps `/gateway/internal/*` to `http://localhost:5600/*` and strips `/internal` before forwarding.
- You can configure path-prefix behavior (`StripPrefix` on/off), destination base URL, and enable/disable routes at runtime.

## Chained Proxy Guidance for Third-Party Apps

When the downstream app is third-party and cannot be modified, path/base-url issues are the most common source of broken CSS/JS, redirects, and login flows.

### Typical Options

- **Root mapping (preferred):** map a dedicated host/subdomain directly to the downstream app without a path prefix.
  - Example: `legacy.example.com/*` -> downstream `/`
  - Benefit: avoids most HTML/link rewrite problems.
- **Prefix mapping:** expose downstream under a prefix (for example `/internal/*`) and remove the prefix before proxying.
  - Example (YARP): `PathRemovePrefix=/internal`
  - Benefit: works on a single host, but may require extra handling for redirects/cookies/absolute URLs.

### Important Behavior to Validate

1. **Forwarded headers** (`X-Forwarded-Proto`, `X-Forwarded-Host`, `X-Forwarded-Prefix`)
   - Needed so generated redirects/callback links use the public URL.
2. **Redirect handling**
   - If upstream returns `Location: /login`, ensure clients resolve to `/internal/login` when using prefix mapping (or prefer host-based mapping).
3. **Cookie path/domain**
   - Auth cookies from upstream may need path/domain rewrite to remain valid behind the gateway prefix/host.
4. **Absolute URLs in HTML/JS**
   - Apps emitting absolute `/...` links can bypass your prefix unless app supports base-path config.
5. **WebSockets/SSE/streaming uploads**
   - Ensure proxy timeouts and upgrade handling are enabled end-to-end.

### If You Control the Downstream App

- Configure forwarded headers (`UseForwardedHeaders`) so scheme/host/prefix are honored.
- Configure base path correctly (for Blazor, dynamic `<base href>` is often required as documented in [Blazor Support](blazor-support.md)).

### If You Do NOT Control the Downstream App

- Prefer dedicated host mapping over path-prefix mapping when possible.
- Keep rewrites minimal and predictable (strip prefix, preserve host/proto headers).
- Use path-prefix only when infrastructure constraints require it.
