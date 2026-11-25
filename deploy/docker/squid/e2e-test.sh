#!/bin/bash
# End-to-end test of the all-in-one image, driven exactly the way a user drives it: docker run,
# the one-line installer, the setup wizard's HTTP API, docker restart / kill / re-create.
# Every path must end with an administrator who can sign in.
#
#   deploy/docker/squid/e2e-test.sh <image>
#
# Scenarios (own container + volume each, all removed on exit):
#   quickstart   documented `docker run` with no -v      -> wizard, anonymous-volume warning, healthcheck
#   wizard       no parameters, named volume             -> token-guarded wizard -> built-in DB -> sign in
#                                                          -> restart, kill -9 + start, re-create (upgrade) keep everything
#   wizard-ext   wizard against external PostgreSQL      -> probe, failed install, corrected retry, sign in
#   unattended   SQUID_ADMIN_PASSWORD with ; = " ' space -> built-in DB, no wizard, sign in
#   external     SQUID_DB + SQUID_ADMIN_USER/PASSWORD    -> sign in, built-in DB never started
#   generated    SQUID_DB only                           -> /data/initial-admin-password works
#   installer    install-squid.sh via `bash -s` (= curl | bash), with and without parameters
set -euo pipefail

IMAGE="${1:?usage: e2e-test.sh <image>}"
REPO_ROOT="$(cd "$(dirname "$0")/../../.." && pwd)"
RUN_ID="squid-e2e-$$-$RANDOM"
NETWORK="$RUN_ID-net"
READY_TIMEOUT=240
CREATED=()

log() { echo "[$(date +%H:%M:%S)] $*"; }

fail() {
    echo "FAIL: $*" >&2
    for c in ${CREATED[@]+"${CREATED[@]}"}; do echo "---- logs: $c" >&2; docker logs --tail 80 "$c" >&2 2>/dev/null || true; done
    exit 1
}

cleanup() {
    for c in ${CREATED[@]+"${CREATED[@]}"}; do docker rm -f -v "$c" >/dev/null 2>&1 || true; done
    for c in ${CREATED[@]+"${CREATED[@]}"}; do docker volume rm "$c-data" >/dev/null 2>&1 || true; done
    docker network rm "$NETWORK" >/dev/null 2>&1 || true
}
trap cleanup EXIT

json() { python3 -c 'import json,sys; print(json.dumps(sys.argv[1]))' "$1"; }

free_port() { python3 -c 'import socket; s=socket.socket(); s.bind(("127.0.0.1",0)); print(s.getsockname()[1]); s.close()'; }

host_port() { docker port "$1" 8080/tcp | head -n1 | sed 's/.*://'; }

base_url() { echo "http://127.0.0.1:$(host_port "$1")"; }

# run_squid <name> <volume|-> [docker args...]; '-' = no -v at all (anonymous volume).
run_squid() {
    local name="$1" volume="$2"; shift 2
    CREATED+=("$name")
    local mount=()
    [[ "$volume" != "-" ]] && mount=(-v "$volume:/data")
    docker run -d --name "$name" --network "$NETWORK" -p 127.0.0.1::8080 ${mount[@]+"${mount[@]}"} "$@" "$IMAGE" >/dev/null
}

status_of() { curl -fsS "$(base_url "$1")/api/setup/status" 2>/dev/null || true; }

wait_for_status() {
    local name="$1" expected="$2" waited=0
    until [[ "$(status_of "$name")" == *"\"required\":$expected"* ]]; do
        [[ "$(docker inspect -f '{{.State.Running}}' "$name")" == "true" ]] || fail "$name stopped while waiting for required=$expected"
        (( waited >= READY_TIMEOUT )) && fail "$name: /api/setup/status never reported required=$expected within ${READY_TIMEOUT}s (check: docker logs $name)"
        sleep 2; waited=$((waited + 2))
    done
}

sign_in() {
    local name="$1" user="$2" password="$3"
    local body
    body="$(curl -sS -X POST "$(base_url "$name")/auth/login" -H 'Content-Type: application/json' -d "{\"userName\":$(json "$user"),\"password\":$(json "$password")}")"
    [[ "$body" == *'"accessToken"'* ]] || fail "$name: sign-in as $user failed: $body"
}

setup_call() {
    local name="$1" method="$2" path="$3" body="${4:-}"
    local token
    token="$(docker exec "$name" cat /data/setup-token)"
    curl -sS -X "$method" "$(base_url "$name")/api/setup/$path" -H "X-Setup-Token: $token" -H 'Content-Type: application/json' ${body:+-d "$body"}
}

wait_for_install() {
    local name="$1" expected="$2" waited=0 progress
    while true; do
        progress="$(setup_call "$name" GET progress 2>/dev/null || true)"
        [[ "$progress" == *"\"state\":\"$expected\""* ]] && return 0
        [[ "$expected" == "Completed" && "$(status_of "$name")" == *'"required":false'* ]] && return 0
        [[ "$expected" == "Completed" && "$progress" == *'"state":"Failed"'* ]] && fail "$name: install failed: $progress"
        (( waited >= READY_TIMEOUT )) && fail "$name: install never reached $expected (last: $progress)"
        sleep 1; waited=$((waited + 1))
    done
}

assert_spa_served() {
    local html
    html="$(curl -fsS "$(base_url "$1")/setup")" || fail "$1: /setup did not return the web UI"
    [[ "$html" == *"<html"* ]] || fail "$1: /setup is not HTML"
}

wait_healthy() {
    local name="$1" waited=0
    until [[ "$(docker inspect -f '{{.State.Health.Status}}' "$name")" == "healthy" ]]; do
        (( waited >= READY_TIMEOUT )) && fail "$name: Docker healthcheck never became healthy"
        sleep 3; waited=$((waited + 3))
    done
}

builtin_postgres_running() { docker exec "$1" test -e /data/pgdata/postmaster.pid; }

start_external_postgres() {
    EXTERNAL_PG="$RUN_ID-postgres"
    CREATED+=("$EXTERNAL_PG")
    docker run -d --name "$EXTERNAL_PG" --network "$NETWORK" -e POSTGRES_PASSWORD=ext-pass -e POSTGRES_USER=squid -e POSTGRES_DB=postgres postgres:16-alpine >/dev/null
    local waited=0
    until docker exec "$EXTERNAL_PG" pg_isready -U squid >/dev/null 2>&1; do
        (( waited >= 60 )) && fail "external postgres did not become ready"
        sleep 2; waited=$((waited + 2))
    done
}

scenario_quickstart() {
    log "quickstart: documented docker run without -v"
    local name="$RUN_ID-quickstart"
    run_squid "$name" -
    wait_for_status "$name" true
    assert_spa_served "$name"
    [[ "$(status_of "$name")" == *'"dataVolumeWarning":"'* ]] || fail "quickstart: no anonymous-volume warning in setup status: $(status_of "$name")"
    grep -q 'anonymous Docker volume' <<< "$(docker logs "$name" 2>&1)" || fail "quickstart: no anonymous-volume warning in the log"
    local logs token
    logs="$(docker logs "$name" 2>&1)"
    token="$(docker exec "$name" cat /data/setup-token)"
    grep -q '/setup' <<< "$logs" || fail "quickstart: the setup page was not announced in the log"
    grep -q 'setup-token' <<< "$logs" || fail "quickstart: the log does not say where to read the setup token"
    [[ "$logs" != *"$token"* ]] || fail "quickstart: the setup token value was written to the log"
    wait_healthy "$name"
    log "quickstart: OK"
}

scenario_wizard() {
    log "wizard: no parameters, named volume"
    local volume="$RUN_ID-wizard-data" name="$RUN_ID-wizard"
    run_squid "$name" "$volume"
    wait_for_status "$name" true
    [[ "$(status_of "$name")" == *'"dataVolumeWarning":null'* ]] || fail "wizard: named volume wrongly reported as anonymous"

    local code
    code="$(curl -s -o /dev/null -w '%{http_code}' -X POST "$(base_url "$name")/api/setup/install" -H 'X-Setup-Token: wrong' -H 'Content-Type: application/json' -d '{}')"
    [[ "$code" == "401" ]] || fail "wizard: install with a wrong token returned $code, expected 401"

    local invalid
    invalid="$(setup_call "$name" POST install '{"database":{"mode":"Builtin"},"redis":{"mode":"Builtin"},"admin":{"userName":"owner","password":"short"},"server":{"externalUrl":"x"}}')"
    [[ "$invalid" == *'admin.password'* && "$invalid" == *'server.externalUrl'* ]] || fail "wizard: invalid request not rejected field by field: $invalid"

    setup_call "$name" POST install '{"database":{"mode":"Builtin"},"redis":{"mode":"Builtin"},"admin":{"userName":"owner","password":"Wizard-Pass-1"},"server":{"externalUrl":"http://localhost:8080"}}' >/dev/null
    wait_for_install "$name" Completed
    wait_for_status "$name" false
    sign_in "$name" owner Wizard-Pass-1
    builtin_postgres_running "$name" || fail "wizard: built-in PostgreSQL is not running"
    docker exec "$name" test ! -e /data/setup-token || fail "wizard: setup token survived the completed setup"
    grep -qx 600 <<< "$(docker exec "$name" bash -c 'stat -c %a /data/squid.json')" || fail "wizard: squid.json is not owner-only"

    log "wizard: restart"
    docker restart "$name" >/dev/null
    wait_for_status "$name" false
    sign_in "$name" owner Wizard-Pass-1

    log "wizard: kill -9 then start (crash recovery of the built-in database)"
    docker kill "$name" >/dev/null
    docker start "$name" >/dev/null
    wait_for_status "$name" false
    sign_in "$name" owner Wizard-Pass-1

    log "wizard: re-create the container on the same volume (upgrade path)"
    docker rm -f "$name" >/dev/null
    run_squid "$name-upgraded" "$volume"
    wait_for_status "$name-upgraded" false
    sign_in "$name-upgraded" owner Wizard-Pass-1

    grep -q 'Admin@123456' <<< "$(docker logs "$name-upgraded" 2>&1)" && fail "wizard: the well-known default password appeared in the log"
    log "wizard: OK"
}

scenario_wizard_external() {
    log "wizard-ext: wizard against an external PostgreSQL"
    local name="$RUN_ID-wizard-ext"
    run_squid "$name" "$name-data"
    wait_for_status "$name" true

    local probe
    probe="$(setup_call "$name" POST database/test "{\"mode\":\"External\",\"connectionString\":\"Host=$EXTERNAL_PG;Database=squid_wiz;Username=squid;Password=ext-pass\"}")"
    [[ "$probe" == *'"success":true'* && "$probe" == *'"databaseExists":false'* && "$probe" == *'"canCreateDatabase":true'* ]] || fail "wizard-ext: probe of a missing database: $probe"

    log "wizard-ext: wrong password fails, nothing persisted"
    setup_call "$name" POST install "{\"database\":{\"mode\":\"External\",\"connectionString\":\"Host=$EXTERNAL_PG;Database=squid_wiz;Username=squid;Password=wrong\",\"createIfMissing\":true},\"redis\":{\"mode\":\"Builtin\"},\"admin\":{\"userName\":\"admin\",\"password\":\"Wizard-Ext-1\"},\"server\":{\"externalUrl\":\"http://localhost:8080\"}}" >/dev/null
    wait_for_install "$name" Failed
    [[ "$(status_of "$name")" == *'"required":true'* ]] || fail "wizard-ext: a failed install left setup mode"

    log "wizard-ext: corrected retry"
    setup_call "$name" POST install "{\"database\":{\"mode\":\"External\",\"connectionString\":\"Host=$EXTERNAL_PG;Database=squid_wiz;Username=squid;Password=ext-pass\",\"createIfMissing\":true},\"redis\":{\"mode\":\"Builtin\"},\"admin\":{\"userName\":\"admin\",\"password\":\"Wizard-Ext-1\"},\"server\":{\"externalUrl\":\"http://localhost:8080\"}}" >/dev/null
    wait_for_install "$name" Completed
    wait_for_status "$name" false
    sign_in "$name" admin Wizard-Ext-1
    builtin_postgres_running "$name" && fail "wizard-ext: built-in PostgreSQL started although External was chosen"
    log "wizard-ext: OK"
}

scenario_unattended() {
    log "unattended: SQUID_ADMIN_PASSWORD with shell / connection-string metacharacters"
    local name="$RUN_ID-unattended" password='Pa ss;w=rd"x'"'"'y$HOME'
    run_squid "$name" "$name-data" -e "SQUID_ADMIN_PASSWORD=$password"
    wait_for_status "$name" false
    sign_in "$name" admin "$password"
    builtin_postgres_running "$name" || fail "unattended: built-in PostgreSQL is not running"
    log "unattended: OK"
}

scenario_external() {
    log "external: SQUID_DB + custom administrator"
    local name="$RUN_ID-external"
    run_squid "$name" "$name-data" -e "SQUID_DB=Host=$EXTERNAL_PG;Database=squid_ext;Username=squid;Password=ext-pass" -e SQUID_ADMIN_USER=ops -e SQUID_ADMIN_PASSWORD=External-Pass-1
    wait_for_status "$name" false
    sign_in "$name" ops External-Pass-1
    builtin_postgres_running "$name" && fail "external: built-in PostgreSQL was started although SQUID_DB was given"

    log "generated: SQUID_DB without an administrator password"
    local generated="$RUN_ID-generated" password
    run_squid "$generated" "$generated-data" -e "SQUID_DB=Host=$EXTERNAL_PG;Database=squid_gen;Username=squid;Password=ext-pass"
    wait_for_status "$generated" false
    password="$(docker exec "$generated" cat /data/initial-admin-password | tr -d '\r\n')"
    [[ -n "$password" ]] || fail "generated: no initial-admin-password file"
    sign_in "$generated" admin "$password"
    log "external + generated: OK"
}

# Same as `curl -fsSL .../install-squid.sh | bash -s -- <args>`: script on stdin, $0 = bash.
run_installer() {
    bash -s -- --image "$IMAGE" "$@" < "$REPO_ROOT/deploy/scripts/install-squid.sh"
}

scenario_installer() {
    log "installer: no parameters (curl | bash)"
    local name="$RUN_ID-installer" port output
    port="$(free_port)"
    CREATED+=("$name")
    output="$(run_installer --name "$name" --volume "$name-data" --port "$port" --polling-port "$(free_port)")"
    local link
    link="$(echo "$output" | grep -o 'http://[^ ]*/setup?token=[A-Za-z0-9]*' | head -n1)"
    [[ -n "$link" ]] || fail "installer: no setup link printed: $output"
    local token="${link##*token=}"
    [[ "$token" == "$(docker exec "$name" cat /data/setup-token)" ]] || fail "installer: printed token does not match the server's"
    grep -q '<html' <<< "$(curl -fsS "http://127.0.0.1:$port/setup")" || fail "installer: the printed port does not serve the UI"

    log "installer: re-run on an existing container only reports"
    grep -q '/setup?token=' <<< "$(run_installer --name "$name" --volume "$name-data" --port "$port")" || fail "installer: re-run did not report the setup link"

    log "installer: external database + custom administrator"
    local param="$RUN_ID-installer-param" pport
    pport="$(free_port)"
    CREATED+=("$param")
    output="$(run_installer --name "$param" --volume "$param-data" --port "$pport" --polling-port "$(free_port)" --db "Host=$RUN_ID-postgres;Database=squid_inst;Username=squid;Password=ext-pass" --admin-user deployer --admin-password 'Install-Pass-1' --url "http://127.0.0.1:$pport" --network "$NETWORK")"
    [[ "$output" == *"Squid is ready"* && "$output" == *"deployer"* ]] || fail "installer: parameter install did not report ready: $output"
    sign_in "$param" deployer Install-Pass-1
    log "installer: OK"
}

docker network create "$NETWORK" >/dev/null
start_external_postgres

scenario_quickstart
scenario_wizard
scenario_wizard_external
scenario_unattended
scenario_external
scenario_installer

log "all scenarios passed"
