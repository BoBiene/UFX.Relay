# UFX.Relay

UFX.Relay connects two ASPNet Core Middleware pipelines using a single WebSocket connection therefor extending a cloud application to an on-premise application instance.
This is similar to services like [ngrok](https://ngrok.com) but rather than requiring an external 3rd party service, UFX.Relay is a self-contained pure ASPNet Core solution.

The sample [Client](samples/Sample.Client/Program.cs) and [Server](samples/Sample.Server/Program.cs) projects demonstrate how to use UFX.Relay to connect a cloud application to an on-premise application with simple association of agents using a TunnelId. 
A request to the server/forwarder with a TunnelId header will be forwarded to the corresponding client/listener that connects with the same TunnelId.

The Server/Forwarder end of UFX.Relay leverages [YARP](https://github.com/microsoft/reverse-proxy) to forward ASPNet Core requests to the on-premise application via the WebSocket connection.
At the lowest level [YARP](https://github.com/microsoft/reverse-proxy) converts a HTTPContext to a HTTPClientRequest and sends it to the on-premise application via the WebSocket connection which uses a [MultiplexingStream](https://github.com/dotnet/Nerdbank.Streams/blob/main/doc/MultiplexingStream.md) to allow multiple requests to be sent over a single connection.
Note: This implementation uses [YARP DirectForwarding](https://github.com/microsoft/reverse-proxy/blob/main/src/ReverseProxy/Forwarder/HttpForwarder.cs) to forward requests to the on-premise application, any [YARP](https://github.com/microsoft/reverse-proxy) cluster configuration will not be used.

## Overview

UFX.Relay comprises three components:
* Forwarder
* Listener
* Tunnel

### Forwarder
This uses [YARP DirectForwarding](https://github.com/microsoft/reverse-proxy/blob/main/src/ReverseProxy/Forwarder/HttpForwarder.cs) to forward requests over the tunnel connection to be received by the listener.

```csharp
var builder = WebApplication.CreateBuilder(args);
builder.Services.AddTunnelForwarder();
var app = builder.Build();
app.MapTunnelForwarder();
app.Run();
```

### Listener
The listener received requests over the tunnel from the forwarder and injects them into the ASPNet Core pipeline.

```csharp
var builder = WebApplication.CreateBuilder(args);
builder.WebHost.AddTunnelListener(options =>
{
    options.DefaultTunnelId = "123";
});
```

#### Reconnect backoff (optional)
If you expect repeated connection failures (e.g., temporary network issues or misconfiguration), you can enable exponential backoff for reconnect attempts. 
When enabled, the delay between reconnect attempts increases progressively after each failed attempt, up to a configurable maximum. 
Once a connection succeeds, the interval resets to the base `ReconnectInterval`.

```csharp
var builder = WebApplication.CreateBuilder(args);

builder.WebHost.AddTunnelListener(options =>
{
    options.DefaultTunnelId = "123";

    // Enable exponential backoff for reconnect attempts
    options.EnableReconnectBackoff = true;

    // Cap the maximum backoff delay (default: 2 minutes)
    options.MaxReconnectInterval = TimeSpan.FromMinutes(5);
});

```

### Tunnel
The Tunnel is a logical layer on top of a WebSocket connection that allows for multiple requests to be multiplexed over a single connection.
The tunnel has both a Client and Host end, the Forwarder and Listener can use either the Tunnel Client or Tunnel Host. Typically, the forwarder would be used with the Tunnel Host and the listener with the Tunnel Client for a ngrok replacement scenario.
However, if the Tunnel Client and Host are swapped this would allow for connection aggregation of multiple on-prem connections via a single connection to a cloud service.


#### Tunnel Client

The client requires the TunnelHost and TunnelId to be specified in order to connect to the Tunnel Host.

```csharp
var builder = WebApplication.CreateBuilder(args);
builder.Services.AddTunnelClient(options =>
    options with
    {
        TunnelHost = "wss://localhost:7400",
        TunnelId = "123"
    });
```

#### Tunnel Host

The tunnel host is added as a minimal api endpoint to the application pipeline accepting websocket connections on /tunnel/{tunnelId} by default.

```csharp
var app = builder.Build();
app.MapTunnelHost();
app.Run();
```


## Sample Projects

The sample projects demonstrate how to use UFX.Relay to connect a cloud application to an on-premise application with simple association of agents using a static RelayId, this in effect creates a static tunnel between the client and server.
Once the sample [Client](samples/Sample.Client/Program.cs) and [Server](samples/Sample.Server/Program.cs) projects have started requests to https://localhost:7200/ will be forwarded to the client application.

The sample server hosts on https://localhost:7200/ and the client hosts on https://localhost:7100.

The sample client opens a websocket connection to the server using wss://localhost:7200/relay/123

Example responses can be tested as follows:

A request to https://localhost:7200/server is handled by the server and a request to https://localhost:7200/client is forwarded to the client and returned via the server.

## Configuration

### Client

The minimal configuration for the client is as follows:

```csharp
builder.WebHost.AddTunnelListener(options => { options.DefaultTunnelId = "123"; });

builder.Services.AddTunnelClient(options =>
    options with
    {
        TunnelHost = "wss://localhost:7200"
    });

```
This will create a Kestrel Listener that will inject requests (from the forwarder) into the client ASPNet Core pipeline received over the WebSocket connection to the server (i.e. wss://localhost:7200) 

When a code based listener is added to Kestrel it will disable the use of the default Kestrel listener configuration derived from ASPNETCORE_URLS environment variable and -url command line argument.
If you require the default listener to be enabled you can set the includeDefaultUrls parameter to true as follows:

```csharp
builder.WebHost.AddTunnelListener(options =>
{
    options.DefaultTunnelId = "123";
}, includeDefaultUrls: true);
```
The sample uses a simple association of agents using a static TunnelId '123'

#### Blazor Support

When using UFX.Relay Client with a Blazor app behind a reverse proxy, additional configuration is needed.

##### Runtime Configuration

In `Program.cs`, configure forwarded headers:

```csharp
app.UseForwardedHeaders(new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.All,
    // Optional: Add KnownProxies / KnownNetworks for secure forwarding
});
```

In `App.razor`, ensure correct resolution of relative URLs:

```razor
<!--
We need to set the base, so that the app can resolve relative URLs correctly.
This needs to be done dynamically, as we can access the app via the reverse-proxy-tunnel and via the local http-endpoint
-->
<base href="@(NavigationManager.BaseUri)" />
```



### Server

The minimal configuration for the server is as follows:

```csharp
var builder = WebApplication.CreateBuilder(args);
builder.Services.AddTunnelForwarder();
var app = builder.Build();
app.MapTunnelHost();
app.Run();

```

Request sent to the server with a TunnelId header will be forwarded to the corresponding listener that connects with the same TunnelId.
If a DefaultTunnelId is set in the configuration then requests without a TunnelId header will be forwarded to the listener with the DefaultTunnelId.
Combining this with the DefaultTunnelId option in the listener configuration allows for simple association of an agent using a static TunnelId statically linking the forwarder and listener.

```csharp
var builder = WebApplication.CreateBuilder(args);
builder.Services.AddTunnelForwarder(options =>
{
    options.DefaultTunnelId = "123";
});
builder.Services.AddRelayHost();
var app = builder.Build();
app.MapTunnelHost();
app.Run();

```

It is also possible to use a transformer (courtesy of [YARP](https://github.com/microsoft/reverse-proxy)) to modify the behaviour of the Forwarder, the following example demonstrates how to use the transformer to enable the default forwarders (i.e. Forwarded-For):

```csharp
builder.Services.AddTunnelForwarder(options =>
{
    options.Transformer = transformBuilderContext =>
    {
        transformBuilderContext.UseDefaultForwarders = true;
    };
});
```

##### Using TunnelPathPrefixTransformer

To extract the TunnelId from a path segment and transform the request path:

```csharp
TunnelPathPrefixTransformer prefixTransformer = new TunnelPathPrefixTransformer("ufx");
builder.Services.AddTunnelForwarder(options =>
{
    options.DefaultTunnelId = "123";
    options.TunnelIdFromContext = prefixTransformer.GetTunnelIdFromContext;
    options.Transformer = context =>
    {
        // Remove /ufx/{tunnelId} from the request path before forwarding
        context.RequestTransforms.Add(prefixTransformer);
    };
});
```

This enables forwarding using URLs like `/ufx/{tunnelId}/`.

> [!IMPORTANT]  
> Ensure to set `UseForwardedHeaders` and `base` href in the Blazor app as mentioned above.

### Runtime TunnelClientOptions (Dynamic)


To configure `TunnelClientOptions` at runtime in Blazor Server, the preferred method is to use the built-in `ITunnelClientOptionsStore`. This allows you to declaratively modify the tunnel settings (such as `TunnelHost`, `TunnelId` and `IsEnabled`) based on user input or query parameters.

#### Example: `TunnelSetup.razor`

```razor
@inject ITunnelClientOptionsStore tunnelClientOptionsStore

// ...

@code {
    // ...

    protected override void OnInitialized()
    {
        var options = tunnelClientOptionsStore.Current;
        tunnelHost = options.TunnelHost;
        tunnelId = options.TunnelId;
        isEnabled = options.IsEnabled;
        // ...
    }

    
    private void Apply()
    {
        tunnelClientOptionsStore.Update(current =>
        {
            current.TunnelHost = tunnelHost;
            current.TunnelId = tunnelId;
            current.IsEnabled = isEnabled;
            return current;
        });
    }
}
```

This enables full user-driven control over the tunnel connection in the UI. The tunnel will automatically reconnect based on the updated settings.

### Monitoring Tunnel Connection State

The `ITunnelClientManager` interface provides information about the current tunnel connection, including real-time updates on state changes.

The available connection states are:
- `Disconnected`
- `Connecting`
- `Connected`
- `Error`

Additionally, you can access the last connection error message (if any) and react to state changes.

#### Example: UI Binding

```razor
@inject ITunnelClientManager tunnelClientManager

<div>
    <strong>Connection State:</strong> @connectionState.ToString()
</div>

@if (!string.IsNullOrEmpty(tunnelClientManager.LastConnectErrorMessage))
{
    <div class="text-danger">
        <strong>Message: </strong>@tunnelClientManager.LastConnectErrorMessage
    </div>
}

@code {
    private TunnelConnectionState connectionState;

    protected override void OnInitialized()
    {
        connectionState = tunnelClientManager.ConnectionState;
        tunnelClientManager.ConnectionStateChanged += OnConnectionStateChanged;
    }

    private void OnConnectionStateChanged(object? sender, TunnelConnectionState newState)
    {
        connectionState = newState;
        InvokeAsync(StateHasChanged);
    }

    public void Dispose()
    {
        tunnelClientManager.ConnectionStateChanged -= OnConnectionStateChanged;
    }
}
```

This allows your Blazor components to reflect the live tunnel status, providing a reactive and user-friendly experience.

### Websocket Authentication

#### Client

The client WebSocket can be configured for authentication, for example setting an Authorization header to authenticate the WebSocket connection to the server as follows:

```csharp
builder.Services.AddTunnelClient(options =>
{
    Action<ClientWebSocketOptions> socketOptions = wsOptions =>
    {
        wsOptions.SetRequestHeader("Authorization", "ApiKey 123");
    };
    return options with
    {
        WebSocketOptions = socketOptions,
        TunnelHost = "wss://localhost:7200",
        TunnelId = "123"
    };
});
```

Or if you also want to get the response body from the faulty WebSocket connection with LastErrorResponseBody, you must specify the authorization header as follows:

```csharp
builder.Services.AddTunnelClient(options =>
{
    Dictionary<string, string> requestHeaders = new()
    {
        { "Authorization", "ApiKey 123" }
    };
    return options with
    {
        RequestHeaders = requestHeaders,
        TunnelHost = "wss://localhost:7200",
        TunnelId = "123"
    };
});
```

#### Server

The Tunnel Host can be configured to require Authentication for the WebSocket connection from the Tunnel Client using standard Minimal API middleware configuration as follows:

```csharp
app.MapTunnelHost().RequireAuthorization();
```

### Detecting Tunnel vs Normal HTTP Requests

In scenarios where the client application receives requests through both the tunnel (from the forwarder) and normal HTTP endpoints, you can use middleware to distinguish between them. This is particularly useful for implementing forward-auth where you trust headers like `x-User` only when they come through the tunnel.

All requests received through the tunnel are marked with an `ITunnelRequestFeature`. You can use the `IsFromTunnel()` extension method to check if a request came through the tunnel:

```csharp
app.Use(async (context, next) =>
{
    if (context.IsFromTunnel())
    {
        // Request came through the tunnel - trusted connection
        // Safe to use x-User header for authentication
        var user = context.Request.Headers["x-User"].ToString();
        if (!string.IsNullOrEmpty(user))
        {
            // Set user identity based on trusted header
            // ... your authentication logic here
        }
    }
    else
    {
        // Request came through normal HTTP endpoint - untrusted
        // Reject x-User header or require standard authentication
        context.Request.Headers.Remove("x-User");
    }
    
    await next(context);
});
```

You can also access the tunnel feature directly to get additional information like the tunnel ID:

```csharp
var tunnelFeature = context.GetTunnelRequestFeature();
if (tunnelFeature != null)
{
    var tunnelId = tunnelFeature.TunnelId;
    // ... use tunnel information
}
```

## Connection Aggregation
Connection aggregation helps when there are a large number of idle connections (such as WebSockets) that need to be maintained.
[Azure Web PubSub](https://azure.microsoft.com/en-gb/products/web-pubsub) is an example of a cloud service that provides WebSocket connection aggregation.
Inverting the WebSocket connection direction of UFX.Relay provides an equivalent capability to [Azure Web PubSub](https://azure.microsoft.com/en-gb/products/web-pubsub) but with the added benefit of being self-contained and not requiring a 3rd party service.
Typically, the Forwarder would be hosted on the cloud and the listener on-prem, this allows for the cloud application to connect to the on-prem application.
However, it is possible to have the forwarder on-prem and the listener in the cloud while still using an out-bound WebSocket connection from the on-prem instance to the cloud thus allowing for connection aggregation of multiple on-prem connections via a single connection to a cloud service.

### Aggregation Cloud End Example

```csharp
var builder = WebApplication.CreateBuilder(args);
builder.WebHost.AddTunnelListener(options =>{ options.DefaultTunnelId = "123"; });
var app = builder.Build();
app.MapTunnelHost();
app.Run();
```

### Aggregation On-Prem End Example

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

### Multi-hop / chained routing

You can compose UFX.Relay with another reverse-proxy hop when you need to reach a second on-prem web app through the first on-prem app.

Common setup:

1. SaaS app: `AddTunnelForwarder` + `MapTunnelHost`
2. On-prem gateway app: `AddTunnelListener` + `AddTunnelClient` (connects to SaaS)
3. On-prem gateway app exposes an additional route (for example via YARP or custom middleware) that proxies to another internal app reachable on the local network.

In this setup the SaaS app still uses a single relay tunnel to the on-prem gateway, while the gateway handles the second hop on the local network.
If the second system is not directly reachable, you can also run a second UFX.Relay tunnel and route by path/tunnel id (for example with `TunnelPathPrefixTransformer`).

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

### Chained proxy guidance for third-party apps (no app changes possible)

When the downstream app is third-party and cannot be modified, path/base-url issues are the most common source of broken CSS/JS, redirects, and login flows.

Typical options:

- **Root mapping (preferred):** map a dedicated host/subdomain directly to the downstream app without a path prefix.
  - Example: `legacy.example.com/*` -> downstream `/`
  - Benefit: avoids most HTML/link rewrite problems.
- **Prefix mapping:** expose downstream under a prefix (for example `/internal/*`) and remove the prefix before proxying.
  - Example (YARP): `PathRemovePrefix=/internal`
  - Benefit: works on a single host, but may require extra handling for redirects/cookies/absolute URLs.

Important behavior to validate in chained scenarios:

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

If you control the downstream app (for example Blazor):

- Configure forwarded headers (`UseForwardedHeaders`) so scheme/host/prefix are honored.
- Configure base path correctly (for Blazor, dynamic `<base href>` is often required as documented above).

If you **do not** control the downstream app:

- Prefer dedicated host mapping over path-prefix mapping when possible.
- Keep rewrites minimal and predictable (strip prefix, preserve host/proto headers).
- Use path-prefix only when infrastructure constraints require it.


## Load Balancing with Multiple Server Instances

### Understanding Connection Aggregation vs. Load Balancing

It's important to distinguish between two different scaling scenarios:

1. **Connection Aggregation** (demonstrated in `samples/Aggregation`): Multiple on-prem clients connecting to a single cloud instance to reduce the number of connections.
2. **Load Balancing** (this section): Multiple cloud server instances (horizontal scaling) to handle increased traffic and provide high availability.

The Aggregation example shows how to efficiently manage many tunnel connections by inverting the tunnel direction. However, it does **not** demonstrate load balancing across multiple server instances.

### Load Balancing Architecture Patterns

When deploying multiple instances of the UFX.Relay forwarder/server for load balancing, you need to handle the fact that WebSocket connections are stateful and tied to a specific server instance.

### Critical: Understanding Tunnel Routing in Load-Balanced Scenarios

When load balancing UFX.Relay, it's essential to understand that there are **two different types of connections**:

1. **Tunnel Client WebSocket Connection**: On-prem application establishes WebSocket connection to a forwarder instance
2. **End-User HTTP Requests**: End users making HTTP requests that need to be forwarded through the tunnel

**The Routing Challenge:**
```
Scenario:
- Tunnel "A" WebSocket connects to Instance 1
- End-user HTTP request for Tunnel "A" arrives at Instance 2
- Problem: Instance 2 doesn't have Tunnel "A" → Returns 404

Current UFX.Relay behavior:
Instance 1: Has Tunnel "A" (ConcurrentDictionary lookup)
Instance 2: Looks for Tunnel "A" locally → Not found → 404 Not Found
```

**Important:** UFX.Relay currently uses **local, in-process tunnel registry** (`ConcurrentDictionary<string, Tunnel>`). Each instance only knows about tunnels connected to itself. There is **no built-in cross-instance tunnel lookup**.

### Solutions for Cross-Instance Tunnel Routing

#### Pattern 1: Sticky Sessions for BOTH Connection Types (Recommended for Most Cases)

The simplest and most effective approach is to use **sticky sessions** (session affinity) at your load balancer level for **both** the tunnel WebSocket connection AND end-user HTTP requests. This ensures:
- Tunnel clients always connect to the same instance
- End-user requests for a specific tunnel are routed to the same instance

**Two approaches to implement sticky sessions:**

**A) TunnelId-Based Routing (Preferred)**

Route both WebSocket and HTTP requests based on the TunnelId, ensuring they land on the same instance:

```nginx
# NGINX example with hash-based routing on TunnelId
upstream relay_servers {
    # Hash on tunnelId from URL or header
    hash $arg_tunnelid consistent;  # For query param: ?tunnelid=123
    # Or: hash $http_x_tunnel_id consistent;  # For header: X-Tunnel-Id
    
    server relay1.example.com:443;
    server relay2.example.com:443;
    server relay3.example.com:443;
}

server {
    listen 443 ssl;
    server_name relay.example.com;
    
    location / {
        proxy_pass https://relay_servers;
        proxy_http_version 1.1;
        proxy_set_header Upgrade $http_upgrade;
        proxy_set_header Connection "upgrade";
        # ... other headers
    }
}
```

**B) IP-Based Routing (Simple but Limited)**

Route based on client IP address:

```nginx
upstream relay_servers {
    ip_hash;  # Both tunnel client and end users from same IP → same instance
    server relay1.example.com:443;
    server relay2.example.com:443;
    server relay3.example.com:443;
}
```

**Limitations of IP-Based:**
- Only works if tunnel client and end users come from the same IP/NAT
- Won't work for public APIs where users come from different IPs than the tunnel client

**Architecture:**
```
Internet
    ↓
Load Balancer (with sticky sessions enabled)
    ├─→ Forwarder Instance 1 (handles tunnels: A, B, C)
    ├─→ Forwarder Instance 2 (handles tunnels: D, E, F)
    └─→ Forwarder Instance 3 (handles tunnels: G, H, I)
        ↑
        └─ On-Prem Clients (connect via WebSocket)
```

**Load Balancer Configuration Examples:**

**NGINX (TunnelId-based - Recommended):**
```nginx
upstream relay_servers {
    # Hash on tunnelId query parameter
    hash $arg_tunnelid consistent;
    server relay1.example.com:443;
    server relay2.example.com:443;
    server relay3.example.com:443;
}
```

**NGINX (IP-based - Only if tunnel client and users share same IP):**
```nginx
upstream relay_servers {
    ip_hash;  # Only works if same source IP for both connections
    server relay1.example.com:443;
    server relay2.example.com:443;
}
```

**HAProxy (TunnelId-based - Recommended):**
```haproxy
backend relay_backend
    balance url_param tunnelid
    hash-type consistent
    server forwarder1 relay1.example.com:443
    server forwarder2 relay2.example.com:443
```

**Cloud Platform Options:**
- **Azure Application Gateway**: Configure path-based or header-based routing rules
- **AWS Application Load Balancer**: Use target group stickiness on custom header/parameter
- **Kubernetes**: Use Ingress with session affinity annotations
- **Traefik**: Configure hash-based load balancing on header or parameter

**Advantages:**
- Simple to implement
- No code changes required
- Works with standard load balancers
- Minimal overhead

**Considerations:**
- If a server instance fails, clients connected to it must reconnect and may be routed to a different instance
- Uneven distribution if some tunnels are much more active than others
- The UFX.Relay client has built-in reconnection logic with exponential backoff to handle instance failures

#### Pattern 2: Distributed State with Service Discovery (Advanced)

For more sophisticated scenarios requiring active-active failover or dynamic tunnel migration, you can use a distributed state management solution.

**Architecture:**
```
Internet
    ↓
Load Balancer (round-robin or least-connections)
    ├─→ Forwarder Instance 1 ←→ Redis/Orleans
    ├─→ Forwarder Instance 2 ←→ Redis/Orleans
    └─→ Forwarder Instance 3 ←→ Redis/Orleans
        ↑                          ↑
        └─ On-Prem Clients         └─ Shared Tunnel Registry
```

#### Pattern 2: Distributed State with Cross-Instance Routing (Advanced)

For scenarios where sticky sessions are not possible or practical (e.g., end users come from different IPs than tunnel clients, or you need true active-active failover), you need a distributed state solution.

**This pattern solves the critical routing problem:**
- Tunnel A connects to Instance 1
- User request for Tunnel A arrives at Instance 2
- Instance 2 looks up in shared state: "Tunnel A is on Instance 1"
- Instance 2 either: (a) redirects the request, or (b) proxies to Instance 1

**Architecture:**
```
End User Request (for Tunnel A)
    ↓
Load Balancer (round-robin) → Can land on ANY instance
    ↓
Instance 2 (doesn't have Tunnel A locally)
    ↓
Redis Lookup: "Where is Tunnel A?"
    → Redis: "Tunnel A is on Instance 1"
    ↓
Option A: HTTP Redirect to Instance 1
Option B: Internal Proxy to Instance 1
    ↓
Instance 1 (has Tunnel A WebSocket)
    ↓
Forward through Tunnel A to on-prem
```

**Implementation Options:**

**Option A: Custom ITunnelCollectionProvider with Redis + HTTP Redirect**

This approach uses Redis to store tunnel-to-instance mappings and redirects requests to the correct instance.

```csharp
public class DistributedTunnelCollectionProvider : ITunnelCollectionProvider
{
    private readonly IConnectionMultiplexer _redis;
    private readonly ITunnelCollection _localTunnelCollection;
    private readonly string _instanceId;
    private readonly string _instanceUrl; // e.g., "https://instance1.example.com"
    
    public async Task<ITunnelCollection> GetTunnelCollectionAsync(
        HttpContext context, 
        CancellationToken cancellationToken)
    {
        var tunnelId = await GetTunnelIdFromContext(context);
        
        // Check Redis for tunnel location
        var instanceId = await _redis.GetDatabase()
            .StringGetAsync($"tunnel:{tunnelId}:instance");
            
        if (instanceId.IsNullOrEmpty || instanceId == _instanceId)
        {
            // Tunnel is on this instance or not connected
            return _localTunnelCollection;
        }
        else
        {
            // Tunnel is on another instance - get its URL
            var instanceUrl = await _redis.GetDatabase()
                .StringGetAsync($"instance:{instanceId}:url");
            
            // Redirect client to the correct instance
            context.Response.StatusCode = StatusCodes.Status307TemporaryRedirect;
            context.Response.Headers["Location"] = 
                $"{instanceUrl}{context.Request.Path}{context.Request.QueryString}";
            
            // Return null to skip tunnel forwarding
            return null;
        }
    }
}

// When tunnel connects, register it in Redis
public class DistributedTunnelHostManager : ITunnelHostManager
{
    public async Task<ITunnel?> OnTunnelConnectedAsync(string tunnelId)
    {
        // Register tunnel location in Redis
        await _redis.GetDatabase().StringSetAsync(
            $"tunnel:{tunnelId}:instance", 
            _instanceId,
            expiry: TimeSpan.FromMinutes(5)  // Refresh with heartbeat
        );
        
        // Register instance URL
        await _redis.GetDatabase().StringSetAsync(
            $"instance:{_instanceId}:url",
            _instanceUrl,
            expiry: TimeSpan.FromMinutes(5)
        );
        
        return await base.OnTunnelConnectedAsync(tunnelId);
    }
    
    public async Task OnTunnelDisconnectedAsync(string tunnelId)
    {
        // Remove tunnel from Redis
        await _redis.GetDatabase().KeyDeleteAsync($"tunnel:{tunnelId}:instance");
        await base.OnTunnelDisconnectedAsync(tunnelId);
    }
}
```

**Option B: Internal Proxy Instead of Redirect**

Instead of redirecting the client, proxy the request internally to the correct instance:

```csharp
public class DistributedTunnelCollectionProvider : ITunnelCollectionProvider
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConnectionMultiplexer _redis;
    
    public async Task<ITunnelCollection> GetTunnelCollectionAsync(
        HttpContext context, 
        CancellationToken cancellationToken)
    {
        var tunnelId = await GetTunnelIdFromContext(context);
        var instanceId = await _redis.GetDatabase()
            .StringGetAsync($"tunnel:{tunnelId}:instance");
            
        if (instanceId.IsNullOrEmpty || instanceId == _instanceId)
        {
            return _localTunnelCollection;
        }
        else
        {
            // Get target instance URL
            var instanceUrl = await _redis.GetDatabase()
                .StringGetAsync($"instance:{instanceId}:url");
            
            // Proxy request to the correct instance
            var httpClient = _httpClientFactory.CreateClient();
            var targetUrl = $"{instanceUrl}{context.Request.Path}{context.Request.QueryString}";
            
            var request = new HttpRequestMessage(
                new HttpMethod(context.Request.Method), 
                targetUrl);
            
            // Copy headers
            foreach (var header in context.Request.Headers)
            {
                request.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
            }
            
            // Copy body if present
            if (context.Request.ContentLength > 0)
            {
                request.Content = new StreamContent(context.Request.Body);
            }
            
            // Execute proxied request
            var response = await httpClient.SendAsync(request, cancellationToken);
            
            // Copy response back
            context.Response.StatusCode = (int)response.StatusCode;
            foreach (var header in response.Headers)
            {
                context.Response.Headers[header.Key] = header.Value.ToArray();
            }
            
            await response.Content.CopyToAsync(context.Response.Body);
            
            // Return null to skip normal tunnel forwarding
            return null;
        }
    }
}
```

**Option C: Microsoft Orleans (Virtual Actors)**

Orleans provides a more sophisticated approach with virtual actors (grains):

```csharp
// Define a grain interface for tunnel routing
public interface ITunnelGrain : IGrainWithStringKey
{
    Task<string?> GetConnectedInstanceAsync();
    Task RegisterInstanceAsync(string instanceId, string instanceUrl);
    Task UnregisterAsync();
}

public class TunnelGrain : Grain, ITunnelGrain
{
    private string? _instanceId;
    private string? _instanceUrl;
    
    public Task<string?> GetConnectedInstanceAsync()
    {
        return Task.FromResult(_instanceUrl);
    }
    
    public Task RegisterInstanceAsync(string instanceId, string instanceUrl)
    {
        _instanceId = instanceId;
        _instanceUrl = instanceUrl;
        return Task.CompletedTask;
    }
    
    public Task UnregisterAsync()
    {
        _instanceId = null;
        _instanceUrl = null;
        return Task.CompletedTask;
    }
}

// Usage in application
public class OrleansTunnelCollectionProvider : ITunnelCollectionProvider
{
    private readonly IGrainFactory _grainFactory;
    
    public async Task<ITunnelCollection> GetTunnelCollectionAsync(
        HttpContext context, 
        CancellationToken cancellationToken)
    {
        var tunnelId = await GetTunnelIdFromContext(context);
        var grain = _grainFactory.GetGrain<ITunnelGrain>(tunnelId);
        var instanceUrl = await grain.GetConnectedInstanceAsync();
        
        if (string.IsNullOrEmpty(instanceUrl) || instanceUrl == _currentInstanceUrl)
        {
            return _localTunnelCollection;
        }
        else
        {
            // Redirect or proxy to the instance with the tunnel
            // (similar to Option A or B above)
        }
    }
}
```

**Advantages:**
- Allows end users and tunnel clients to come from different IPs
- Enables true active-active failover
- Better load distribution across instances
- Can handle tunnel migration between instances
- Provides global view of all tunnels
- No dependency on load balancer sticky session configuration

**Disadvantages:**
- Much more complex to implement and operate
- Requires distributed infrastructure (Redis, Orleans cluster, etc.)
- Additional latency for distributed state lookup on each request
- Requires custom code (not built into UFX.Relay)
- More operational complexity (Redis/Orleans cluster management)
- Additional points of failure (distributed state store)

**Implementation Effort:**
- Redis Option: Medium complexity (~500-1000 lines of code)
- Orleans Option: High complexity (~1000-2000 lines of code + cluster setup)

### Decision Guide: Which Pattern to Choose?

Use this decision tree to determine the right approach:

#### Use Pattern 1 (Sticky Sessions - TunnelId-Based) if:
✅ You can extract TunnelId from requests (query param, header, or path)  
✅ Your load balancer supports hash-based routing  
✅ You want simple, proven, low-latency solution  
✅ You're okay with reconnection on instance failure  

**This covers 80% of use cases.**

#### Use Pattern 1 (Sticky Sessions - IP-Based) if:
✅ Tunnel clients and end users come from the same IP/NAT  
✅ You have simple, static client scenarios  
✅ Your load balancer only supports IP hash  

**This covers simple scenarios with limited client IPs.**

#### Use Pattern 2 (Distributed State) if:
⚠️ End users and tunnel clients come from completely different IPs  
⚠️ You cannot use TunnelId-based routing at load balancer  
⚠️ You need zero-downtime tunnel migration between instances  
⚠️ You require active-active failover with automatic tunnel discovery  
⚠️ You have thousands of tunnels and need sophisticated routing  

**This covers <5% of use cases and requires significant development effort.**

### Real-World Scenario Examples

**Scenario 1: SaaS Application with On-Prem Connectors**
- Each customer has one on-prem connector with unique TunnelId
- End users access via web browser (different IPs than connector)
- **Solution:** Pattern 1 with TunnelId-based hash routing
- **Why:** TunnelId is known and can be included in URL/header

**Scenario 2: IoT Gateway with Device Tunnels**
- Hundreds of IoT devices, each with a tunnel
- Devices and control applications are in the same data center
- **Solution:** Pattern 1 with IP-based hash routing
- **Why:** Source IPs are stable and limited

**Scenario 3: Multi-Tenant Platform with Dynamic Tunnel Assignment**
- Thousands of dynamic tunnels
- Complex routing requirements
- Need zero-downtime instance replacement
- **Solution:** Pattern 2 with Redis/Orleans
- **Why:** Complexity justifies the implementation effort

**Scenario 4: Public API with Random Tunnel Access**
- End users access arbitrary tunnels via REST API
- Users come from public internet (any IP)
- Cannot predict which tunnel a user will access
- **Solution:** Pattern 2 with Redis redirect or Pattern 1 with TunnelId extraction from API path
- **Why:** If TunnelId can be extracted from API path, use Pattern 1; otherwise Pattern 2

### Recommended Approach

For **most deployments**, use **Pattern 1 with TunnelId-Based Sticky Sessions**:

1. Deploy multiple instances of your UFX.Relay forwarder behind a load balancer
2. Configure TunnelId-based hash routing on the load balancer
3. Ensure TunnelId is passed in query parameter, header, or path
4. Ensure WebSocket upgrade headers are properly forwarded
5. Set appropriate timeouts for long-lived WebSocket connections
6. Rely on UFX.Relay's built-in reconnection logic to handle instance failures

**Example Request Flow:**
```
Tunnel Client: Connects with TunnelId "abc123" → Hash routes to Instance 1
End User: Requests with ?tunnelid=abc123 → Hash routes to Instance 1
Result: Both connections on same instance ✓
```

**Pattern 2 (Distributed State)** is only needed for:
- Very large scale (thousands of tunnels)
- Requirements for zero-downtime instance replacement
- Complex multi-region deployments
- Active-active failover scenarios

### Example: Kubernetes Deployment with Sticky Sessions

```yaml
apiVersion: v1
kind: Service
metadata:
  name: ufx-relay-service
spec:
  type: LoadBalancer
  sessionAffinity: ClientIP
  sessionAffinityConfig:
    clientIP:
      timeoutSeconds: 10800  # 3 hours
  ports:
    - port: 443
      targetPort: 8080
      protocol: TCP
  selector:
    app: ufx-relay-forwarder
---
apiVersion: apps/v1
kind: Deployment
metadata:
  name: ufx-relay-forwarder
spec:
  replicas: 3
  selector:
    matchLabels:
      app: ufx-relay-forwarder
  template:
    metadata:
      labels:
        app: ufx-relay-forwarder
    spec:
      containers:
      - name: forwarder
        image: your-registry/ufx-relay-forwarder:latest
        ports:
        - containerPort: 8080
        env:
        - name: ASPNETCORE_URLS
          value: "http://+:8080"
```

For NGINX Ingress with cookie-based affinity:
```yaml
apiVersion: networking.k8s.io/v1
kind: Ingress
metadata:
  name: ufx-relay-ingress
  annotations:
    nginx.ingress.kubernetes.io/affinity: "cookie"
    nginx.ingress.kubernetes.io/affinity-mode: "persistent"
    nginx.ingress.kubernetes.io/session-cookie-name: "ufx-relay-route"
    nginx.ingress.kubernetes.io/session-cookie-max-age: "10800"
    nginx.ingress.kubernetes.io/websocket-services: "ufx-relay-service"
    nginx.ingress.kubernetes.io/proxy-read-timeout: "3600"
    nginx.ingress.kubernetes.io/proxy-send-timeout: "3600"
spec:
  rules:
  - host: relay.example.com
    http:
      paths:
      - path: /
        pathType: Prefix
        backend:
          service:
            name: ufx-relay-service
            port:
              number: 443
```

### Health Checks and Monitoring

When running multiple instances, implement health checks to ensure the load balancer routes traffic only to healthy instances:

```csharp
var builder = WebApplication.CreateBuilder(args);
builder.Services.AddHealthChecks();
builder.Services.AddTunnelForwarder();

var app = builder.Build();
app.MapHealthChecks("/health");  // For load balancer health checks
app.MapTunnelHost();
app.MapTunnelForwarder();
await app.RunAsync();
```

Configure your load balancer to use `/health` endpoint for instance health monitoring.

## Future

* Add a reference implementation of Pattern 2 (Distributed State) using [Microsoft.Orleans](https://github.com/dotnet/orleans) to store the TunnelId to instance mapping and enable dynamic tunnel routing across multiple server instances.
* Add an example of client certificate authentication for the WebSocket connection.
* Consider adding TCP/UDP Forwarding over the tunnel
