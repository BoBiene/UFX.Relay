# Advanced Configuration

## Using TunnelPathPrefixTransformer

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
> Ensure to set `UseForwardedHeaders` and `base` href in the Blazor app as mentioned in [Blazor Support](blazor-support.md).

## WebSocket Authentication

### Client

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

### Server

The Tunnel Host can be configured to require Authentication for the WebSocket connection from the Tunnel Client using standard Minimal API middleware configuration as follows:

```csharp
app.MapTunnelHost().RequireAuthorization();
```

## Detecting Tunnel vs Normal HTTP Requests

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
