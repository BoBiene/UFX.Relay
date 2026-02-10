# Load Balancing Quick Start Guide

## What's the Difference?

### Connection Aggregation (samples/Aggregation)
**Problem**: Too many WebSocket connections from multiple on-prem clients
**Solution**: Aggregate multiple on-prem connections through a single cloud endpoint
```
Many On-Prem Clients → Single Cloud Instance
```

### Load Balancing (this guide)
**Problem**: Single cloud instance can't handle all traffic, need high availability
**Solution**: Multiple cloud instances behind a load balancer
```
Clients → Load Balancer → Multiple Cloud Instances
```

## Quick Start: Sticky Sessions Pattern (Recommended)

### 1. Choose Your Load Balancer

| Platform | Configuration |
|----------|--------------|
| **NGINX** | `ip_hash` in upstream block |
| **Kubernetes** | `sessionAffinity: ClientIP` in Service |
| **Azure App Gateway** | Enable cookie-based session affinity |
| **AWS ALB** | Enable target group stickiness |
| **HAProxy** | `balance source` |

### 2. Deploy Multiple Instances

```bash
# Example: 3 instances behind NGINX
Instance 1: https://relay1.example.com:443
Instance 2: https://relay2.example.com:443
Instance 3: https://relay3.example.com:443

Load Balancer: https://relay.example.com:443
```

### 3. Configure Load Balancer

**NGINX Example:**
```nginx
upstream relay_servers {
    ip_hash;
    server relay1.example.com:443;
    server relay2.example.com:443;
    server relay3.example.com:443;
}
```

**Kubernetes Example:**
```yaml
spec:
  sessionAffinity: ClientIP
  sessionAffinityConfig:
    clientIP:
      timeoutSeconds: 10800  # 3 hours
```

### 4. Client Configuration

Clients connect to the load balancer (no changes needed):
```csharp
builder.Services.AddTunnelClient(options =>
    options with
    {
        TunnelHost = "wss://relay.example.com",  // Load balancer URL
        TunnelId = "my-tunnel"
    });
```

### 5. Test It

```bash
# Client connects and gets routed to one instance
# All subsequent requests for that tunnel go to the same instance
curl https://relay.example.com/tunnel/my-tunnel
```

## Why Sticky Sessions?

✅ **Simple**: No code changes, just load balancer config
✅ **Reliable**: Works with standard load balancers
✅ **Efficient**: Minimal overhead
✅ **Proven**: Industry-standard pattern for WebSocket load balancing

## When Do You Need Advanced Patterns?

Only if you have:
- ❌ Thousands of concurrent tunnels
- ❌ Zero-downtime instance replacement requirements
- ❌ Multi-region deployments with tunnel migration
- ❌ Complex failover scenarios

For 99% of use cases, **sticky sessions are sufficient**.

## Files in This Sample

```
LoadBalancing/
├── README.md                          # Main documentation
├── QUICKSTART.md                      # This file
├── docker-compose.yml                 # Local testing setup
├── nginx/
│   ├── nginx.conf                     # Production NGINX config
│   └── nginx-docker.conf              # Docker Compose NGINX config
├── kubernetes/
│   ├── README.md                      # Kubernetes deployment guide
│   ├── deployment.yaml                # Deployment and Service
│   └── ingress.yaml                   # Ingress with sticky sessions
└── haproxy/
    └── haproxy.cfg                    # HAProxy configuration
```

## Next Steps

1. ✅ Read the main [README.md](README.md) for detailed documentation
2. ✅ Choose your platform (NGINX, Kubernetes, HAProxy, etc.)
3. ✅ Follow the platform-specific guide
4. ✅ Test locally with Docker Compose (optional)
5. ✅ Deploy to production

## Need Help?

- Review the [main README](../../README.md#load-balancing-with-multiple-server-instances) for architecture details
- Check platform-specific guides in subdirectories
- Look at existing samples for patterns

## Common Questions

**Q: Can I use round-robin load balancing?**
A: No, WebSocket connections are stateful and must stay with the same instance. Use sticky sessions.

**Q: What happens if an instance crashes?**
A: The client will reconnect (UFX.Relay has built-in reconnection) and may be routed to a different instance.

**Q: How do I scale to 1000+ tunnels?**
A: Sticky sessions work fine for thousands of tunnels. Only consider distributed state (Orleans/Redis) if you need advanced features like tunnel migration or multi-region support.

**Q: Can I mix HTTP and WebSocket traffic?**
A: Yes, sticky sessions work for both HTTP and WebSocket. Once a client connects, all traffic is routed to the same instance.

**Q: Is the Aggregation sample the same as load balancing?**
A: No! Aggregation is about reducing connections (many clients → one server). Load balancing is about scaling (clients → many servers).
