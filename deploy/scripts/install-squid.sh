#!/bin/bash
set -euo pipefail

# Squid Server one-line installer (Docker).
#
#   curl -fsSL https://raw.githubusercontent.com/SolarifyDev/Squid/main/deploy/scripts/install-squid.sh | bash
#
# With no options Squid starts with a built-in database and prints a setup link: open it, create
# the administrator and you are done. Options configure everything up front instead (no wizard):
#
#   --db <connection string>     External PostgreSQL, e.g. "Host=db;Database=squid;Username=squid;Password=..."
#   --redis <connection string>  External Redis, e.g. "redis:6379,password=..."      (default: built-in)
#   --admin-user <name>          Administrator user name                              (default: admin)
#   --admin-password <password>  Administrator password; skips the wizard            (default with --db: generated)
#   --url <url>                  External URL users and agents reach Squid on
#   --polling-url <url>          URL agents poll (default: external URL host + polling port)
#   --port <port>                Host port for the web UI / API                      (default: 8080)
#   --polling-port <port>        Host port for agent polling                         (default: 10943)
#   --name <name>                Container name                                       (default: squid)
#   --volume <name>              Docker volume for /data                              (default: squid-data)
#   --image <image>              Image                                                (default: squidcd/squid:latest)
#   --network <name>             Docker network to join (e.g. the one your PostgreSQL runs in)

IMAGE="squidcd/squid:latest"
NAME="squid"
VOLUME="squid-data"
PORT="8080"
POLLING_PORT="10943"
DB=""
REDIS=""
ADMIN_USER=""
ADMIN_PASSWORD=""
URL=""
POLLING_URL=""
NETWORK=""
READY_TIMEOUT_SECONDS=300

usage() {
    cat <<'HELP'
Usage: install-squid.sh [options]

  No options: start with a built-in database and print a setup link.

  --db <connection string>     External PostgreSQL (skips the wizard)
  --redis <connection string>  External Redis (default: built-in)
  --admin-user <name>          Administrator user name (default: admin)
  --admin-password <password>  Administrator password (skips the wizard)
  --url <url>                  External URL users and agents reach Squid on
  --polling-url <url>          URL agents poll
  --port <port>                Host port for the web UI / API (default: 8080)
  --polling-port <port>        Host port for agent polling (default: 10943)
  --name <name>                Container name (default: squid)
  --volume <name>              Docker volume for /data (default: squid-data)
  --image <image>              Image (default: squidcd/squid:latest)
  --network <name>             Docker network to join (e.g. the one your PostgreSQL runs in)
HELP
}

need_value() {
    [[ $# -ge 2 && -n "$2" && "$2" != --* ]] || { echo "Error: $1 needs a value (see --help)" >&2; exit 1; }
}

while [[ $# -gt 0 ]]; do
    case $1 in
        -h|--help) usage; exit 0 ;;
        --*) need_value "$@" ;;
    esac
    case $1 in
        --db) DB="$2"; shift 2 ;;
        --redis) REDIS="$2"; shift 2 ;;
        --admin-user) ADMIN_USER="$2"; shift 2 ;;
        --admin-password) ADMIN_PASSWORD="$2"; shift 2 ;;
        --url) URL="$2"; shift 2 ;;
        --polling-url) POLLING_URL="$2"; shift 2 ;;
        --port) PORT="$2"; shift 2 ;;
        --polling-port) POLLING_PORT="$2"; shift 2 ;;
        --name) NAME="$2"; shift 2 ;;
        --volume) VOLUME="$2"; shift 2 ;;
        --image) IMAGE="$2"; shift 2 ;;
        --network) NETWORK="$2"; shift 2 ;;
        *) echo "Unknown option: $1 (see --help)"; exit 1 ;;
    esac
done

fail() { echo "Error: $*" >&2; exit 1; }

require_docker() {
    command -v docker >/dev/null 2>&1 || fail "Docker is not installed. Install it first: https://docs.docker.com/engine/install/"
    docker info >/dev/null 2>&1 || fail "Cannot talk to the Docker daemon. Is it running, and may this user use it (try sudo)?"
}

container_exists() {
    docker container inspect "$NAME" >/dev/null 2>&1
}

generate_password() {
    LC_ALL=C tr -dc 'A-Za-z0-9' </dev/urandom | head -c 20 || true
}

# With an external database the wizard is skipped, so an administrator password must exist.
default_admin_password() {
    if [[ -n "$DB" && -z "$ADMIN_PASSWORD" ]]; then
        ADMIN_PASSWORD="$(generate_password)"
        GENERATED_PASSWORD=1
    fi
}

run_container() {
    local args=(run -d --name "$NAME" --restart unless-stopped --stop-timeout 60 -p "$PORT:8080" -p "$POLLING_PORT:10943" -v "$VOLUME:/data")

    [[ -n "$NETWORK" ]] && args+=(--network "$NETWORK")
    [[ -n "$DB" ]] && args+=(-e "SQUID_DB=$DB")
    [[ -n "$REDIS" ]] && args+=(-e "SQUID_REDIS=$REDIS")
    [[ -n "$ADMIN_USER" ]] && args+=(-e "SQUID_ADMIN_USER=$ADMIN_USER")
    [[ -n "$ADMIN_PASSWORD" ]] && args+=(-e "SQUID_ADMIN_PASSWORD=$ADMIN_PASSWORD")
    [[ -n "$URL" ]] && args+=(-e "SQUID_URL=$URL")
    [[ -n "$POLLING_URL" ]] && args+=(-e "SQUID_POLLING_URL=$POLLING_URL")

    echo "Pulling $IMAGE ..."
    docker pull "$IMAGE" >/dev/null 2>&1 || echo "  (pull failed - using a local image named $IMAGE if there is one)"
    docker "${args[@]}" "$IMAGE" >/dev/null
}

# Asks the server itself (from inside the container, so no curl is needed on the host).
setup_status() {
    docker exec "$NAME" bash -c 'exec 3<>/dev/tcp/127.0.0.1/8080 && printf "GET /api/setup/status HTTP/1.0\r\n\r\n" >&3 && cat <&3' 2>/dev/null | tail -n 1
}

wait_until_ready() {
    local waited=0

    echo -n "Starting Squid "

    until STATUS="$(setup_status)" && [[ "$STATUS" == *'"required"'* ]]; do
        if ! docker container inspect -f '{{.State.Running}}' "$NAME" 2>/dev/null | grep -q true; then
            echo
            docker logs --tail 40 "$NAME" >&2 || true
            fail "the container stopped. The log above shows why."
        fi

        if (( waited >= READY_TIMEOUT_SECONDS )); then
            echo
            fail "Squid did not answer within ${READY_TIMEOUT_SECONDS}s. Check: docker logs $NAME"
        fi

        echo -n "."
        sleep 3
        waited=$((waited + 3))
    done

    echo " ready."
}

host_address() {
    if [[ -n "$URL" ]]; then echo "${URL%/}"; return; fi

    local ip=""
    command -v hostname >/dev/null 2>&1 && ip="$(hostname -I 2>/dev/null | awk '{print $1}')" || true

    echo "http://${ip:-localhost}:$PORT"
}

print_result() {
    local base
    base="$(host_address)"

    echo
    if [[ "$STATUS" == *'"required":true'* ]]; then
        local token
        token="$(docker exec "$NAME" cat /data/setup-token 2>/dev/null || true)"
        echo "  Squid is running. Finish the setup in your browser:"
        echo
        echo "    $base/setup?token=$token"
        echo
    else
        echo "  Squid is ready:  $base"
        echo
        echo "  Administrator:   ${ADMIN_USER:-admin}"
        if [[ -n "$ADMIN_PASSWORD" ]]; then
            echo "  Password:        $ADMIN_PASSWORD${GENERATED_PASSWORD:+   (generated - change it after signing in)}"
        else
            echo "  Password:        docker exec $NAME cat /data/initial-admin-password"
        fi
        echo
    fi
    echo "  Logs:      docker logs -f $NAME"
    echo "  Data:      Docker volume '$VOLUME'"
    echo "  Upgrade:   docker pull $IMAGE && docker rm -f $NAME, then run this script again"
    echo "             (the volume keeps all data and configuration; back it up first)"
    echo
}

main() {
    require_docker

    if container_exists; then
        echo "A container named '$NAME' already exists; showing its status (remove it with: docker rm -f $NAME)."
        docker start "$NAME" >/dev/null 2>&1 || true
    else
        default_admin_password
        run_container
    fi

    wait_until_ready
    print_result
}

main
