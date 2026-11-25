#!/usr/bin/env bash
# Render-level tests for the squid-server chart: every documented value combination renders the
# resources and settings it promises, and invalid combinations fail with a clear message.
#
#   deploy/helm/squid-server/tests/template-test.sh
#
# Needs only `helm`. Exits non-zero on the first failed assertion.
set -euo pipefail

CHART="$(cd "$(dirname "$0")/.." && pwd)"
PASSED=0

fail() { echo "FAIL: $CURRENT — $*" >&2; exit 1; }

render() { helm template squid "$CHART" --namespace squid "$@"; }

# Renders and asserts; usage: case_ "name" [helm args...]; then assertions on $OUT.
case_() { CURRENT="$1"; shift; OUT="$(render "$@")" || fail "helm template failed"; PASSED=$((PASSED + 1)); }

has() { grep -qF -- "$1" <<< "$OUT" || fail "expected to find: $1"; }
lacks() { grep -qF -- "$1" <<< "$OUT" && fail "did not expect: $1"; return 0; }

# Asserts the env entry NAME (its lines up to the next "- name:") contains TEXT, e.g. a value or secret ref.
env_entry() { awk -v n="- name: $1" 'index($0, n) { on = 1; print; next } on && /- name:/ { exit } on { print }' <<< "$OUT"; }
env_has() { grep -qF -- "$2" <<< "$(env_entry "$1")" || fail "env $1 should contain: $2"; }

fails_with() {
  CURRENT="$1"; local message="$2"; shift 2
  local err
  err="$(render "$@" 2>&1 >/dev/null)" && fail "expected helm template to fail"
  grep -qF -- "$message" <<< "$err" || fail "expected error containing: $message (got: $err)"
  PASSED=$((PASSED + 1))
}

CURRENT="lint"; helm lint "$CHART" >/dev/null || fail "helm lint failed"; PASSED=$((PASSED + 1))

case_ "defaults: all-in-one, setup wizard, data on a volume, polling on a LoadBalancer"
has "image: \"squidcd/squid:"
has "type: Recreate"
has "kind: PersistentVolumeClaim"
has "claimName: squid-squid-server-data"
has "mountPath: /data"
has "containerPort: 8080"
has "containerPort: 10943"
has "type: LoadBalancer"
lacks "SQUID_ADMIN_PASSWORD"
lacks "SQUID_DB"
lacks "SQUID_URL"
lacks "SQUID_POLLING_URL"
lacks "kind: Ingress"
lacks "kind: Secret"

case_ "image tag override" --set image.tag=1.9.11
has "image: \"squidcd/squid:1.9.11\""

case_ "admin password skips the wizard via a chart-managed secret" --set admin.password=s3cret --set admin.username=ops
has "kind: Secret"
has "admin-password: \"czNjcmV0\""
env_has SQUID_ADMIN_PASSWORD "name: squid-squid-server"
env_has SQUID_ADMIN_PASSWORD "key: admin-password"
env_has SQUID_ADMIN_USER "value: \"ops\""

case_ "existing secret supplies credentials; chart renders no secret" --set existingSecret=my-squid --set admin.password=ignored
lacks "kind: Secret"
env_has SQUID_ADMIN_PASSWORD "name: my-squid"
env_has SQUID_DB "name: my-squid"
env_has SQUID_REDIS "name: my-squid"
has "optional: true"

case_ "existing secret can carry an existing install's keys, so its agents and encrypted data survive the move" --set existingSecret=my-squid
env_has Security__VariableEncryption__MasterKey "key: master-key"
env_has SelfCert__Base64 "key: server-certificate"
env_has SelfCert__Password "key: server-certificate-password"
env_has Authentication__Jwt__SymmetricKey "key: jwt-key"
env_has SelfCert__Base64 "optional: true"

case_ "without an existing secret the install generates its own keys" --set admin.password=x
lacks "Security__VariableEncryption__MasterKey"
lacks "SelfCert__Base64"

case_ "external PostgreSQL and Redis" --set database.connectionString="Host=pg;Database=squid" --set redis.connectionString="redis:6379"
has "database: \"SG9zdD1wZztEYXRhYmFzZT1zcXVpZA==\""
has "redis: \"cmVkaXM6NjM3OQ==\""
env_has SQUID_DB "key: database"
env_has SQUID_REDIS "key: redis"

case_ "ingress with TLS derives the external URL" --set ingress.enabled=true --set ingress.host=squid.example.com --set ingress.tls.secretName=squid-tls --set ingress.className=nginx
has "kind: Ingress"
has "host: \"squid.example.com\""
has "secretName: squid-tls"
has "ingressClassName: nginx"
env_has SQUID_URL "value: \"https://squid.example.com\""

case_ "ingress without TLS derives an http external URL" --set ingress.enabled=true --set ingress.host=squid.example.com
env_has SQUID_URL "value: \"http://squid.example.com\""

case_ "explicit URLs win over derived ones" --set ingress.enabled=true --set ingress.host=squid.example.com --set externalUrl=https://ci.example.com --set polling.host=agents.example.com --set pollingUrl=https://agents.example.com:9999
env_has SQUID_URL "value: \"https://ci.example.com\""
env_has SQUID_POLLING_URL "value: \"https://agents.example.com:9999\""

case_ "loadBalancer polling: annotations pass through, host derives the polling URL" --set polling.host=agents.example.com --set-string 'polling.loadBalancer.annotations.example\.com/protocol=tcp' --set polling.loadBalancer.loadBalancerIP=10.0.0.5
has "example.com/protocol: tcp"
has "loadBalancerIP: 10.0.0.5"
env_has SQUID_POLLING_URL "value: \"https://agents.example.com:10943\""

case_ "ingressPassthrough polling: SNI passthrough ingress to the Halibut port, URL on 443" --set polling.mode=ingressPassthrough --set polling.host=agents.example.com --set polling.ingressPassthrough.className=nginx
has "nginx.ingress.kubernetes.io/ssl-passthrough: \"true\""
has "host: \"agents.example.com\""
has "name: halibut"
lacks "type: LoadBalancer"
env_has SQUID_POLLING_URL "value: \"https://agents.example.com\""

case_ "nodePort polling: fixed node port, URL uses it" --set polling.mode=nodePort --set polling.nodePort.port=30943 --set polling.host=node.example.com
has "type: NodePort"
has "nodePort: 30943"
env_has SQUID_POLLING_URL "value: \"https://node.example.com:30943\""

case_ "polling disabled: no polling service or ingress" --set polling.mode=none
lacks "type: LoadBalancer"
lacks "ssl-passthrough"
lacks "SQUID_POLLING_URL"

case_ "existing claim: no PVC rendered" --set persistence.existingClaim=squid-data
lacks "kind: PersistentVolumeClaim"
has "claimName: squid-data"

case_ "persistence disabled: emptyDir" --set persistence.enabled=false
lacks "kind: PersistentVolumeClaim"
has "emptyDir: {}"

case_ "storage class and size" --set persistence.storageClass=fast --set persistence.size=50Gi
has "storageClassName: \"fast\""
has "storage: \"50Gi\""

case_ "extra env and resources pass through" --set 'extraEnv[0].name=TZ' --set 'extraEnv[0].value=Asia/Hong_Kong' --set resources.requests.memory=1Gi
env_has TZ "value: Asia/Hong_Kong"
has "memory: 1Gi"

case_ "service account: own account by default, no API token mounted"
has "kind: ServiceAccount"
has "serviceAccountName: squid-squid-server"
has "automountServiceAccountToken: false"

case_ "service account: existing one" --set serviceAccount.create=false --set serviceAccount.name=ops-sa
lacks "kind: ServiceAccount"
has "serviceAccountName: ops-sa"

case_ "pod metadata, pull secrets and security contexts pass through" --set 'imagePullSecrets[0].name=regcred' --set-string podAnnotations.team=platform --set-string podLabels.tier=cd --set podSecurityContext.fsGroup=999 --set securityContext.allowPrivilegeEscalation=false
has "- name: regcred"
has "team: platform"
has "tier: cd"
has "fsGroup: 999"
has "allowPrivilegeEscalation: false"

fails_with "ingressPassthrough requires a host" "polling.host is required" --set polling.mode=ingressPassthrough
fails_with "ingress requires a host" "ingress.host is required" --set ingress.enabled=true
fails_with "schema: unknown polling mode is rejected" "polling/mode" --set polling.mode=bogus
fails_with "schema: unknown service type is rejected" "service/type" --set service.type=Bogus
fails_with "schema: wrong value type is rejected" "persistence/enabled" --set-string persistence.enabled=yes
fails_with "schema: misspelled top-level value is rejected" "adminPassword" --set adminPassword=x

echo "All $PASSED chart template checks passed"
