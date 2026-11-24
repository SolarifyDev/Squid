#!/bin/sh
# Entry point of the all-in-one Squid image.
#
# Translates the short, documented start parameters into the configuration keys Squid reads, then
# hands over to the server. A long key that is already set always wins, so anything an advanced
# operator configures explicitly is never overwritten. These names are the image's public
# interface: renaming one breaks every `docker run` that uses it.
#
#   SQUID_DB              -> SquidStore__ConnectionString   (PostgreSQL; omit = built-in)
#   SQUID_REDIS           -> RedisCacheConnectionString     (omit = built-in)
#   SQUID_ADMIN_USER      -> Bootstrap__Admin__UserName     (default admin)
#   SQUID_ADMIN_PASSWORD  -> Bootstrap__Admin__Password     (given without SQUID_DB = skip the wizard, built-in DB)
#   SQUID_URL             -> ServerUrl__ExternalUrl
#   SQUID_POLLING_URL     -> ServerUrl__CommsUrl
set -e

map() {
  short_value=$(printenv "$1" || true)
  long_value=$(printenv "$2" || true)

  if [ -n "$short_value" ] && [ -z "$long_value" ]; then
    export "$2=$short_value"
  fi
}

map SQUID_DB SquidStore__ConnectionString
map SQUID_REDIS RedisCacheConnectionString
map SQUID_ADMIN_USER Bootstrap__Admin__UserName
map SQUID_ADMIN_PASSWORD Bootstrap__Admin__Password
map SQUID_URL ServerUrl__ExternalUrl
map SQUID_POLLING_URL ServerUrl__CommsUrl

exec "$@"
