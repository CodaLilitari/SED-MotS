#!/bin/sh
set -eu
tools=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd -P)
exec python3 -B "$tools/macos.py" package "$@"
