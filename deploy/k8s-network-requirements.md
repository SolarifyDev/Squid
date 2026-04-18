# Squid on Kubernetes — Network Requirements

Deploying the Squid API server on Kubernetes exposes **two independent
endpoints** with different networking stacks. Getting either one wrong
produces silent failures that are expensive to diagnose. This document is the
authoritative checklist for cluster operators.

> Counterpart developer-facing doc:
> [`CLAUDE.md → Kubernetes Deployment — Exposing Halibut Polling`](../CLAUDE.md)

## Endpoints at a glance

| Endpoint | Protocol | K8s abstraction | TLS owner | Public port |
|---|---|---|---|---|
| Web / HTTP API | HTTP/1.1, HTTP/2 | `Ingress → ClusterIP Service → pod:8080` | cert-manager + Let's Encrypt, terminated at ingress | 443 |
| Halibut polling (agent RPC) | L4 TCP, Halibut's own mTLS | `Service type=LoadBalancer` → pod:10943 (pure TCP passthrough) | Halibut self-signed cert inside the pod | 10943 |

**Do not mix them.** nginx-ingress has no first-class L4 passthrough story for
the Halibut protocol, and coupling them produces fragile per-cloud hacks.

---

## Pre-flight checklist (do this once per cluster / environment)

- [ ] **DNS:** one A record per endpoint (two different hostnames or at minimum
      two different IPs)
  - `squid-api-<env>.<domain>`     → ingress IP
  - `squid-polling-<env>.<domain>` → Halibut SLB IP
- [ ] **Node security group:** allow `TCP 30000-32767` from `100.64.0.0/10`
      (see detail below — this is the single most common root cause of
      "backend 异常" on Alibaba Cloud ACK)
- [ ] **Octopus / deployment variables:**
  - `ServerUrl__ExternalUrl`  = `https://squid-api-<env>.<domain>`
  - `ServerUrl__CommsUrl`     = `https://squid-polling-<env>.<domain>:10943`
  - Both resolved via `#{IngressBaseDomainName}` / `#{PollingBaseDomainName}`
    project variables for portability
- [ ] **Self-signed Halibut cert** baked into `SelfCertSetting.Base64` in the
      API pod — shared across replicas via a K8s Secret; never regenerated per
      pod (would break agent trust on rollout)
- [ ] **Ingress** only references the **HTTP** Service (`squid-service:8080`).
      It MUST NOT reference `squid-halibut` or `:10943`.
- [ ] **Two LoadBalancer Services** (if HTTP also goes via an SLB in front of
      ingress):
  1. The ingress-controller's existing LB Service for `:80/:443`
  2. A dedicated new LB Service for `:10943` — **see `squid-halibut` yaml
     below**

## Alibaba SLB health check — the `100.64.0.0/10` rule

Alibaba Cloud sources its SLB health check probes from the **shared carrier
NAT range `100.64.0.0/10`** (RFC 6598). This range is:

- **Not routable on the public internet** — an attacker cannot spoof these
  IPs from outside the cloud
- **Used exclusively for Alibaba internal infrastructure** (SLB, CDN, etc.)
- **Required in the worker node security group** for every K8s LoadBalancer
  Service, otherwise SLB cannot verify pod health, marks the backend as
  `异常 (unhealthy)`, and **silently drops all forwarded traffic**

**Add this single rule to the worker node security group (once per cluster):**

| Field | Value |
|---|---|
| Direction | Inbound |
| Action | Allow |
| Priority | 1 |
| Protocol | TCP |
| Port range | `30000/32767` |
| Source | `100.64.0.0/10` |
| Description | `Alibaba SLB health check → K8s NodePort (RFC 6598)` |

> Reference: Alibaba Cloud official CLB documentation,
> ["配置健康检查"](https://help.aliyun.com/zh/slb/classic-load-balancer/user-guide/configure-health-checks)

## Dedicated LoadBalancer Service for Halibut

The complete working Service definition (paste into your Octopus step, or
apply directly via `kubectl`):

```yaml
apiVersion: v1
kind: Service
metadata:
  name: squid-halibut
  namespace: '#{K8SNameSpace}'
  annotations:
    # Force SLB listener to TCP protocol (CCM may default to HTTP, which mis-parses
    # the Halibut binary protocol and immediately RSTs every connection)
    service.beta.kubernetes.io/alibaba-cloud-loadbalancer-protocol-port: "tcp:10943"

    # Force TCP health check (default HTTP GET hits Halibut → permanent "异常")
    service.beta.kubernetes.io/alibaba-cloud-loadbalancer-health-check-type: "tcp"
    service.beta.kubernetes.io/alibaba-cloud-loadbalancer-health-check-healthy-threshold: "2"
    service.beta.kubernetes.io/alibaba-cloud-loadbalancer-health-check-unhealthy-threshold: "2"
    service.beta.kubernetes.io/alibaba-cloud-loadbalancer-health-check-interval: "5"

    # Long-lived polling connections (Tentacle keeps TCP open until the server
    # has work for it; default 900s is enough but we explicit to 1h for safety)
    service.beta.kubernetes.io/alibaba-cloud-loadbalancer-persistence-timeout: "3600"

    # Pin SLB identity → stable public IP across Service delete/recreate.
    # Remove to let CCM allocate a fresh SLB (which WILL change the IP).
    # service.beta.kubernetes.io/alibaba-cloud-loadbalancer-id: "lb-xxxxxxxxxxxx"
    # service.beta.kubernetes.io/alibaba-cloud-loadbalancer-force-override-listeners: "true"
spec:
  type: LoadBalancer
  ports:
    - name: halibut
      port: 10943
      targetPort: 10943
      protocol: TCP
  selector:
    # When this Service is in a DIFFERENT Octopus step than the Deployment,
    # Octopus does NOT auto-rewrite `octopusexport: OctopusExport` placeholders.
    # Write the real selector literally:
    Octopus.Kubernetes.DeploymentName: deploy-squid-api
```

## Diagnostic playbook

### Step 1: is the pod's own Halibut listener healthy?

```bash
# Inside the API pod (or any pod in the same namespace):
python3 -c "
import socket, ssl, hashlib
s = socket.create_connection(('127.0.0.1', 10943), timeout=5)
tls = ssl.create_default_context()
tls.check_hostname = False
tls.verify_mode = ssl.CERT_NONE
t = tls.wrap_socket(s, server_hostname='localhost')
print('thumbprint=', hashlib.sha1(t.getpeercert(True)).hexdigest().upper())"
# Expected output: matches the server's SelfCertSetting thumbprint
```

If this fails, the bug is inside the API pod (Halibut runtime not started, or
TLS config broken). Check pod logs for `HalibutRuntime created.` and
`Halibut polling listener started on port 10943`.

### Step 2: does the Service route to the pod?

```bash
kubectl -n <ns> get endpoints squid-halibut
# Expected: squid-halibut   <pod-ip>:10943
```

If `<none>`: selector mismatch. Verify `kubectl get pod <pod> --show-labels`
contains the selector key/value pair.

### Step 3: does the SLB see the backend as healthy?

Open the Alibaba Cloud console → SLB → find the LB by ID → **后端服务器** tab.
The entry for the node/pod should show `运行中` / `Normal`. If `异常`:

1. 99% of the time — the `100.64.0.0/10` security group rule is missing.
   Add it (see above).
2. 1% of the time — health check is HTTP (wrong) rather than TCP. Check
   **监听** tab and confirm **健康检查方式 = TCP**.

### Step 4: does the public endpoint present the right certificate?

```bash
openssl s_client -connect squid-polling-<env>.<domain>:10943 \
  -servername squid-polling-<env>.<domain> </dev/null 2>/dev/null \
  | openssl x509 -noout -fingerprint -sha1
```

Expected: thumbprint matches the server's self-signed cert (find it in Seq
log: `HalibutRuntime created. ServerCertThumbprint=<hex>`). If it doesn't
match: some intermediary is terminating TLS with its own cert — check that
the SLB listener is `tcp:10943` (L4 passthrough), not HTTPS.

### Step 5: do server logs show trust reload?

Seq filter: `@MessageTemplate like '%trust reconfigured%'`

Expected after every agent registration:

```
Halibut trust reconfigured, N polling agent(s) trusted
```

Where `N` matches the number of active polling-style machines in the
database. If `N=0`: `GetPollingThumbprintsAsync` is not finding the
thumbprints (SQL / JSON query issue, typically EF function binding).

## Common failure modes

| Symptom | Root cause | Fix |
|---|---|---|
| Tentacle: `Received an unexpected EOF`; server Seq shows no incoming connection | Traffic never reaches the pod — ingress swallowing it, or LB not configured | Create `squid-halibut` Service with `type: LoadBalancer` (see above) |
| Tentacle registers (`MachineId=N`) but polling EOF; server Seq shows `Socket IO exception: <node-ip>` | SLB backend marked unhealthy → drops forwarded traffic | Add `100.64.0.0/10` SG rule + verify health check is TCP |
| `kubectl get endpoints <svc>` returns `<none>` | Service selector doesn't match pod labels | Hardcode real label (`Octopus.Kubernetes.DeploymentName: deploy-squid-api`); don't rely on Octopus selector rewriting across steps |
| DNS no longer resolves after Service `delete+recreate` | CCM allocated a new SLB with a new IP | Add `alibaba-cloud-loadbalancer-id` annotation to pin the SLB, OR update DNS |
| UI generates install script with polling URL pointing at the ingress domain, not the LB domain | `ServerUrl__CommsUrl` env var empty | Set it explicitly to `https://<polling-domain>:10943` in the Deployment yaml |
| Server log: `Halibut trust reconfigured, 0 polling agent(s) trusted` despite machines in DB | EF function binding for `jsonb_extract_path_text` misregistered, or JSON property casing drift | Check `SquidDbContext.OnModelCreating` + `MachineRegistrationService.BuildTentaclePollingEndpointJson` key casing match |

## What the server does automatically

The Squid API, starting with the version documented here, runs a
**reachability probe at install-script generation time** (see
`TentacleCommsUrlProbe`). When an operator clicks "Generate install script"
in the UI:

1. Server resolves the configured polling URL from `ServerUrl__CommsUrl`
2. Opens TCP + performs TLS handshake against that URL
3. Compares the observed certificate thumbprint against the expected Halibut
   cert thumbprint
4. Returns the probe result (`reachable`, `skipped`, `thumbprintMatches`,
   `detail`) alongside the generated script

The UI displays this result as a warning banner **before** the operator ships
the script to a new Tentacle owner — turning days of silent EOF loops into
an immediate, actionable error at the moment of misconfiguration.

If the probe reports `reachable=false`, the `Detail` field contains a
remediation hint pointing directly at the most likely gap (DNS, SLB listener
protocol, health check type, or security group). Follow the corresponding
section above.
