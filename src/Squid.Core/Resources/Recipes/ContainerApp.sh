
# ---- Safe replacement: shared by every container recipe ------------------------------------------------------------
# The recipe part above sets the settings and defines prepare_image (sets IMAGE) and start_container NAME PUBLISH.
#
# The new version is first started next to the old one on a temporary port and health-checked there; the old one is
# not touched until that passes. Only then is the old one stopped (its port is needed) and renamed, never removed, and
# the new one is started on the real port and checked again. If that fails, the new one is removed and the old one is
# started again, so a version that cannot run never replaces one that can.

CANDIDATE_NAME="${CONTAINER_NAME}-candidate"
PREVIOUS_NAME="${CONTAINER_NAME}-previous"

log() { printf '%s\n' "$*"; }

fail() { printf 'ERROR: %s\n' "$*" >&2; exit 1; }

require_docker() {
  command -v docker >/dev/null 2>&1 || fail "Docker is required on this target but 'docker' was not found on the PATH. Install Docker on the target, then deploy again."
  docker info >/dev/null 2>&1 || fail "Docker is installed on this target but the Docker daemon cannot be reached by this user. Start it, or give this user access to it, then deploy again."
}

require_http_client() {
  command -v curl >/dev/null 2>&1 || command -v wget >/dev/null 2>&1 || fail "curl or wget is required on this target to check that the app answers, and neither was found."
}

http_ok() {
  if command -v curl >/dev/null 2>&1; then
    curl -fsS -o /dev/null --max-time 3 "$1" >/dev/null 2>&1
  else
    wget -q -O /dev/null -T 3 "$1" >/dev/null 2>&1
  fi
}

container_exists() { docker container inspect "$1" >/dev/null 2>&1; }

container_running() { [ "$(docker ps -q --filter "name=^${1}\$" | wc -l | tr -d ' ')" != "0" ]; }

remove_container() { docker rm -f "$1" >/dev/null 2>&1 || true; }

show_logs() {
  log "Last log lines of $1:"
  docker logs --tail 40 "$1" 2>&1 || true
}

wait_healthy() {
  local name="$1" port="$2" deadline=$((SECONDS + HEALTH_TIMEOUT_SECONDS))

  while [ "$SECONDS" -lt "$deadline" ]; do
    if http_ok "http://127.0.0.1:${port}${HEALTH_PATH}"; then return 0; fi
    if ! container_running "$name"; then return 1; fi
    sleep 1
  done

  return 1
}

recover_interrupted_swap() {
  if container_exists "$PREVIOUS_NAME" && ! container_exists "$CONTAINER_NAME"; then
    log "A previous deployment was interrupted after stopping the running version; starting it again."
    docker rename "$PREVIOUS_NAME" "$CONTAINER_NAME"
    docker start "$CONTAINER_NAME" >/dev/null
  fi
}

verify_candidate() {
  local port=""

  remove_container "$CANDIDATE_NAME"
  log "Starting the new version on a temporary port to check it before it replaces anything."

  if start_container "$CANDIDATE_NAME" "127.0.0.1::${CONTAINER_PORT}" >/dev/null; then
    port="$(docker port "$CANDIDATE_NAME" "${CONTAINER_PORT}/tcp" 2>/dev/null | head -n 1 | sed 's/.*://' || true)"
  fi

  if [ -n "$port" ] && wait_healthy "$CANDIDATE_NAME" "$port"; then
    remove_container "$CANDIDATE_NAME"
    return 0
  fi

  show_logs "$CANDIDATE_NAME"
  remove_container "$CANDIDATE_NAME"
  fail "The new version did not start, or did not answer 'GET ${HEALTH_PATH}' with a success status within ${HEALTH_TIMEOUT_SECONDS} seconds. The version that was running (if any) was left untouched."
}

restore_previous() {
  if ! container_exists "$PREVIOUS_NAME"; then return 0; fi

  docker rename "$PREVIOUS_NAME" "$CONTAINER_NAME"
  docker start "$CONTAINER_NAME" >/dev/null || fail "The previous version could not be started again. Its container is '$CONTAINER_NAME'; start it by hand."
  log "The previous version is running again."
}

swap_in() {
  remove_container "$PREVIOUS_NAME"

  if container_exists "$CONTAINER_NAME"; then
    docker stop "$CONTAINER_NAME" >/dev/null || fail "The running version could not be stopped; nothing was changed."
    docker rename "$CONTAINER_NAME" "$PREVIOUS_NAME"
  fi

  if start_container "$CONTAINER_NAME" "${HOST_PORT}:${CONTAINER_PORT}" >/dev/null && wait_healthy "$CONTAINER_NAME" "$HOST_PORT"; then
    remove_container "$PREVIOUS_NAME"
    log "Deployed ${APP_NAME}: container '$CONTAINER_NAME' answers on port ${HOST_PORT}."
    return 0
  fi

  show_logs "$CONTAINER_NAME"
  remove_container "$CONTAINER_NAME"
  restore_previous
  fail "The new version could not take over port ${HOST_PORT}. Any previous version was restored."
}

require_docker
require_http_client
prepare_image
recover_interrupted_swap
verify_candidate
swap_in
