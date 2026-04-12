# ==========================================================================
# Squid Runtime Helper Functions (bash)
# Injected by the SSH transport before every user script. DO NOT EDIT —
# changes made inside the remote work directory are lost on next run.
# ==========================================================================

__squid_b64() {
    printf '%s' "$1" | base64 | tr -d '\n\r '
}

# set_squidvariable NAME VALUE [True|False]
# Emits a ##squid[setVariable ...] service message that the server parses
# into an output variable for subsequent deployment steps.
set_squidvariable() {
    local __name="$1"
    local __value="${2:-}"
    local __sensitive="${3:-False}"
    printf '##squid[setVariable name="%s" value="%s" sensitive=%s]\n' \
        "$(__squid_b64 "$__name")" \
        "$(__squid_b64 "$__value")" \
        "'$__sensitive'"
}

# get_squidvariable NAME
# Reads a previously-exported deployment variable by its sanitized env name.
get_squidvariable() {
    local __name="$1"
    local __env
    __env="$(printf '%s' "$__name" | sed 's/[^A-Za-z0-9_]/_/g')"
    case "$__env" in
        [0-9]*) __env="_$__env" ;;
    esac
    eval "printf '%s' \"\${$__env:-}\""
}

# new_squidartifact PATH [NAME]
# Emits a ##squid[createArtifact ...] service message registering a file
# produced by the script as a deployment artifact.
new_squidartifact() {
    local __path="$1"
    local __name="${2:-$(basename "$__path")}"
    printf '##squid[createArtifact path="%s" name="%s"]\n' \
        "$(__squid_b64 "$__path")" \
        "$(__squid_b64 "$__name")"
}

# fail_step [MESSAGE]
# Emits a ##squid[stepFailed ...] service message and terminates the
# current script with exit code 1 so the deployment marks the step failed.
fail_step() {
    local __message="${1:-Script requested step failure}"
    printf '##squid[stepFailed message="%s"]\n' "$(__squid_b64 "$__message")"
    exit 1
}

# write_squidwarning MESSAGE
# Emits a ##squid[stdWarning ...] service message for non-fatal warnings.
write_squidwarning() {
    local __message="${1:-}"
    printf '##squid[stdWarning message="%s"]\n' "$(__squid_b64 "$__message")"
}
