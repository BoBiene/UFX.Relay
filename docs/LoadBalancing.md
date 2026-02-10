# Load Balancing with Multiple Server Instances - Detailed Guide

This document provides comprehensive information about deploying UFX.Relay across multiple server instances for high availability and horizontal scaling.

**For a quick overview, see the [main README](../README.md#load-balancing-with-multiple-server-instances).**

---

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

**Load Balancer Configuration:**

- **Azure Application Gateway**: Enable cookie-based session affinity
- **AWS Application Load Balancer**: Enable sticky sessions with target group settings
- **NGINX**: Use `ip_hash` or cookie-based stickiness
- **HAProxy**: Use `balance source` or `cookie` directive
- **Traefik**: Use `sticky.cookie` configuration

**Example NGINX Configuration:**
```nginx
upstream relay_servers {
    ip_hash;  # Ensures same client IP goes to same server
    server relay1.example.com:443;
    server relay2.example.com:443;
    server relay3.example.com:443;
}

server {
    listen 443 ssl;
    server_name relay.example.com;
    
    location /tunnel/ {
        proxy_pass https://relay_servers;
        proxy_http_version 1.1;
        proxy_set_header Upgrade $http_upgrade;
        proxy_set_header Connection "upgrade";
        proxy_set_header Host $host;
        proxy_set_header X-Real-IP $remote_addr;
        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto $scheme;
        
        # WebSocket timeout settings
        proxy_read_timeout 3600s;
        proxy_send_timeout 3600s;
    }
}
```

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

