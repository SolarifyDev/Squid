set -euo pipefail

# ---- Dockerfile app: built from the package's Dockerfile --------------------------------------------------------------
APP_NAME={{AppName}}
CONTAINER_NAME_BASE={{ContainerNameBase}}
PROJECT_ID={{ProjectId}}
ENVIRONMENT_ID={{EnvironmentId}}
HOST_PORT={{HostPort}}
CONTAINER_PORT={{ContainerPort}}
HEALTH_PATH={{HealthPath}}
HEALTH_TIMEOUT_SECONDS={{HealthTimeoutSeconds}}
INSTALL_DIR={{InstallDir}}
BUILD_SUB_PATH={{BuildSubPath}}
RELEASE_VERSION={{ReleaseVersion}}

prepare_image() {
  BUILD_DIR="${INSTALL_DIR%/}"

  if [ -n "$BUILD_SUB_PATH" ]; then BUILD_DIR="$BUILD_DIR/$BUILD_SUB_PATH"; fi

  [ -f "$BUILD_DIR/Dockerfile" ] || fail "No Dockerfile was found in '$BUILD_DIR'; the package was not extracted where the recipe expected."

  local tag
  tag="$(printf '%s' "$RELEASE_VERSION" | tr -c 'A-Za-z0-9_.-' '-')"
  IMAGE="${CONTAINER_NAME}:${tag:-latest}"

  log "Building image $IMAGE"
  docker build -t "$IMAGE" "$BUILD_DIR" || fail "docker build failed for $IMAGE."
}

start_container() {
  docker run -d --name "$1" --restart unless-stopped --label "squid.app=$CONTAINER_NAME" -p "$2" "$IMAGE"
}
