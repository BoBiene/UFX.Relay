# Load Balancing Sample

This sample demonstrates how to deploy multiple instances of UFX.Relay forwarder/server behind a load balancer for high availability and horizontal scaling.

## Overview

This sample includes:
- Example NGINX configuration with sticky sessions
- Kubernetes deployment manifests
- Docker Compose setup for local testing
- HAProxy configuration example

## Important Concepts

### Sticky Sessions (Session Affinity)

UFX.Relay uses WebSocket connections which are stateful. When a tunnel client connects to a forwarder instance, that connection must remain with the same instance for its lifetime. This is achieved through **sticky sessions** at the load balancer level.

### How It Works

1. **Client connects**: The tunnel client initiates a WebSocket connection to the load balancer
2. **Load balancer routes**: Based on the sticky session algorithm (IP hash, cookie, etc.), the request is routed to a specific forwarder instance
3. **Connection established**: The WebSocket connection is established and maintained with that instance
4. **Subsequent requests**: All HTTP requests for that tunnel are routed to the same instance via sticky sessions
5. **Reconnection on failure**: If the instance fails, the client reconnects and may be routed to a different instance (UFX.Relay has built-in reconnection logic)

## Local Testing with Docker Compose

The `docker-compose.yml` file demonstrates running multiple forwarder instances behind an NGINX load balancer.

### Start the services:
```bash
docker-compose up -d
```

This starts:
- 3 forwarder instances (ports 8081, 8082, 8083)
- 1 NGINX load balancer (port 443)
- 1 client instance that connects to the load balancer

### Test the setup:
```bash
# Requests are load-balanced across instances
curl https://localhost:443/server

# Tunnel requests are routed via sticky sessions
curl https://localhost:443/client
```

### View logs:
```bash
docker-compose logs -f forwarder1
docker-compose logs -f nginx
```

### Clean up:
```bash
docker-compose down
```

## Production Deployment Examples

### NGINX Configuration

See `nginx/nginx.conf` for a complete example with:
- IP hash-based sticky sessions for WebSocket connections
- Proper header forwarding
- WebSocket upgrade support
- Timeout configuration

### Kubernetes Deployment

See `kubernetes/` directory for:
- Deployment with 3 replicas
- Service with `ClientIP` session affinity
- NGINX Ingress with cookie-based sticky sessions
- Health check configuration

### HAProxy Configuration

See `haproxy/haproxy.cfg` for an alternative load balancer configuration.

## Key Configuration Points

### 1. WebSocket Support
Ensure your load balancer properly handles WebSocket upgrade:
```nginx
proxy_http_version 1.1;
proxy_set_header Upgrade $http_upgrade;
proxy_set_header Connection "upgrade";
```

### 2. Sticky Sessions
Configure session affinity based on your needs:
- **IP hash**: Simple, works well for clients with stable IPs
- **Cookie-based**: More reliable for clients behind NAT
- **Source IP**: Built-in option in many cloud load balancers

### 3. Timeouts
Set appropriate timeouts for long-lived WebSocket connections:
```nginx
proxy_read_timeout 3600s;  # 1 hour
proxy_send_timeout 3600s;
```

### 4. Header Forwarding
Preserve client information:
```nginx
proxy_set_header X-Real-IP $remote_addr;
proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
proxy_set_header X-Forwarded-Proto $scheme;
```

### 5. Health Checks
Add health check endpoint to your forwarder:
```csharp
app.MapHealthChecks("/health");
```

Configure load balancer to use this endpoint to detect failed instances.

## Monitoring

When running multiple instances, monitor:
- Number of active tunnels per instance
- Request distribution across instances
- WebSocket connection success/failure rates
- Instance health and resource usage

## Troubleshooting

### Issue: Clients can't maintain connections
- Check sticky session configuration
- Verify WebSocket upgrade headers are forwarded
- Check timeout settings

### Issue: Uneven load distribution
- Consider using cookie-based sticky sessions instead of IP hash
- Check if some tunnels are much more active than others
- Review load balancer's health check configuration

### Issue: Connections fail after instance restart
- This is expected behavior - clients will automatically reconnect to another instance
- Ensure UFX.Relay reconnection logic is properly configured
- Implement graceful shutdown if possible

## Advanced: Distributed State Pattern

For very large-scale deployments requiring active-active failover, see the main README section on "Pattern 2: Distributed State with Service Discovery" which discusses using Redis or Microsoft Orleans for distributed tunnel registry.

This is only necessary for:
- Thousands of concurrent tunnels
- Zero-downtime instance replacement requirements
- Multi-region deployments
- Complex failover scenarios

For most use cases, sticky sessions (demonstrated here) are sufficient and much simpler to operate.
