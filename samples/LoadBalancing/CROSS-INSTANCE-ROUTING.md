# Understanding Cross-Instance Tunnel Routing

## The Critical Problem

When load balancing UFX.Relay across multiple instances, you must understand that there are **two different types of connections** that must be properly routed:

### Connection Type 1: Tunnel Client WebSocket
The on-prem application establishes a WebSocket connection to create the tunnel.

### Connection Type 2: End-User HTTP Requests
End users making HTTP requests that need to be forwarded through an existing tunnel.

## The Routing Challenge

**Scenario:**
```
1. Tunnel Client (TunnelId="abc123") connects → Load Balancer → Instance 1
   - WebSocket tunnel is established on Instance 1
   
2. End User makes HTTP request with tunnelid=abc123 → Load Balancer → Instance 2
   - Instance 2 looks for tunnel "abc123" in its local registry
   - Instance 2 does NOT have tunnel "abc123" (it's on Instance 1)
   - Result: 404 Not Found ❌
```

**Root Cause:**
UFX.Relay uses a **local, in-process tunnel registry** (`ConcurrentDictionary<string, Tunnel>`). Each instance only knows about tunnels connected directly to itself. There is **no built-in cross-instance tunnel lookup**.

## Solutions

### Solution 1: TunnelId-Based Load Balancer Routing (Recommended)

Configure your load balancer to hash requests based on the TunnelId. This ensures:
- Both WebSocket and HTTP requests with the same TunnelId go to the same instance
- No shared state required
- No code changes to UFX.Relay needed

**How it works:**
```
1. Tunnel Client connects with TunnelId="abc123"
   → Load balancer hashes "abc123" → Always routes to Instance 1
   
2. End User requests with tunnelid=abc123
   → Load balancer hashes "abc123" → Always routes to Instance 1
   
3. Both connections are on Instance 1 ✓
```

**Implementation:**

**NGINX:**
```nginx
upstream relay_servers {
    # Hash on tunnelid query parameter
    hash $arg_tunnelid consistent;
    
    # OR hash on X-Tunnel-Id header
    # hash $http_x_tunnel_id consistent;
    
    server instance1:443;
    server instance2:443;
    server instance3:443;
}
```

**HAProxy:**
```haproxy
backend relay_backend
    balance url_param tunnelid
    hash-type consistent
    
    # OR hash on header
    # balance hdr(X-Tunnel-Id)
    # hash-type consistent
```

**Kubernetes Service:**
```yaml
apiVersion: v1
kind: Service
metadata:
  name: ufx-relay-service
  annotations:
    service.kubernetes.io/topology-aware-hints: "auto"
spec:
  sessionAffinity: ClientIP
  sessionAffinityConfig:
    clientIP:
      timeoutSeconds: 10800
```

**Requirements:**
1. TunnelId must be present in **every request** (query param, header, or path)
2. Both tunnel client and end user requests must include the TunnelId
3. Load balancer must support hash-based routing

**Advantages:**
✅ Simple - no code changes  
✅ Low latency - load balancer routing  
✅ No additional infrastructure  
✅ Proven pattern  

**Limitations:**
⚠️ Requires TunnelId in all requests  
⚠️ Requires hash-capable load balancer  

---

### Solution 2: Distributed State with Shared Registry

Implement a shared registry (Redis, Orleans) that tracks which instance has which tunnel. When a request arrives at the "wrong" instance, it can:
- Option A: Redirect the client to the correct instance
- Option B: Internally proxy the request to the correct instance

**How it works:**
```
1. Tunnel "abc123" connects to Instance 1
   → Instance 1 registers in Redis: tunnel:abc123 → instance1.example.com
   
2. End User requests tunnel "abc123" → lands on Instance 2
   → Instance 2 queries Redis: "Where is tunnel abc123?"
   → Redis responds: "instance1.example.com"
   → Instance 2 redirects/proxies to Instance 1
   
3. Request reaches Instance 1 which has the tunnel ✓
```

**Implementation:** See README.md "Pattern 2: Distributed State with Cross-Instance Routing"

**Advantages:**
✅ Works without TunnelId in every request  
✅ True active-active failover  
✅ Can migrate tunnels between instances  

**Disadvantages:**
❌ Complex - requires custom code (~500-2000 lines)  
❌ Additional infrastructure (Redis/Orleans cluster)  
❌ Higher latency (distributed lookup)  
❌ More operational complexity  

---

### Solution 3: IP-Based Sticky Sessions (Limited Use Cases)

If tunnel clients and end users **always** come from the same source IP, you can use IP-based sticky sessions.

**NGINX:**
```nginx
upstream relay_servers {
    ip_hash;
    server instance1:443;
    server instance2:443;
}
```

**When it works:**
✅ Tunnel client and end users are behind the same NAT  
✅ Corporate network with limited, stable IPs  
✅ IoT scenarios where devices and control systems share IPs  

**When it DOESN'T work:**
❌ End users come from public internet (different IPs than tunnel client)  
❌ Tunnel client is on-prem, users are remote  
❌ Mobile users with changing IPs  

---

## Decision Tree

```
Do tunnel clients and end users come from the same IP?
│
├─ YES → Use IP-based sticky sessions (Solution 3)
│
└─ NO → Can you include TunnelId in every request?
    │
    ├─ YES → Use TunnelId-based hashing (Solution 1) ✅ RECOMMENDED
    │
    └─ NO → You need distributed state (Solution 2)
```

## Common Scenarios

### Scenario 1: SaaS Application
- **Setup:** Each customer has on-prem connector, users access via web
- **IPs:** Tunnel client (on-prem IP) ≠ Users (various IPs)
- **TunnelId:** Available in URL path or query parameter
- **Solution:** TunnelId-based hashing ✅

### Scenario 2: IoT Gateway
- **Setup:** Devices in data center, control system in same network
- **IPs:** Devices and control system share NAT IP
- **Solution:** IP-based sticky sessions ✅

### Scenario 3: Public REST API
- **Setup:** Any user can access any tunnel via API
- **IPs:** Completely random
- **TunnelId:** Can extract from API path (e.g., `/api/tunnels/{id}/...`)
- **Solution:** TunnelId-based hashing if TunnelId in path, else distributed state

### Scenario 4: Multi-Tenant Platform
- **Setup:** Thousands of dynamic tunnels, complex routing
- **Requirements:** Zero-downtime instance replacement
- **Solution:** Distributed state with Redis/Orleans

## Testing Your Setup

### Test TunnelId-Based Routing

```bash
# 1. Start tunnel client with TunnelId
curl "wss://relay.example.com/tunnel/connect?tunnelid=test123"

# 2. Make HTTP request with same TunnelId
curl "https://relay.example.com/some-endpoint?tunnelid=test123"

# 3. Verify both went to same instance (check logs)
# Both should show same instance ID in logs
```

### Test IP-Based Routing

```bash
# 1. Connect tunnel from IP A
# 2. Make HTTP request from same IP A
# 3. Verify both hit same instance
```

### Test Load Distribution

```bash
# Make requests with different TunnelIds
for i in {1..10}; do
  curl "https://relay.example.com/test?tunnelid=tunnel-$i"
done

# Check which instance handled each request
# Should distribute across instances based on TunnelId hash
```

## Summary

**The Problem:** Tunnel WebSocket on Instance 1, User request lands on Instance 2 → 404

**The Solution (Recommended):** TunnelId-based load balancer hashing ensures both connections land on the same instance.

**Key Requirement:** TunnelId must be present in all requests (query param, header, or path).

**Alternative:** If TunnelId cannot be included, implement distributed state (complex, but works).
