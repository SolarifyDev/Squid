set -euo pipefail

# ---- Prelude: shared by every recipe script ----------------------------------------------------------------------------
# A deployment replaces its variable expressions in the script as plain text, so a value that comes from the deployment (a project or environment
# name inside the extraction path, a release number) must never be written between quotes: a quote in it would end the literal.
# Every such value reaches the script as Base64 (the | ToBase64 filter), a text with no character that means anything to bash,
# and is decoded here into a variable that is only ever used quoted.

log() { printf '%s\n' "$*"; }

fail() { printf 'ERROR: %s\n' "$*" >&2; exit 1; }

# read_squid_var TARGET LABEL BASE64: decodes into the variable TARGET exactly, trailing newlines included.
read_squid_var() {
  case "$3" in
    ''|*[!A-Za-z0-9+/=]*) fail "The deployment did not give this script $2." ;;
  esac

  local decoded
  decoded="$({ printf '%s' "$3" | { base64 -d 2>/dev/null || base64 -D 2>/dev/null; }; printf x; } || true)"
  printf -v "$1" '%s' "${decoded%x}"
}

require_port() {
  case "$2" in
    ''|*[!0-9]*) fail "$1 '$2' is not a TCP port." ;;
  esac

  if [ "$2" -lt 1 ] || [ "$2" -gt 65535 ]; then fail "$1 '$2' is not a TCP port between 1 and 65535."; fi
}

require_number() {
  case "$2" in
    ''|*[!0-9]*) fail "$1 '$2' is not a number." ;;
  esac
}
