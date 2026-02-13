# Blazor Support

When using ReverseTunnel.Yarp Client with a Blazor app behind a reverse proxy, additional configuration is needed.

## Runtime Configuration

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

## Runtime TunnelClientOptions (Dynamic)

To configure `TunnelClientOptions` at runtime in Blazor Server, the preferred method is to use the built-in `ITunnelClientOptionsStore`. This allows you to declaratively modify the tunnel settings (such as `TunnelHost`, `TunnelId` and `IsEnabled`) based on user input or query parameters.

### Example: `TunnelSetup.razor`

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

## Monitoring Tunnel Connection State

The `ITunnelClientManager` interface provides information about the current tunnel connection, including real-time updates on state changes.

The available connection states are:
- `Disconnected`
- `Connecting`
- `Connected`
- `Error`

Additionally, you can access the last connection error message (if any) and react to state changes.

### Example: UI Binding

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
