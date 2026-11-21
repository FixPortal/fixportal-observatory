#!/bin/sh
# Runs before the nginx image's envsubst step. OBSERVATORY_API_KEY is written into a quoted nginx
# string (nginx.conf.template), so a character nginx treats specially would leave default.conf
# unparseable and the container would exit with an error that does not name the setting.
set -eu
key="${OBSERVATORY_API_KEY-}"
case "$key" in
    *[!A-Za-z0-9._~+/=:@!#%^\&*\(\)-]*)
        echo "OBSERVATORY_API_KEY contains a character the proxy cannot embed. Use only letters, digits and . _ ~ + / = : @ ! # % ^ & * ( ) -" >&2
        exit 1
        ;;
esac
# The allow-list becomes server_name, so an empty value or a stray character would break the file.
hosts="${OBSERVATORY_ALLOWED_HOSTS-}"
case "$hosts" in
    ""|*[!A-Za-z0-9._*\ -]*)
        echo "OBSERVATORY_ALLOWED_HOSTS must be a non-empty, space-separated list of host names (letters, digits and . _ * -)." >&2
        exit 1
        ;;
esac
