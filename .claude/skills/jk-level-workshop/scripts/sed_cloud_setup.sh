#!/usr/bin/env bash
# Build, test, package and verify SED MotS's native Linux distribution in
# Claude's cloud workspace, then extract it ready to use.
#
#   sed_cloud_setup.sh WORKDIR [SOURCE] [REF]
#
# SOURCE is a Git URL or bundle; it defaults to the public repository,
# https://gitlab.com/codalilitari/SED-MotS.git. REF defaults to `claude`: Coda's
# native Linux branch plus Red's Linux engine-capture profile. WORKDIR must not
# exist. The script:
#   1. clones REF;
#   2. installs the .NET SDK pinned by global.json into ~/.dotnet when it is missing;
#   3. installs the desktop libraries, Mesa's software Vulkan driver (lavapipe),
#      Xvfb and xauth when they are missing, and selects lavapipe for this run;
#   4. runs the repository's own tools/package-linux.sh with --graphics: a clean
#      source snapshot, the Core/MCP/Python suites and UI probe, the Vulkan smoke
#      test, SceneProbe and AppShot, a self-contained publish, and relocated
#      CLI/MCP verification including a real snapshot;
#   5. extracts the package to WORKDIR/sed.
# It prints the receipts' results and the launcher paths. Nothing is installed on
# Andrei's computer, and no game engine or game data is involved.
set -euo pipefail

if [[ $# -lt 1 || $# -gt 3 ]]; then
  echo "usage: sed_cloud_setup.sh WORKDIR [SOURCE] [REF]" >&2
  exit 2
fi
work=$1
source=${2:-https://gitlab.com/codalilitari/SED-MotS.git}
ref=${3:-claude}
if [[ -e $work ]]; then echo "workdir exists: $work" >&2; exit 2; fi
mkdir -p "$work"
work=$(cd "$work" && pwd -P)
git clone -q -b "$ref" "$source" "$work/src"
sdk=$(python3 -c 'import json,sys; print(json.load(open(sys.argv[1]))["sdk"]["version"])' "$work/src/global.json")

export DOTNET_ROOT=${DOTNET_ROOT:-$HOME/.dotnet} DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
export PATH="$DOTNET_ROOT:$PATH"
if ! "$DOTNET_ROOT/dotnet" --list-sdks 2>/dev/null | grep -q "^$sdk "; then
  curl -fsSL https://dot.net/v1/dotnet-install.sh -o "$work/dotnet-install.sh"
  bash "$work/dotnet-install.sh" --version "$sdk" --install-dir "$DOTNET_ROOT" >/dev/null
fi

# The libraries START-HERE.md lists for Ubuntu 24.04, plus Xvfb for the X11 check.
wanted=(libicu74 libssl3t64 zlib1g libx11-6 libxcb1 libxext6 libxrender1 libxrandr2 libxi6 libxcursor1
        libice6 libsm6 libxkbcommon0 libxkbcommon-x11-0 libfontconfig1 libfreetype6 libvulkan1
        mesa-vulkan-drivers xvfb xauth)
missing=()
for package in "${wanted[@]}"; do
  dpkg-query -W -f='${Status}' "$package" 2>/dev/null | grep -q "install ok installed" || missing+=("$package")
done
if [[ ${#missing[@]} -gt 0 ]]; then
  (apt-get update -qq && DEBIAN_FRONTEND=noninteractive apt-get install -y -qq "${missing[@]}") >/dev/null 2>&1 \
    || echo "warning: could not install ${missing[*]}; graphics checks may fail" >&2
fi
icd=$(ls /usr/share/vulkan/icd.d/*lvp*.json 2>/dev/null | head -1 || true)
if [[ -z $icd ]]; then echo "no lavapipe ICD found under /usr/share/vulkan/icd.d" >&2; exit 1; fi
export VK_ICD_FILENAMES=$icd VK_DRIVER_FILES=$icd

cd "$work/src"
version=$(git describe --tags --always | sed 's/^v//')
[[ $version =~ ^[0-9]+\.[0-9]+\.[0-9]+ ]] || version="0.0.0-$version"
status=0
tools/package-linux.sh --dotnet "$DOTNET_ROOT/dotnet" --version "$version" --graphics \
  --output "$work/package" > "$work/package.log" 2>&1 || status=$?
python3 - "$work/package" <<'PY'
import json, sys
from pathlib import Path
out = Path(sys.argv[1])
for name in ("build-receipt.json", "package-receipt.json"):
    path = out / name
    if path.is_file():
        receipt = json.loads(path.read_text(encoding="utf-8"))
        print(f"{name}: {receipt.get('status')}  commit {str(receipt.get('commit', ''))[:12]}  {receipt.get('error', '')}")
    else:
        print(f"{name}: missing")
PY
if [[ $status -ne 0 ]]; then
  echo "packaging failed (exit $status); see $work/package.log and the receipts" >&2
  exit "$status"
fi
(cd "$work/package" && sha256sum -c --quiet SHA256SUMS.txt)
archive=$(ls "$work"/package/SED-MotS-*-linux-x64.tar.gz)
mkdir -p "$work/sed"
tar -xzf "$archive" -C "$work/sed"
root=$(ls -d "$work"/sed/SED-MotS-*-linux-x64)
echo "package: $root"
echo "launchers: $root/bin/sed-agent  $root/bin/sed-mcp  $root/bin/sed-mots"
echo "lavapipe: VK_ICD_FILENAMES=$icd"
