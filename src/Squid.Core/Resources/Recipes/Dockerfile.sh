
# ---- Dockerfile app: built from the package's Dockerfile --------------------------------------------------------------
APP_NAME={{AppName}}
CONTAINER_NAME_BASE={{ContainerNameBase}}
CONTAINER_PORT={{ContainerPort}}
HEALTH_PATH={{HealthPath}}
HEALTH_TIMEOUT_SECONDS={{HealthTimeoutSeconds}}
BUILD_SUB_PATH={{BuildSubPath}}

read_squid_var INSTALL_DIR "the folder the package was extracted into" {{InstallDirB64}}
read_squid_var PROJECT_ID "the project id" {{ProjectIdB64}}
read_squid_var ENVIRONMENT_ID "the environment id" {{EnvironmentIdB64}}
read_squid_var HOST_PORT "the host port" {{HostPortB64}}
read_squid_var RELEASE_VERSION "the release number" {{ReleaseVersionB64}}

# A Docker tag is at most 128 characters of [A-Za-z0-9_.-] and cannot start with '.' or '-'.
docker_tag() {
  local tag
  tag="$(printf '%s' "$1" | tr -c 'A-Za-z0-9_.-' '-' | cut -c1-128)"

  case "$tag" in
    [A-Za-z0-9_]*) ;;
    *) tag="$(printf 'v%s' "$tag" | cut -c1-128)" ;;
  esac

  printf '%s' "$tag"
}

prepare_image() {
  BUILD_DIR="${INSTALL_DIR%/}"

  if [ -n "$BUILD_SUB_PATH" ]; then BUILD_DIR="$BUILD_DIR/$BUILD_SUB_PATH"; fi

  [ -f "$BUILD_DIR/Dockerfile" ] || fail "No Dockerfile was found in the build folder; the package was not extracted where the recipe expected."

  IMAGE_REPO="$CONTAINER_NAME"
  IMAGE_TAG="$(docker_tag "$RELEASE_VERSION")"
  IMAGE="${IMAGE_REPO}:${IMAGE_TAG}"

  log "Building image $IMAGE"
  docker build -t "$IMAGE" "$BUILD_DIR" || fail "docker build failed for $IMAGE."
}

start_container() {
  docker run -d --name "$1" --restart unless-stopped --label "squid.app=$CONTAINER_NAME" -p "$2" "$IMAGE"
}
