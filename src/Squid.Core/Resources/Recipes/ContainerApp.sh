
# ---- Safe replacement: shared by every container recipe ------------------------------------------------------------
# The recipe part above sets the settings and defines prepare_image (sets IMAGE, and IMAGE_REPO/IMAGE_TAG when it builds one)
# and start_container NAME PUBLISH.
#
# The new version is first started next to the old one on a temporary port and health-checked there; the old one is not
# touched until that passes. Only then is the old one stopped (its port is needed) and renamed to <name>-previous, which is
# kept until the new one has been confirmed healthy on the real port. While the swap is in progress a trap puts the previous
# one back if the script is stopped or fails, and the next run puts it back (or finishes the swap) if the script was killed
# outright, so a version that cannot run never replaces one that can.

# One container per project and environment on a host: the process is shared by every environment, so the ids are read at deploy time.
require_number "The project id" "$PROJECT_ID"
require_number "The environment id" "$ENVIRONMENT_ID"
require_port "The host port" "$HOST_PORT"

CONTAINER_NAME="${CONTAINER_NAME_BASE}-p${PROJECT_ID}-e${ENVIRONMENT_ID}"
CANDIDATE_NAME="${CONTAINER_NAME}-candidate"
PREVIOUS_NAME="${CONTAINER_NAME}-previous"
PREVIOUS_IMAGE_TAG=""
SWAP_ACTIVE=0

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

# wait_healthy NAME PORT [SECONDS]
wait_healthy() {
  local name="$1" port="$2" deadline=$((SECONDS + ${3:-$HEALTH_TIMEOUT_SECONDS}))

  while [ "$SECONDS" -lt "$deadline" ]; do
    if http_ok "http://127.0.0.1:${port}${HEALTH_PATH}"; then return 0; fi
    if ! container_running "$name"; then return 1; fi
    sleep 1
  done

  return 1
}

# The host port a container is published on, whatever it was started with.
published_port() { docker port "$1" "${CONTAINER_PORT}/tcp" 2>/dev/null | head -n 1 | sed 's/.*://' || true; }

live_is_healthy() {
  local port
  port="$(published_port "$CONTAINER_NAME")"

  [ -n "$port" ] && container_running "$CONTAINER_NAME" && wait_healthy "$CONTAINER_NAME" "$port" 20
}

# Puts the previous container back under the live name and starts it. Safe to call when nothing is in progress.
restore_previous() {
  if container_exists "$PREVIOUS_NAME"; then
    remove_container "$CONTAINER_NAME"
    docker rename "$PREVIOUS_NAME" "$CONTAINER_NAME" || true
  fi

  if container_exists "$CONTAINER_NAME" && ! container_running "$CONTAINER_NAME"; then
    docker start "$CONTAINER_NAME" >/dev/null || log "ERROR: the previous version could not be started again. Its container is '$CONTAINER_NAME'; start it by hand."
  fi
}

rollback_swap() {
  if [ "$SWAP_ACTIVE" != "1" ]; then return 0; fi

  SWAP_ACTIVE=0
  log "The swap did not finish; restoring the previous version."
  restore_previous
}

on_exit() {
  local code=$?

  trap - EXIT
  rollback_swap
  exit "$code"
}

trap on_exit EXIT
trap 'exit 143' TERM
trap 'exit 130' INT

# A run that was killed outright (nothing can trap SIGKILL) leaves one of three states; this settles it before anything else:
#  - the previous container exists and the live one is healthy: the swap had succeeded, the previous one is no longer needed;
#  - the previous container exists and the live one is missing or unhealthy: the swap had not succeeded, put the previous one back;
#  - the live container exists but is stopped (killed between stopping it and renaming it): start it.
recover_interrupted_swap() {
  if container_exists "$PREVIOUS_NAME"; then
    log "A previous deployment was interrupted while replacing the running version; settling it first."

    if container_exists "$CONTAINER_NAME" && live_is_healthy; then
      remove_container "$PREVIOUS_NAME"
    else
      restore_previous
    fi
  fi

  if container_exists "$CONTAINER_NAME" && ! container_running "$CONTAINER_NAME"; then
    log "The running version was found stopped; starting it again."
    docker start "$CONTAINER_NAME" >/dev/null || true
  fi
}

verify_candidate() {
  local port=""

  remove_container "$CANDIDATE_NAME"
  log "Starting the new version on a temporary port to check it before it replaces anything."

  if start_container "$CANDIDATE_NAME" "127.0.0.1::${CONTAINER_PORT}" >/dev/null; then
    port="$(published_port "$CANDIDATE_NAME")"
  fi

  if [ -n "$port" ] && wait_healthy "$CANDIDATE_NAME" "$port"; then
    remove_container "$CANDIDATE_NAME"
    return 0
  fi

  show_logs "$CANDIDATE_NAME"
  remove_container "$CANDIDATE_NAME"
  fail "The new version did not start, or did not answer 'GET ${HEALTH_PATH}' with a success status within ${HEALTH_TIMEOUT_SECONDS} seconds. The version that was running (if any) was left untouched."
}

record_previous_image_tag() {
  local image
  image="$(docker inspect --format '{{.Config.Image}}' "$CONTAINER_NAME" 2>/dev/null || true)"

  case "$image" in
    "${IMAGE_REPO}:"*) if [ -n "$IMAGE_REPO" ]; then PREVIOUS_IMAGE_TAG="${image#"${IMAGE_REPO}":}"; fi ;;
  esac
}

# After a successful swap: the images of this app except the running one and the one it replaced (kept for a quick rollback).
cleanup_images() {
  if [ -z "$IMAGE_REPO" ]; then return 0; fi

  local tag
  for tag in $(docker images "$IMAGE_REPO" --format '{{.Tag}}' 2>/dev/null || true); do
    if [ "$tag" = "$IMAGE_TAG" ] || [ "$tag" = "$PREVIOUS_IMAGE_TAG" ]; then continue; fi

    docker rmi "${IMAGE_REPO}:${tag}" >/dev/null 2>&1 || true
  done
}

swap_in() {
  if container_exists "$CONTAINER_NAME"; then
    record_previous_image_tag
    SWAP_ACTIVE=1
    docker stop "$CONTAINER_NAME" >/dev/null || fail "The running version could not be stopped; nothing was changed."
    docker rename "$CONTAINER_NAME" "$PREVIOUS_NAME"
  fi

  if start_container "$CONTAINER_NAME" "${HOST_PORT}:${CONTAINER_PORT}" >/dev/null && wait_healthy "$CONTAINER_NAME" "$HOST_PORT"; then
    SWAP_ACTIVE=0
    remove_container "$PREVIOUS_NAME"
    cleanup_images
    log "Deployed ${APP_NAME}: container '$CONTAINER_NAME' answers on port ${HOST_PORT}."
    return 0
  fi

  show_logs "$CONTAINER_NAME"
  remove_container "$CONTAINER_NAME"
  fail "The new version could not take over port ${HOST_PORT}. Any previous version is being restored."
}

require_docker
require_http_client
recover_interrupted_swap
prepare_image
verify_candidate
swap_in
