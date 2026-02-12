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


## Future

* Scaling across multiple instances of the cloud service could be achieved by using [Microsoft.Orleans](https://github.com/dotnet/orleans) to store the TunnelId to instance mapping and redirect clients to the correct instance where the client is connected.
* Add an example of client certificate authentication for the WebSocket connection.
* Consider adding TCP/UDP Forwarding over the tunnel
