#!/usr/bin/env bash
# Build the OpenMotS engine (OpenJKDF2-MotS-Fixes) for Linux in Claude's cloud
# workspace: for SED's capture runner, or with Coda's opt-in agent hooks for
# live play-and-test sessions.
#
#   openmots_cloud_setup.sh WORKDIR [--agent] [SOURCE [REF]]
#
# SOURCE defaults to the public repository.
#   Without --agent, REF defaults to 2c6008db: its production source is identical
#   to the capture build that passed Coda's Windows ENGINE-01 matrix (74e4657c,
#   same as integrated 197c2625); it adds only docs. The repository's own
#   build_linux64.sh builds it.
#   With --agent, REF defaults to f554833f, the tip of Coda's
#   agent/linux-workspace-20261030 branch (main ea715f21 plus the agent
#   workspace and mod scenarios). It is configured with
#   -DOPENJKDF2_AGENT_HOOKS=ON in build_agent_linux, and agent_workspace.py's
#   doctor checks the result. Capture-v1 is unchanged in that build.
# WORKDIR must not exist. The script installs the build and headless-display
# packages with apt, clones SOURCE, checks out REF, fetches the submodules,
# builds (8 to 15 minutes on 2 CPUs), checks that the binary refuses -capture
# without a request, and writes WORKDIR/build-receipt.json.
# No game files are needed, read or downloaded. Nothing is installed on
# Andrei's computer. Run it in the background and poll WORKDIR/build.log.
set -euo pipefail

usage() { echo "usage: openmots_cloud_setup.sh WORKDIR [--agent] [SOURCE [REF]]" >&2; exit 2; }
[[ $# -ge 1 ]] || usage
work=$1; shift
agent=false
if [[ ${1:-} == "--agent" ]]; then agent=true; shift; fi
[[ $# -le 2 ]] || usage
source=${1:-https://gitlab.com/codalilitari/OpenJKDF2-MotS-Fixes.git}
if [[ $agent == true ]]; then ref=${2:-f554833f}; else ref=${2:-2c6008db}; fi
if [[ -e $work ]]; then echo "workdir exists: $work" >&2; exit 2; fi
mkdir -p "$work"
work=$(cd "$work" && pwd)

packages=(git build-essential cmake make python3 python3-pip python3-venv bison imagemagick libgtk-3-dev
  protobuf-compiler zsh libpng-dev libcurl4-openssl-dev clang libsdl2-dev libsdl2-mixer-dev libopenal-dev
  libglew-dev libssl-dev libprotobuf-dev xvfb xauth libgl1-mesa-dri mesa-utils)
missing=()
for p in "${packages[@]}"; do dpkg-query -W -f='${Status}' "$p" 2>/dev/null | grep -q "ok installed" || missing+=("$p"); done
if (( ${#missing[@]} )); then
  echo "installing: ${missing[*]}"
  apt-get update -qq >/dev/null
  # --force-confold keeps existing configuration files instead of prompting (zsh's does).
  DEBIAN_FRONTEND=noninteractive apt-get install -y -qq -o Dpkg::Options::=--force-confold "${missing[@]}" >"$work/apt.log" 2>&1
fi

git clone -q "$source" "$work/src"
git -C "$work/src" checkout -q "$ref"
git -C "$work/src" submodule update --init --recursive --depth 1 -q
commit=$(git -C "$work/src" rev-parse HEAD)

echo "building $commit (agent hooks: $agent; log: $work/build.log)"
started=$(date -u +%Y-%m-%dT%H:%M:%SZ)
if [[ $agent == true ]]; then
  engine="$work/src/build_agent_linux/openjkdf2"
  (
    cd "$work/src"
    export OPENJKDF2_RELEASE_COMMIT=$commit OPENJKDF2_RELEASE_COMMIT_SHORT=${commit:0:8}
    cmake -S . -B build_agent_linux -DPLAT_LINUX_64=ON -DCMAKE_BUILD_TYPE=Release -DOPENJKDF2_AGENT_HOOKS=ON
    # PROTOBUF exists only when GameNetworkingSockets builds from the bundled sources.
    if cmake --build build_agent_linux --target help | grep -q "PROTOBUF"; then
      cmake --build build_agent_linux --target PROTOBUF --parallel "$(nproc)" \
        || cmake --build build_agent_linux --target PROTOBUF --parallel 1
    fi
    cmake --build build_agent_linux --target openjkdf2 --parallel "$(nproc)"
  ) >"$work/build.log" 2>&1 || { echo "build failed; see $work/build.log" >&2; tail -20 "$work/build.log" >&2; exit 1; }
else
  engine="$work/src/build_linux64/openjkdf2"
  (cd "$work/src" && ./build_linux64.sh) >"$work/build.log" 2>&1 \
    || { echo "build failed; see $work/build.log" >&2; tail -20 "$work/build.log" >&2; exit 1; }
fi
[[ -x $engine ]] || { echo "build produced no engine at $engine" >&2; exit 1; }

# Start check: the capture guard must refuse a missing request, write nothing and exit 6.
check="$work/start-check"; mkdir "$check"
set +e
(cd "$check" && env -i PATH=/usr/bin:/bin HOME="$check" timeout 20 "$engine" -capture >stdout.log 2>stderr.log)
status=$?
set -e
guard=false
if [[ $status -eq 6 ]] && grep -q "capture: invalid request or isolated session" "$check/stderr.log" \
   && [[ $(ls -A "$check" | sort | tr '\n' ' ') == "stderr.log stdout.log " ]]; then guard=true; fi

doctor=skipped
if [[ $agent == true ]]; then
  if python3 -B "$work/src/scripts/agent_workspace.py" doctor --binary "$engine" >"$work/doctor.json" 2>"$work/doctor.log"; then
    doctor=passed
  else
    doctor=failed
  fi
fi

python3 - "$work/build-receipt.json" "$source" "$ref" "$commit" "$engine" "$started" "$guard" "$agent" "$doctor" <<'PY'
import hashlib, json, platform, subprocess, sys
out, source, ref, commit, engine, started, guard, agent, doctor = sys.argv[1:]
digest = hashlib.sha256(open(engine, "rb").read()).hexdigest()
def first_line(command):
    try:
        return subprocess.run(command, capture_output=True, text=True, timeout=20).stdout.splitlines()[0]
    except Exception:
        return None
receipt = {
    "schema": "red-workshop/openmots-linux-build/v2",
    "source": source, "requestedRef": ref, "commit": commit,
    "agentHooks": agent == "true",
    "engine": engine, "engineSha256": digest,
    "built": {"started": started, "platform": platform.platform(), "compiler": first_line(["cc", "--version"])},
    "startCheck": {"refusesCaptureWithoutRequest": guard == "true"},
    "agentWorkspaceDoctor": doctor,
    "scope": "Engine binary only. No game files were read; captures and play sessions need the user's own game files.",
}
open(out, "w", encoding="utf-8").write(json.dumps(receipt, indent=2) + "\n")
print(f"engine: {engine}\nsha256: {digest}\nstart check: {'passed' if guard == 'true' else 'FAILED'}\nagent doctor: {doctor}")
PY
[[ $guard == true ]] || exit 1
[[ $doctor != failed ]] || exit 1
