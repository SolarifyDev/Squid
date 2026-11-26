
# ---- Static site: served by a Caddy container -----------------------------------------------------------------------
APP_NAME={{AppName}}
CONTAINER_NAME_BASE={{ContainerNameBase}}
CONTAINER_PORT={{ContainerPort}}
HEALTH_PATH={{HealthPath}}
HEALTH_TIMEOUT_SECONDS={{HealthTimeoutSeconds}}
SITE_SUB_PATH={{SiteSubPath}}
SERVER_IMAGE={{ServerImage}}
IMAGE_REPO=""

read_squid_var INSTALL_DIR "the folder the package was extracted into" {{InstallDirB64}}
read_squid_var PROJECT_ID "the project id" {{ProjectIdB64}}
read_squid_var ENVIRONMENT_ID "the environment id" {{EnvironmentIdB64}}
read_squid_var HOST_PORT "the host port" {{HostPortB64}}

prepare_image() {
  SITE_DIR="${INSTALL_DIR%/}"

  if [ -n "$SITE_SUB_PATH" ]; then SITE_DIR="$SITE_DIR/$SITE_SUB_PATH"; fi

  [ -f "$SITE_DIR/index.html" ] || fail "index.html was not found in the site folder; the package was not extracted where the recipe expected."

  IMAGE="$SERVER_IMAGE"
}

# The site is copied into the container (docker cp takes the path as one argument), not bind-mounted: a mount option string cannot hold every character a path can.
start_container() {
  docker create --name "$1" --restart unless-stopped --label "squid.app=$CONTAINER_NAME" -p "$2" \
    "$IMAGE" caddy file-server --root /srv --listen ":$CONTAINER_PORT" >/dev/null \
    && docker cp "$SITE_DIR/." "$1:/srv" \
    && docker start "$1"
}
