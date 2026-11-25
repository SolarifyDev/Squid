set -euo pipefail

# ---- Static site: served by a Caddy container -----------------------------------------------------------------------
APP_NAME={{AppName}}
CONTAINER_NAME_BASE={{ContainerNameBase}}
PROJECT_ID={{ProjectId}}
ENVIRONMENT_ID={{EnvironmentId}}
HOST_PORT={{HostPort}}
CONTAINER_PORT={{ContainerPort}}
HEALTH_PATH={{HealthPath}}
HEALTH_TIMEOUT_SECONDS={{HealthTimeoutSeconds}}
INSTALL_DIR={{InstallDir}}
SITE_SUB_PATH={{SiteSubPath}}
SERVER_IMAGE={{ServerImage}}

prepare_image() {
  SITE_DIR="${INSTALL_DIR%/}"

  if [ -n "$SITE_SUB_PATH" ]; then SITE_DIR="$SITE_DIR/$SITE_SUB_PATH"; fi

  [ -f "$SITE_DIR/index.html" ] || fail "index.html was not found in '$SITE_DIR'; the package was not extracted where the recipe expected."

  case "$SITE_DIR" in
    *,*|*\"*) fail "The site folder '$SITE_DIR' contains a comma or a double quote, which a Docker bind mount cannot take." ;;
  esac

  IMAGE="$SERVER_IMAGE"
}

start_container() {
  docker run -d --name "$1" --restart unless-stopped --label "squid.app=$CONTAINER_NAME" -p "$2" \
    --mount "type=bind,source=$SITE_DIR,target=/srv,readonly" \
    "$IMAGE" caddy file-server --root /srv --listen ":$CONTAINER_PORT"
}
