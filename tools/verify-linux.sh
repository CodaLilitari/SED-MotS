#!/bin/sh
set -eu
script=$(readlink -f -- "$0")
tools=$(CDPATH= cd -- "$(dirname -- "$script")" && pwd -P)
exec python3 -B "$tools/linux.py" verify "$@"
