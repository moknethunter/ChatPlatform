#!/usr/bin/env bash
# The runtime image includes bash; no curl or package download is needed.
set -eu
exec 3<>/dev/tcp/127.0.0.1/8080
printf 'GET /ready HTTP/1.1\r\nHost: localhost\r\nConnection: close\r\n\r\n' >&3
IFS= read -r status <&3
[[ "$status" == HTTP/*" 200 "* ]]
