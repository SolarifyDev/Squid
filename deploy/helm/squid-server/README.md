# squid-server

Squid Server — the web UI, API and agent polling endpoint, with built-in or external PostgreSQL and Redis

## Install

```bash
helm install squid oci://registry-1.docker.io/squidcd/squid-server
```

With nothing set, Squid uses its built-in PostgreSQL and Redis (data on the persistent volume) and
prints a setup link in its log:

```bash
kubectl logs deploy/squid-squid-server | grep "setup?token="
```

To skip the setup page, give the administrator password (and your own PostgreSQL for production):

```bash
helm install squid oci://registry-1.docker.io/squidcd/squid-server \
  --set admin.password='change-me-please' \
  --set database.connectionString='Host=postgres;Database=squid;Username=squid;Password=change-me' \
  --set ingress.enabled=true --set ingress.host=squid.example.com \
  --set polling.host=squid-polling.example.com
```

## Agent polling

Agents (Tentacles) connect to the server's Halibut polling port, which runs its own mutual TLS, so
nothing in front of it may terminate TLS. `polling.mode` picks how it is exposed:

| Mode | What it creates | Use it when |
|---|---|---|
| `loadBalancer` (default) | TCP LoadBalancer Service on 10943 | Any cloud; on Alibaba Cloud start from [examples/alibaba-cloud.yaml](https://github.com/SolarifyDev/Squid/blob/main/deploy/helm/squid-server/examples/alibaba-cloud.yaml) |
| `ingressPassthrough` | Ingress on port 443 routed by host name, TLS passed through | The NGINX ingress controller runs with `--enable-ssl-passthrough` and agents should only need 443 |
| `nodePort` | NodePort Service | Bare-metal clusters |
| `none` | Nothing | Agents run inside the cluster |

Set `polling.host` to the name agents use, so the install scripts Squid generates carry the right
polling URL.

## Moving an existing Squid onto this chart

Point `database.connectionString` at the existing database and put the existing install's keys in
`existingSecret` (`master-key`, `server-certificate`, `server-certificate-password`, `jwt-key`).
Without them the new install generates its own: registered agents stop trusting the server and
encrypted variables can no longer be read.

## Requirements

Kubernetes: `>=1.23.0-0`

## Values

| Key | Type | Default | Description |
|-----|------|---------|-------------|
| admin.password | string | `""` | Administrator password. Empty shows the setup page instead (link in the pod log) |
| admin.username | string | `"admin"` | Administrator created on first start |
| affinity | object | `{}` | Affinity |
| database.connectionString | string | `""` | PostgreSQL connection string. Empty uses the built-in PostgreSQL on the persistent volume |
| existingSecret | string | `""` | Existing Secret to read credentials from instead of the values above (the chart then creates no Secret). Optional keys: `admin-password`, `database`, `redis`. When moving an existing Squid onto this chart (same database), also add its `master-key` (Security:VariableEncryption:MasterKey), `server-certificate` and `server-certificate-password` (SelfCert:Base64 / SelfCert:Password) and `jwt-key` (Authentication:Jwt:SymmetricKey): without them the new install generates its own, every registered agent stops trusting the server and encrypted variables can no longer be read. |
| externalUrl | string | `""` | Address users and agents open the web UI at. Empty derives it from `ingress.host` |
| extraEnv | list | `[]` | Extra environment variables for the server container |
| fullnameOverride | string | `""` | Override the full resource name |
| image.pullPolicy | string | `"IfNotPresent"` | Image pull policy |
| image.repository | string | `"squidcd/squid"` | Server image: the all-in-one Squid image (web UI, API, built-in PostgreSQL and Redis) |
| image.tag | string | `""` | Image tag. Defaults to the chart's appVersion |
| imagePullSecrets | list | `[]` | Secrets for pulling the image from a private registry, e.g. `[{name: regcred}]` |
| ingress.annotations | object | `{}` | Ingress annotations, e.g. for cert-manager |
| ingress.className | string | `""` | Ingress class |
| ingress.enabled | bool | `false` | Expose the web UI and API through an Ingress |
| ingress.host | string | `""` | Host name of the web UI (also the derived `externalUrl`) |
| ingress.tls.secretName | string | `""` | TLS secret for `ingress.host`. Empty serves plain http |
| nameOverride | string | `""` | Override the chart name |
| nodeSelector | object | `{}` | Node selector |
| persistence.enabled | bool | `true` | Keep the configuration, server certificate, generated keys and built-in database on a volume. Losing them means every registered agent stops trusting the server |
| persistence.existingClaim | string | `""` | Use this existing PersistentVolumeClaim instead of creating one |
| persistence.size | string | `"10Gi"` | Volume size |
| persistence.storageClass | string | `""` | Storage class. Empty uses the cluster default |
| podAnnotations | object | `{}` | Pod annotations |
| podLabels | object | `{}` | Pod labels |
| podSecurityContext | object | `{}` | Pod security context |
| polling.host | string | `""` | Host name agents use. Required for `ingressPassthrough`; otherwise derives `pollingUrl` |
| polling.ingressPassthrough.annotations | object | `{}` | Extra annotations on the passthrough Ingress |
| polling.ingressPassthrough.className | string | `""` | Ingress class of the passthrough Ingress |
| polling.loadBalancer.annotations | object | `{}` | Annotations on the LoadBalancer Service, e.g. cloud load balancer settings |
| polling.loadBalancer.loadBalancerIP | string | `""` | Fixed load balancer IP, where the cloud supports it |
| polling.mode | string | `"loadBalancer"` | enable-ssl-passthrough). `nodePort`: a NodePort Service. `none`: not exposed outside the cluster |
| polling.nodePort.port | string | `""` | Node port. Empty lets Kubernetes assign one |
| polling.port | int | `10943` | Halibut polling port |
| pollingUrl | string | `""` | Address agents poll. Empty derives it from `polling.host` and `polling.mode` |
| redis.connectionString | string | `""` | Redis connection string. Empty uses the built-in Redis |
| resources | object | `{}` | Container resources |
| securityContext | object | `{}` | Container security context. The image starts as root so the built-in PostgreSQL can switch to its own user |
| service.port | int | `8080` | Service port for the web UI and API |
| service.type | string | `"ClusterIP"` | Service type for the web UI and API |
| serviceAccount.annotations | object | `{}` | Service account annotations, e.g. for cloud workload identity |
| serviceAccount.automount | bool | `false` | Mount the service account's API token. The server does not call the Kubernetes API itself |
| serviceAccount.create | bool | `true` | Create a service account for the server |
| serviceAccount.name | string | `""` | Service account name. Empty uses the release's full name |
| tolerations | list | `[]` | Tolerations |
