# Kubernetes Deployment Guide for UFX.Relay Load Balancing

This directory contains Kubernetes manifests for deploying UFX.Relay with multiple instances and load balancing.

## Files

- `deployment.yaml`: Deployment and Service configuration
- `ingress.yaml`: NGINX Ingress configuration with sticky sessions

## Prerequisites

- Kubernetes cluster (1.19+)
- NGINX Ingress Controller installed
- Optional: cert-manager for automatic SSL certificate management

## Deployment Steps

### 1. Build and Push Docker Image

First, build your UFX.Relay forwarder application as a Docker image:

```bash
# Example Dockerfile for UFX.Relay forwarder
cd /path/to/your/forwarder/app
docker build -t your-registry/ufx-relay-forwarder:latest .
docker push your-registry/ufx-relay-forwarder:latest
```

### 2. Update Configuration

Edit the manifests to match your environment:

**deployment.yaml:**
- Update `image:` to your Docker registry
- Adjust environment variables for your configuration
- Modify resource requests/limits based on your workload

**ingress.yaml:**
- Change `host:` to your domain
- Update `secretName:` for your TLS certificate
- Adjust timeout values if needed

### 3. Apply Manifests

```bash
# Create namespace (optional)
kubectl create namespace ufx-relay

# Apply manifests
kubectl apply -f deployment.yaml -n ufx-relay
kubectl apply -f ingress.yaml -n ufx-relay
```

### 4. Verify Deployment

```bash
# Check pod status
kubectl get pods -n ufx-relay

# Check service
kubectl get svc -n ufx-relay

# Check ingress
kubectl get ingress -n ufx-relay

# View logs
kubectl logs -f -l app=ufx-relay-forwarder -n ufx-relay
```

### 5. Test Load Balancing

```bash
# Get the ingress IP/hostname
kubectl get ingress ufx-relay-ingress -n ufx-relay

# Test endpoint
curl https://relay.example.com/health

# Test with multiple requests to see load distribution in logs
for i in {1..10}; do curl https://relay.example.com/health; done
```

## Key Configuration Details

### Session Affinity

Two levels of session affinity are configured:

1. **Service Level (ClientIP)**:
   ```yaml
   sessionAffinity: ClientIP
   sessionAffinityConfig:
     clientIP:
       timeoutSeconds: 10800
   ```
   This ensures requests from the same client IP go to the same pod.

2. **Ingress Level (Cookie)**:
   ```yaml
   nginx.ingress.kubernetes.io/affinity: "cookie"
   nginx.ingress.kubernetes.io/affinity-mode: "persistent"
   ```
   This provides more reliable affinity using cookies, especially for clients behind NAT.

### Health Checks

The deployment includes liveness and readiness probes that check `/health` endpoint:

```yaml
livenessProbe:
  httpGet:
    path: /health
    port: 8080
readinessProbe:
  httpGet:
    path: /health
    port: 8080
```

Ensure your application exposes this endpoint:
```csharp
app.MapHealthChecks("/health");
```

### Pod Anti-Affinity

The deployment uses pod anti-affinity to spread instances across different nodes:

```yaml
podAntiAffinity:
  preferredDuringSchedulingIgnoredDuringExecution:
  - weight: 100
    podAffinityTerm:
      labelSelector:
        matchExpressions:
        - key: app
          operator: In
          values:
          - ufx-relay-forwarder
      topologyKey: kubernetes.io/hostname
```

This improves availability by ensuring instances run on different nodes.

## Scaling

### Manual Scaling

```bash
kubectl scale deployment ufx-relay-forwarder --replicas=5 -n ufx-relay
```

### Horizontal Pod Autoscaler (HPA)

Create an HPA to automatically scale based on CPU/memory:

```yaml
apiVersion: autoscaling/v2
kind: HorizontalPodAutoscaler
metadata:
  name: ufx-relay-hpa
spec:
  scaleTargetRef:
    apiVersion: apps/v1
    kind: Deployment
    name: ufx-relay-forwarder
  minReplicas: 3
  maxReplicas: 10
  metrics:
  - type: Resource
    resource:
      name: cpu
      target:
        type: Utilization
        averageUtilization: 70
  - type: Resource
    resource:
      name: memory
      target:
        type: Utilization
        averageUtilization: 80
```

Apply with:
```bash
kubectl apply -f hpa.yaml -n ufx-relay
```

## Monitoring

### View Real-time Logs

```bash
# All pods
kubectl logs -f -l app=ufx-relay-forwarder -n ufx-relay

# Specific pod
kubectl logs -f ufx-relay-forwarder-xxx-yyy -n ufx-relay
```

### Check Resource Usage

```bash
kubectl top pods -n ufx-relay
kubectl top nodes
```

### Ingress Controller Logs

```bash
# Find NGINX ingress controller pods
kubectl get pods -n ingress-nginx

# View logs
kubectl logs -f nginx-ingress-controller-xxx -n ingress-nginx
```

## Troubleshooting

### Pods Not Starting

```bash
kubectl describe pod ufx-relay-forwarder-xxx -n ufx-relay
kubectl logs ufx-relay-forwarder-xxx -n ufx-relay
```

### WebSocket Connections Failing

1. Verify ingress annotations for WebSocket support
2. Check timeout settings in ingress
3. Ensure sticky sessions are configured
4. Test with: `wscat -c wss://relay.example.com/tunnel/123`

### Load Not Distributing

1. Check session affinity configuration
2. Test from different client IPs
3. Clear browser cookies if using cookie-based affinity
4. Check service endpoints: `kubectl get endpoints ufx-relay-service -n ufx-relay`

### Health Checks Failing

1. Verify `/health` endpoint is exposed in your app
2. Check readiness probe configuration
3. View events: `kubectl get events -n ufx-relay --sort-by='.lastTimestamp'`

## Cloud Provider Specific Notes

### Azure AKS

- Use Azure Load Balancer with Application Gateway Ingress Controller (AGIC) as alternative
- Enable cookie-based session affinity in Application Gateway

### AWS EKS

- Can use AWS Application Load Balancer (ALB) Ingress Controller
- Configure target group stickiness in ALB settings

### Google GKE

- Use Google Cloud Load Balancer with session affinity
- Configure via Ingress annotations or BackendConfig

## Security Considerations

1. **TLS/SSL**: Always use HTTPS in production
2. **Network Policies**: Restrict pod-to-pod communication
3. **Secrets Management**: Use Kubernetes Secrets or external secret managers
4. **RBAC**: Apply principle of least privilege
5. **Pod Security**: Use Pod Security Standards/Admission

## Production Checklist

- [ ] Docker image built and pushed to registry
- [ ] Environment variables configured
- [ ] Resource limits set appropriately
- [ ] Health checks working
- [ ] TLS certificate configured
- [ ] Sticky sessions enabled
- [ ] Pod anti-affinity configured
- [ ] Monitoring and logging in place
- [ ] Backup and disaster recovery plan
- [ ] Scaling policy defined
- [ ] Security policies applied
