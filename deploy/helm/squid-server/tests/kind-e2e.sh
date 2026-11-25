#!/usr/bin/env bash
# End-to-end test of the squid-server chart on a Kind cluster with real agents.
#
#   deploy/helm/squid-server/tests/kind-e2e.sh
#
# Installs the chart twice, once per way agents can reach the polling port:
#   passthrough   polling.mode=ingressPassthrough: ingress-nginx (--enable-ssl-passthrough) shares
#                 port 443 between the web UI and Halibut, routed by host name
#   nodeport      polling.mode=nodePort: a plain TCP Service, the same path a LoadBalancer takes
# For each it checks that the URLs the chart derives reach the server, registers a polling Tentacle
# from the install script the server generates, and runs a health check through Halibut. It then
# restarts the passthrough server pod and checks the agent still trusts it (certificate kept on the
# volume).
#
# Needs docker, kind, kubectl, helm, python3. Environment:
#   IMAGE          server image (default squidcd/squid:latest)
#   TENTACLE_IMAGE agent image (default squidcd/squid-tentacle-linux:latest)
#   KEEP=1         leave the cluster and agents running afterwards
set -euo pipefail

CHART="$(cd "$(dirname "$0")/.." && pwd)"
CLUSTER=squid-helm-e2e
IMAGE="${IMAGE:-squidcd/squid:latest}"
TENTACLE_IMAGE="${TENTACLE_IMAGE:-squidcd/squid-tentacle-linux:latest}"
INGRESS_NGINX_VERSION=controller-v1.11.3
CURL_IMAGE=curlimages/curl:8.10.1
ADMIN_PASSWORD="E2e-$(openssl rand -hex 8)"
WORK="$(mktemp -d)"
AGENTS=()

log() { echo "[$(date +%H:%M:%S)] $*"; }
fail() { echo "FAIL: $*" >&2; diagnostics; exit 1; }
k() { kubectl --context "kind-$CLUSTER" "$@"; }

diagnostics() {
  log "--- diagnostics"
  k get pods -A 2>/dev/null || true
  for ns in passthrough nodeport; do k -n "$ns" logs deploy/squid-squid-server --tail=40 2>/dev/null || true; done
  for agent in ${AGENTS[@]+"${AGENTS[@]}"}; do docker logs --tail=30 "$agent" 2>&1 || true; done
}

cleanup() {
  [ "${KEEP:-}" = "1" ] && { log "KEEP=1: leaving cluster $CLUSTER and agents"; return; }
  for agent in ${AGENTS[@]+"${AGENTS[@]}"}; do docker rm -f "$agent" >/dev/null 2>&1 || true; done
  kind delete cluster --name "$CLUSTER" >/dev/null 2>&1 || true
  rm -rf "$WORK"
}
trap cleanup EXIT

# Preloads an image into the node from the host (for hosts whose node cannot reach the registry).
# Best effort: `kind load` cannot import multi-platform images from Docker's containerd image store
# (GitHub runners), and then the node pulls the image itself.
load_image() {
  docker image inspect "$1" >/dev/null 2>&1 || docker pull -q "$1" >/dev/null
  kind load docker-image "$1" --name "$CLUSTER" >/dev/null 2>&1 || log "could not preload $1; the node pulls it"
}

create_cluster() {
  log "creating Kind cluster $CLUSTER"
  printf 'kind: Cluster\napiVersion: kind.x-k8s.io/v1alpha4\nnodes:\n  - role: control-plane\n    labels:\n      ingress-ready: "true"\n' > "$WORK/kind.yaml"
  kind delete cluster --name "$CLUSTER" >/dev/null 2>&1 || true
  kind create cluster --name "$CLUSTER" --config "$WORK/kind.yaml" --wait 120s >/dev/null
  NODE_IP="$(docker inspect -f '{{.NetworkSettings.Networks.kind.IPAddress}}' "$CLUSTER-control-plane")"
}

install_ingress_nginx() {
  log "installing ingress-nginx $INGRESS_NGINX_VERSION with --enable-ssl-passthrough"
  curl -fsSL "https://raw.githubusercontent.com/kubernetes/ingress-nginx/$INGRESS_NGINX_VERSION/deploy/static/provider/kind/deploy.yaml" \
    | sed -E 's#(image: [^@ ]+)@sha256:[0-9a-f]+#\1#; s#imagePullPolicy: Always#imagePullPolicy: IfNotPresent#' \
    | awk '{ print } /- --election-id=ingress-nginx-leader/ { sub(/--election-id=ingress-nginx-leader/, "--enable-ssl-passthrough"); print }' > "$WORK/ingress-nginx.yaml"
  grep -q -- '--enable-ssl-passthrough' "$WORK/ingress-nginx.yaml" || fail "could not add --enable-ssl-passthrough to the ingress-nginx manifest"

  for image in $(grep -oE 'image: [^ ]+' "$WORK/ingress-nginx.yaml" | awk '{print $2}' | sort -u); do load_image "$image"; done

  k apply -f "$WORK/ingress-nginx.yaml" >/dev/null
  k -n ingress-nginx wait --for=condition=complete job --all --timeout=180s >/dev/null || fail "ingress-nginx admission jobs did not complete within 180s"
  k -n ingress-nginx wait --for=condition=ready pod -l app.kubernetes.io/component=controller --timeout=180s >/dev/null || fail "ingress-nginx controller not ready within 180s — check: kubectl -n ingress-nginx describe pod"
}

# install_squid NAMESPACE [helm args...]
install_squid() {
  local ns="$1"; shift
  log "$ns: helm install"
  helm --kube-context "kind-$CLUSTER" install squid "$CHART" --namespace "$ns" --create-namespace \
    --set image.repository="${IMAGE%:*}" --set image.tag="${IMAGE##*:}" \
    --set admin.password="$ADMIN_PASSWORD" "$@" >/dev/null
}

wait_ready() {
  k -n "$1" rollout status deploy/squid-squid-server --timeout=600s >/dev/null || fail "$1: server not ready within 10 min — check: kubectl -n $1 logs deploy/squid-squid-server"
  for _ in $(seq 1 60); do http "$2" GET /api/server-configuration -o /dev/null -w '%{http_code}' | grep -q 401 && return; sleep 5; done
  fail "$1: API at $2 not answering through the ingress"
}

# http HOST METHOD PATH [curl args...] — from a container on the Kind network, HOST resolved to the node.
http() {
  local host="$1" method="$2" path="$3"; shift 3
  docker run --rm --network kind --add-host "$host:$NODE_IP" "$CURL_IMAGE" -s -X "$method" "http://$host$path" "$@"
}

# json PATH — reads one field, e.g. json "['data']['accessToken']".
json() { python3 -c "import json,sys; d=json.load(sys.stdin); print(eval('d' + sys.argv[1]))" "$1"; }

# The "Docker Run" variant of a generate-tentacle-install-script response.
docker_run_script() { python3 -c "import json,sys; print(next(s['content'] for s in json.load(sys.stdin)['data']['scripts'] if 'Docker Run' in (s.get('label') or s.get('name') or '')))"; }

login() {
  TOKEN="$(http "$1" POST /auth/login -H 'Content-Type: application/json' -d "{\"userName\":\"admin\",\"password\":\"$ADMIN_PASSWORD\"}" | json "['data']['accessToken']")"
  [ -n "$TOKEN" ] || fail "login to $1 failed"
}

api() { local host="$1" method="$2" path="$3"; shift 3; http "$host" "$method" "$path" -H "Authorization: Bearer $TOKEN" -H "X-Space-Id: 1" -H 'Content-Type: application/json' "$@"; }

# check_urls HOST EXPECTED_SERVER_URL EXPECTED_COMMS_URL — the chart's derived URLs reached the server.
check_urls() {
  local config; config="$(api "$1" GET /api/server-configuration)"
  [ "$(json "['serverUrl']" <<< "$config")" = "$2" ] || fail "$1: server URL is $(json "['serverUrl']" <<< "$config"), expected $2"
  [ "$(json "['commsUrl']" <<< "$config")" = "$3" ] || fail "$1: polling URL is $(json "['commsUrl']" <<< "$config"), expected $3"
  THUMBPRINT="$(json "['serverThumbprint']" <<< "$config")"
}

# run_agent NAME SERVER_HOST POLLING_HOST — registers a polling Tentacle the way the generated install script does.
run_agent() {
  local name="$1" host="$2" polling_host="$3" script api_key comms_url
  script="$(api "$host" POST /api/machines/generate-tentacle-install-script -d "{\"machineName\":\"$name\",\"environments\":[\"Production\"],\"tags\":[\"web\"],\"spaceId\":1,\"communicationMode\":\"Polling\",\"operatingSystem\":\"Linux\",\"serverUrl\":\"http://$host\",\"serverCommsUrl\":\"$COMMS_URL\"}" | docker_run_script)"
  api_key="$(grep -oE 'Tentacle__ApiKey="[^"]+"' <<< "$script" | cut -d'"' -f2)"
  comms_url="$(grep -oE 'Tentacle__ServerCommsUrl="[^"]+"' <<< "$script" | cut -d'"' -f2)"
  [ -n "$api_key" ] || fail "$name: no API key in the generated install script"
  [ "$comms_url" = "$COMMS_URL" ] || fail "$name: install script polls $comms_url, expected $COMMS_URL"

  docker rm -f "$name" >/dev/null 2>&1 || true
  docker run -d --name "$name" --network kind --add-host "$host:$NODE_IP" --add-host "$polling_host:$NODE_IP" \
    -e Tentacle__ServerUrl="http://$host" -e Tentacle__ApiKey="$api_key" -e Tentacle__Roles=web -e Tentacle__Environments=Production \
    -e Tentacle__SpaceId=1 -e Tentacle__ServerCommsUrl="$comms_url" -e Tentacle__ServerCertificate="$THUMBPRINT" -e Tentacle__MachineName="$name" \
    "$TENTACLE_IMAGE" >/dev/null
  AGENTS+=("$name")

  for _ in $(seq 1 60); do
    MACHINE_ID="$(docker logs "$name" 2>&1 | grep -oE 'Registered with server. MachineId=[0-9]+' | grep -oE '[0-9]+$' | head -1 || true)"
    [ -n "$MACHINE_ID" ] && docker logs "$name" 2>&1 | grep -q "Secure connection established" && return
    sleep 2
  done
  fail "$name: did not register and open a polling connection within 120s — check: docker logs $name"
}

health_check() {
  local result
  for _ in $(seq 1 12); do
    result="$(api "$1" POST "/api/machines/$MACHINE_ID/health-check" -d '{}')"
    [ "$(json "['data']['successful']" <<< "$result" 2>/dev/null)" = "True" ] && return
    sleep 5
  done
  fail "$1: health check of machine $MACHINE_ID through Halibut did not succeed: $result"
}

scenario_passthrough() {
  local host=squid.e2e.test polling=agents.e2e.test
  install_squid passthrough --set ingress.enabled=true --set ingress.className=nginx --set ingress.host=$host \
    --set polling.mode=ingressPassthrough --set polling.host=$polling --set polling.ingressPassthrough.className=nginx
  wait_ready passthrough $host
  login $host

  COMMS_URL="https://$polling"
  check_urls $host "http://$host" "$COMMS_URL"
  log "passthrough: URLs derived by the chart reached the server ($COMMS_URL)"

  run_agent passthrough-agent $host $polling
  docker logs passthrough-agent 2>&1 | grep -q "Server at .*:443 identified by thumbprint: $THUMBPRINT" || fail "passthrough: agent did not reach Squid's certificate on port 443"
  health_check $host
  log "passthrough: agent polls on 443 and passed a health check"

  local before="$THUMBPRINT"
  k -n passthrough delete pod -l app.kubernetes.io/name=squid-server --wait=true >/dev/null
  wait_ready passthrough $host
  login $host
  check_urls $host "http://$host" "$COMMS_URL"
  [ "$THUMBPRINT" = "$before" ] || fail "passthrough: server certificate changed across a pod restart ($before -> $THUMBPRINT); agents would stop trusting it"
  health_check $host
  log "passthrough: after a pod restart the certificate is unchanged and the agent still passes a health check"
}

scenario_nodeport() {
  local host=squid-np.e2e.test node_port=30943
  install_squid nodeport --set ingress.enabled=true --set ingress.className=nginx --set ingress.host=$host \
    --set polling.mode=nodePort --set polling.nodePort.port=$node_port --set polling.host=node.e2e.test
  wait_ready nodeport $host
  login $host

  COMMS_URL="https://node.e2e.test:$node_port"
  check_urls $host "http://$host" "$COMMS_URL"
  log "nodeport: URLs derived by the chart reached the server ($COMMS_URL)"

  run_agent nodeport-agent $host node.e2e.test
  health_check $host
  log "nodeport: agent polls through the TCP Service and passed a health check"
}

command -v kind >/dev/null && command -v helm >/dev/null && command -v kubectl >/dev/null || { echo "needs kind, helm and kubectl" >&2; exit 1; }

create_cluster
install_ingress_nginx
for image in "$IMAGE" "$TENTACLE_IMAGE" "$CURL_IMAGE"; do docker image inspect "$image" >/dev/null 2>&1 || docker pull -q "$image" >/dev/null; done
load_image "$IMAGE"

scenario_passthrough
scenario_nodeport

log "All squid-server chart E2E scenarios passed"
