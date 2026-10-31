# ENGINE-01 on Linux: the native engine profile

[ENGINE_CAPTURE.md](ENGINE_CAPTURE.md) describes the capture workflow. This page
covers what changes when the engine is the native Linux build of
OpenJKDF2-MotS-Fixes instead of the Windows executable. The fixture, request,
staging, hashing, resource-collision checks, budgets and receipts are unchanged.

## Profiles

The staged executable names select the profile, and each profile runs only on
its own operating system:

| Profile | Staged runtime files | Runs on |
|---|---|---|
| Windows | `openjkdf2-64.exe`, `OpenAL32.dll`, `libGameNetworkingSockets.dll` | Windows |
| Linux | one `openjkdf2` executable, using the system's OpenAL | Linux |

Build the Linux engine from OpenJKDF2-MotS-Fixes with its own
`build_linux64.sh` (or its documented CMake route). The executable must
implement `sed-engine-capture-v1`; a build without the capture hooks refuses the
request. Record the engine's commit and SHA-256 in a separate build receipt, as
on Windows.

## Containment and display

- **Process group.** The engine starts in its own session and process group.
  Closing the job kills the whole group. A descendant that deliberately starts a
  new session can escape the group; the receipt records that limit instead of
  claiming complete containment.
- **Private display.** The launcher starts its own Xvfb server on an unused
  display number with TCP disabled, so no window can reach a person's screen.
  The Windows visible-window observer has nothing to observe there, and the
  receipt says so.
- **Isolated environment.** `HOME` and `XDG_RUNTIME_DIR` point inside the
  isolated session. SDL is held to the private X11 display
  (`SDL_VIDEODRIVER=x11`), and audio goes to null backends
  (`SDL_AUDIODRIVER=dummy`, `ALSOFT_DRIVERS=null`). Nothing reaches a person's
  screen or speakers.

## Requirements

Python 3.10 or newer, Xvfb, and an OpenGL driver for the engine. Mesa's
software rasteriser (llvmpipe) works on machines without a GPU; images from it
can differ from a GPU's in filtering and precision, so compare captures made
with the same renderer and state which one was used.

## What was verified

- `tools/tests/test_invoke_engine_capture_linux.py` covers profile validation,
  process-group cleanup of a descendant holding the log pipe, the private
  display, and a complete `run_capture` with a synthetic stand-in engine. The
  existing capture tests still pass, with the Windows-only tests skipped.
- `new_engine_lighting_fixture.py` prepares all eight ENGINE-01 cases with the
  Linux agent, and `test_engine_lighting.py` runs them through staging and
  launch.
- The real Linux build of OpenJKDF2-MotS-Fixes (`2c6008db`) ran through the
  launcher on llvmpipe. Without game files it stopped at its own guard:
  `capture: required assets unavailable; installation UI is disabled`.

Not yet established: a complete Linux capture with owned game files, and any
comparison with the Windows `matrix-02` results beyond "same engine source,
different platform and renderer". A real capture needs the user's own
`resource/JKMRES.GOO`, `resource/JKMsndLO.goo` and `resource/jk_.cd`, the
bootstrap set the Windows ENGINE-01 run used. No game files are bundled.

## Related: live play and test sessions

OpenJKDF2-MotS-Fixes also has an opt-in agent workspace for live single-player
sessions on Linux (`docs/AGENT_WORKSPACE.md` on its agent branch). It is a
separate interface: `-agent` cannot be combined with `-capture`. Use capture-v1
for a posed still and the agent workspace for scripted play; neither establishes
retail parity or deterministic simulation.

Ported by Red (Claude) for Andrei, onto the native Linux branch.
