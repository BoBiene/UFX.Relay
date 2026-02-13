<div align="center">

 <img src="ReverseTunnel.Yarp.Logo.png" width="220" alt="ReverseTunnel.Yarp Logo" />

  <h3><b>Active Reverse Tunnel for YARP (ARTY)</b></h3> 
  <i >
    Outbound-only secure connectivity for ASP.NET Core
  </i>
  <br/>
<br/>
  
[![CI](https://github.com/BoBiene/UFX.Relay/actions/workflows/ci.yml/badge.svg)](https://github.com/BoBiene/UFX.Relay/actions/workflows/ci.yml)
[![NuGet](https://img.shields.io/nuget/v/ReverseTunnel.Yarp.svg)](https://www.nuget.org/packages/ReverseTunnel.Yarp/)
[![NuGet Downloads](https://img.shields.io/nuget/dt/ReverseTunnel.Yarp.svg)](https://www.nuget.org/packages/ReverseTunnel.Yarp/)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)

</div>

> **Note:** This is a fork of [UnifiedFX/UFX.Relay](https://github.com/unifiedfx/UFX.Relay), rebranded and enhanced for broader community use.

# Active Reverse Tunnel for YARP (ARTY)

## Overview

ReverseTunnel.Yarp connects two ASP.NET Core Middleware pipelines using a single WebSocket connection, extending a cloud application to an on-premise application instance. This is similar to services like [ngrok](https://ngrok.com), but rather than requiring an external 3rd party service, ReverseTunnel.Yarp is a self-contained pure ASP.NET Core solution.

The Server/Forwarder end leverages [YARP (Yet Another Reverse Proxy)](https://github.com/microsoft/reverse-proxy) to forward ASP.NET Core requests to the on-premise application via the WebSocket connection. At the lowest level, YARP converts an HTTPContext to an HTTPClientRequest and sends it to the on-premise application via the WebSocket connection, which uses a [MultiplexingStream](https://github.com/dotnet/Nerdbank.Streams/blob/main/doc/MultiplexingStream.md) to allow multiple requests to be sent over a single connection.

> **Note:** This implementation uses [YARP DirectForwarding](https://github.com/microsoft/reverse-proxy/blob/main/src/ReverseProxy/Forwarder/HttpForwarder.cs) to forward requests to the on-premise application. Any YARP cluster configuration will not be used.

### Key Components

ReverseTunnel.Yarp comprises three main components:

- **Forwarder** - Uses YARP DirectForwarding to forward requests over the tunnel
- **Listener** - Receives requests over the tunnel and injects them into the ASP.NET Core pipeline
- **Tunnel** - A logical layer on top of a WebSocket connection that multiplexes multiple requests

## Quick Start

### Installation

```bash
dotnet add package ReverseTunnel.Yarp
```

### Minimal Server Configuration (Forwarder)

```csharp
var builder = WebApplication.CreateBuilder(args);
builder.Services.AddTunnelForwarder();
var app = builder.Build();
app.MapTunnelHost();
app.MapTunnelForwarder();
app.Run();
```

### Minimal Client Configuration (Listener)

```csharp
var builder = WebApplication.CreateBuilder(args);
builder.WebHost.AddTunnelListener(options =>
{
    options.DefaultTunnelId = "123";
});

builder.Services.AddTunnelClient(options =>
    options with
    {
        TunnelHost = "wss://localhost:7200",
        TunnelId = "123"
    });
```

## Core Concepts

### Forwarder

The forwarder uses YARP DirectForwarding to forward requests over the tunnel connection to be received by the listener.

```csharp
var builder = WebApplication.CreateBuilder(args);
builder.Services.AddTunnelForwarder();
var app = builder.Build();
app.MapTunnelForwarder();
app.Run();
```

### Listener

The listener receives requests over the tunnel from the forwarder and injects them into the ASP.NET Core pipeline.

```csharp
var builder = WebApplication.CreateBuilder(args);
builder.WebHost.AddTunnelListener(options =>
{
    options.DefaultTunnelId = "123";
});
```

#### Reconnect Backoff (Optional)

If you expect repeated connection failures (e.g., temporary network issues or misconfiguration), you can enable exponential backoff for reconnect attempts:

```csharp
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

#### Tunnel Client

The client requires the TunnelHost and TunnelId to be specified in order to connect to the Tunnel Host.

```csharp
builder.Services.AddTunnelClient(options =>
    options with
    {
        TunnelHost = "wss://localhost:7400",
        TunnelId = "123"
    });
```

#### Tunnel Host

The tunnel host is added as a minimal API endpoint to the application pipeline, accepting websocket connections on `/tunnel/{tunnelId}` by default.

```csharp
var app = builder.Build();
app.MapTunnelHost();
app.Run();
```

## Sample Projects

The sample [Client](samples/Sample.Client/Program.cs) and [Server](samples/Sample.Server/Program.cs) projects demonstrate how to use ReverseTunnel.Yarp to connect a cloud application to an on-premise application with simple association using a TunnelId.

Once the sample projects have started, requests to `https://localhost:7200/` will be forwarded to the client application:

- `https://localhost:7200/server` - Handled by the server
- `https://localhost:7200/client` - Forwarded to the client and returned via the server

## Configuration

### Client Configuration

The minimal configuration for the client:

```csharp
builder.WebHost.AddTunnelListener(options => { options.DefaultTunnelId = "123"; });

builder.Services.AddTunnelClient(options =>
    options with
    {
        TunnelHost = "wss://localhost:7200"
    });
```

This creates a Kestrel Listener that will inject requests (from the forwarder) into the client ASP.NET Core pipeline received over the WebSocket connection to the server.

> **Note:** When a code-based listener is added to Kestrel, it will disable the use of the default Kestrel listener configuration. If you require the default listener to be enabled, set the `includeDefaultUrls` parameter to `true`:

```csharp
builder.WebHost.AddTunnelListener(options =>
{
    options.DefaultTunnelId = "123";
}, includeDefaultUrls: true);
```

### Server Configuration

The minimal configuration for the server:

```csharp
var builder = WebApplication.CreateBuilder(args);
builder.Services.AddTunnelForwarder();
var app = builder.Build();
app.MapTunnelHost();
app.Run();
```

Requests sent to the server with a TunnelId header will be forwarded to the corresponding listener. If a DefaultTunnelId is set in the configuration, requests without a TunnelId header will be forwarded to the listener with the DefaultTunnelId:

```csharp
var builder = WebApplication.CreateBuilder(args);
builder.Services.AddTunnelForwarder(options =>
{
    options.DefaultTunnelId = "123";
});
var app = builder.Build();
app.MapTunnelHost();
app.Run();
```

You can also use a transformer (courtesy of YARP) to modify the behavior of the Forwarder:

```csharp
builder.Services.AddTunnelForwarder(options =>
{
    options.Transformer = transformBuilderContext =>
    {
        transformBuilderContext.UseDefaultForwarders = true;
    };
});
```

## Advanced Topics

For more detailed configuration and advanced use cases, see the documentation:

- **[Blazor Support](docs/blazor-support.md)** - Configuration for Blazor apps, runtime options, and connection state monitoring
- **[Advanced Configuration](docs/advanced-configuration.md)** - Path prefix transformers, WebSocket authentication, and tunnel request detection
- **[Connection Aggregation](docs/connection-aggregation.md)** - Aggregating multiple on-prem connections via a single cloud connection
- **[Multi-hop / Chained Routing](docs/multi-hop-routing.md)** - Composing ReverseTunnel.Yarp with additional reverse-proxy hops

## Future Enhancements

- Scaling across multiple instances of the cloud service could be achieved by using [Microsoft.Orleans](https://github.com/dotnet/orleans) to store the TunnelId to instance mapping and redirect clients to the correct instance
- Add an example of client certificate authentication for the WebSocket connection
- Consider adding TCP/UDP Forwarding over the tunnel

## Contributing

Contributions are welcome! Please feel free to submit a Pull Request.

For information about publishing releases to NuGet.org, see the [Publishing Guide](docs/publishing.md).

## License

This project is licensed under the MIT License - see the [LICENSE](LICENSE) file for details.

## Acknowledgments

This project is a fork of [UnifiedFX/UFX.Relay](https://github.com/unifiedfx/UFX.Relay). Thanks to the original authors for their excellent work.
