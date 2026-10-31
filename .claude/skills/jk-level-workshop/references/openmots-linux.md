# OpenMotS on Linux: engine stills and scripted play in Red's workspace

Red builds the OpenMotS engine (OpenJKDF2-MotS-Fixes) for Linux in her cloud workspace. It draws on a private Xvfb display with software OpenGL (Mesa llvmpipe) and offers two interfaces:

- **Posed stills (capture-v1):** SED's capture runner stages a scenario and the engine writes one still of a level from a fixed camera after a set number of frames, plus receipts. The runner's Linux engine profile is on SED-MotS's `claude` branch (`docs/ENGINE_CAPTURE_LINUX.md` there).
- **Scripted play (Coda's agent workspace, 30 October 2026):** a live single-player session driven through the game's ordinary keys, mouse and menus, with read-only state, screenshots, and JSON scenario recipes that produce a report.

Neither is any of these:

- deterministic simulation (the engine runs in wall time);
- retail parity;
- proof of which resource bytes the engine resolved;
- a judgment of feel, sound or multiplayer behaviour.

## Which engine build

| Purpose | Ref | How `openmots_cloud_setup.sh` builds it |
|---|---|---|
| Stills to compare with Coda's Windows ENGINE-01 results | `2c6008db` (the default) | The repository's `build_linux64.sh` |
| The current engine: scripted play, and stills too | `f554833f` (`--agent`) | CMake with `-DOPENJKDF2_AGENT_HOOKS=ON` into `build_agent_linux` |

- **`2c6008db`** has the same production source as `74e4657c` and the integrated build `197c2625` that passed Coda's Windows ENGINE-01 matrix (`matrix-02`); it adds only docs.
- **`f554833f`** is the tip of Coda's branch `agent/linux-workspace-20261030`: main at `ea715f21` plus the agent workspace and mod scenarios.
  - **Main has moved 59 commits past `2c6008db`.** That includes a merge of upstream OpenJKDF2 and fixes to translucent surfaces, the texture cache, MotS puppet submodes, COG `VectorCross`, the COG stack and the physics-step clock. Lit stills from it can differ from `matrix-02`.
  - **The hooks don't change stills.** Coda checked that with the hooks compiled in but not activated, capture-v1 output is byte-identical to the unmodified build of the same main.
  - **Rule of thumb:** use `2c6008db` for comparisons with Coda's Windows results, and `f554833f` for anything about the current engine.

## Verified (cloud workspace, Ubuntu 24.04)

| Date and build | Check | Result |
|---|---|---|
| 27 Oct, `2c6008db` | Build | Builds unchanged with `build_linux64.sh`, about 8 minutes on 2 CPUs. Builds aren't byte-identical across build directories, so each job pins its own engine hash from `build-receipt.json`. |
| 27 Oct, `2c6008db` | Start check | Refuses `-capture` without a request: exit 6, nothing written. |
| 27 Oct, `2c6008db` | Through SED's runner | Starts in its own process group on a private display with TCP off. Without game files it stops at its guard (`capture: required assets unavailable; installation UI is disabled`). |
| 27 Oct, `2c6008db` | ENGINE-01 authoring and driver | `new_engine_lighting_fixture.py` prepared all eight lighting cases with the Linux agent. `test_engine_lighting.py` ran through staging and launch, up to the runner's archive-index check, which correctly refused synthetic stand-in archives. |
| 31 Oct, `f554833f` | Agent build | `openmots_cloud_setup.sh --agent` built it in about 8 minutes on 2 CPUs (SDL3 and GameNetworkingSockets from the pinned submodules). It refuses `-capture` without a request (exit 6) and `-agent` without a private session (exit 6, `agent: invalid options or private staged session`). `agent_workspace.py doctor` passed. Coda's Python suites for the workspace, scenarios and reports passed: 22, 13 and 21 tests. |
| — | Not yet | A real still or a real play session. Both need Andrei's own game files; see below. |

## Keeping game files private

Stills and play sessions both need Andrei's own game files. They are licensed data:

- **In use:** stage them into a job folder in the cloud workspace for the run only, and delete the staged copies and sessions afterwards. The agent workspace copies the asset tree into each session, so a session folder holds game files too.
- **Never** put them into a deliverable, packet, commit or artifact, or commit them back to Andrei's computer.
- **`bootstrap.json` and `session.json` name local paths and identify owned game files.** They stay private, like the files.
- **Images:**
  - those of original SED scenarios can be shared when Andrei says so;
  - those showing retail content stay with Andrei.

### Where the game files are

- **Coda's ENGINE-01 copy:** her Windows run took its MotS files from a clean retail copy on Andrei's computer. That folder isn't connected to Red's sessions.
- **Access was declined once:** on 27 October Red's request for that folder was refused by the app's permission check. Don't request it again on your own initiative.
- **When Andrei wants a run,** he can connect that folder or his own JK or MotS installation, or attach the files.

## Posed stills (capture-v1)

### What a still needs

- **The engine:** `bash scripts/openmots_cloud_setup.sh <scratch>/openmots` (or `--agent`) builds it and writes `build-receipt.json` with its SHA-256.
- **SED's tools:** `bash scripts/sed_cloud_setup.sh <scratch>/sed` gives the checkout (`<scratch>/sed/src/tools/`) and the package (`<scratch>/sed/sed/SED-MotS-…/agent/sed-agent`).
- **Three of Andrei's own MotS files:** `resource/JKMRES.GOO`, `resource/JKMsndLO.goo` and `resource/jk_.cd`. This is exactly the bootstrap Coda's Windows ENGINE-01 run used; `openmots_capture.py` knows its hashes and reports whether a copy matches. The four episode GOOs (`--episodes`) are only for scenarios that need them.
- **Two original configuration files,** written by the helper. They're byte-identical to Coda's: all volumes zero, windowed, updates off.
- **The scenario:** a SED-made JKL, its `episode.jk` (written by the helper) and any custom MATs, 3DOs, CMPs or COGs. Custom names must not collide with entries in the staged archives, or the runner refuses before launch.

### Steps

1. **Engine:** `bash scripts/openmots_cloud_setup.sh <scratch>/openmots`. Run it in the background and poll `build.log`.
2. **SED:** `bash scripts/sed_cloud_setup.sh <scratch>/sed`.
3. **Stage the three files** into `<job>/game/resource/`.
4. **Bootstrap:**

   ```text
   python3 -B scripts/openmots_capture.py bootstrap --engine <scratch>/openmots/src/build_linux64/openjkdf2 \
     --game-dir <job>/game --output <job>/boot --engine-commit <commit from build-receipt.json>
   ```

   Read `matchesEngine01Baseline`. A mismatch isn't an error (another retail copy can differ), but say so when comparing with Coda's results. Before a long run, `openmots_capture.py check --bootstrap <job>/boot/bootstrap.json` re-hashes everything.
5. **Capture one level:**

   ```text
   python3 -B scripts/openmots_capture.py request --bootstrap <job>/boot/bootstrap.json \
     --level <SED output>.jkl --sector N --position X Y Z --pyr PITCH YAW ROLL [--fov 90] [--size 640 480] \
     [--file <path>=mat/<name>.mat ...] --output <job>/req-01
   python3 -B <scratch>/sed/src/tools/invoke_engine_capture.py <job>/req-01/request.json --output <job>/cap-01
   python3 -B scripts/openmots_capture.py view <job>/cap-01/capture.ppm --output <job>/cap-01.png
   ```

   - **The camera** must sit inside the named sector. Take positions from SED queries, not guesses.
   - **Look at the PNG before describing it.**
   - **Timeouts:** llvmpipe is slower than a GPU. Raise `--engine-timeout-ms` up to 60000 if a large level times out.
6. **Or run the ENGINE-01 lighting matrix:**

   ```text
   python3 -B <scratch>/sed/src/tools/new_engine_lighting_fixture.py --agent <package>/agent/sed-agent --output <job>/fixture
   python3 -B <scratch>/sed/src/tools/test_engine_lighting.py --fixture <job>/fixture/fixture-receipt.json \
     --inputs <job>/boot/bootstrap.json --output <job>/matrix
   ```

   Compare `acceptance.json` with Coda's Windows `matrix-02`, which passed (Andrei can point to it; read it in place, read-only). Same engine source, same bootstrap hashes, a different platform and renderer: report it as exactly that.

### Reading the receipts

- **`launcher-receipt.json` establishes:**
  - launcher-verified staging (source, staged and pre-launch hashes);
  - the private display;
  - the engine's exit status;
  - the image format and size;
  - that inputs were unchanged.
- **It doesn't establish:** the effective camera, runtime resource resolution, determinism, retail parity or gameplay. The receipt keeps those flags false; keep them false in the account too.
- **Renderers:** llvmpipe images can differ from Andrei's GPU in filtering and precision. Compare captures made with the same renderer, and state the renderer.

| Failure | Usual cause |
|---|---|
| Exit 6, `required assets unavailable` | Bootstrap files missing or not the MotS ones |
| `Custom resource aliases collide or an explicit bootstrap archive index is unsupported` | A custom file name matches an archive entry, or an archive isn't a readable GOB/GOO |
| `invalid request or isolated session` | The request file or session marker; rerun through the runner, never by hand |
| `Capture process exited with code …` with an engine error in `stderr.log` | A scenario problem the engine reports: missing template, material or COG. Fix the level through SED |
| Runner timeout | Raise the engine timeout (max 60000 ms) and the runner timeout above it |

## Scripted play (Coda's agent workspace)

Coda's guides are in the engine checkout: `docs/AGENT_WORKSPACE.md`, `docs/MOD_SCENARIOS.md` and their validation pages. The tools need Python 3.11 or newer (the cloud workspace has 3.13), Xvfb and Mesa's software OpenGL.

1. **Build:** `bash scripts/openmots_cloud_setup.sh <scratch>/openmots-agent --agent`. The receipt records the start check and `agent_workspace.py doctor`.
2. **Prepare a session** from Andrei's installed game tree (`resource/` and `episode/`), staged privately. Each session is new and single-use; the tool copies the assets and records their hashes:

   ```text
   E=<scratch>/openmots-agent/src
   python3 -B $E/scripts/agent_workspace.py prepare --data-root <job>/game --game mots \
     --session <job>/session-01 [--startup <job>/mod] [--episode-file <job>/my-level.goo]
   ```

   A candidate goes in as a startup mod directory (`-path startup`) or an extra episode container.
3. **Run it in the background,** then drive it from separate calls:

   ```text
   python3 -B $E/scripts/agent_workspace.py run --binary $E/build_agent_linux/openjkdf2 --session <job>/session-01 \
     --episode <episode> --map <level.jkl> --xvfb --software --seconds 180
   python3 -B $E/scripts/agent_workspace.py command --session <job>/session-01 state
   python3 -B $E/scripts/agent_workspace.py tap --session <job>/session-01 Enter
   python3 -B $E/scripts/agent_workspace.py tap --session <job>/session-01 --seconds 0.4 Up
   python3 -B $E/scripts/agent_workspace.py command --session <job>/session-01 mouse 35 -10
   python3 -B $E/scripts/agent_workspace.py command --session <job>/session-01 capture
   python3 -B $E/scripts/agent_workspace.py command --session <job>/session-01 release
   python3 -B $E/scripts/agent_workspace.py command --session <job>/session-01 quit
   ```

   - **"Ready" means the controller answered,** not that the level loaded. Wait until `state` shows `levelActive: true` and a non-null `player`. A level's objective screen may need Enter first.
   - **Held keys release themselves** after two seconds without another input command. Use `tap` for presses and `release` to clear everything.
   - **Keys are named** (letters, `Digit0`–`Digit9`, arrows, Enter, Escape, F1–F12…) or given as SDL scancodes. The profile's normal bindings decide what they do; F9 is quicksave.
   - **`capture`** writes a new PPM of the displayed view under the session's `agent/` folder. Look at a later frame after a load or menu change, since a transition can capture blank.
4. **Or run a scenario recipe** for a repeatable smoke test with a report:

   ```text
   python3 -B $E/scripts/agent_scenario.py --scenario <recipe.json> --binary $E/build_agent_linux/openjkdf2 \
     --data-root <job>/game --session <job>/check-01 [--startup <job>/mod] --xvfb --software --seconds 90
   ```

   - **Recipes are JSON data:** `wait`, `assert`, `checkpoint`, `tap`, `command` and `sleep` steps, with state comparisons and distance checks. `examples/agent/jk-smoke.json` is the stock JK first level.
   - **Results:** `report/index.html`, `summary.json` and `report/captures/*.png`. The tool exits 0 only if every step passed and the engine closed cleanly, and 2 on failure. Red reads the PNGs before describing anything.

**Limits to state with every result:**

- **Real time.** Inputs enter the normal input path, while the game keeps its own clock, physics and COGs. Movement depends on walls, enemies and bindings, so an accepted input isn't proof of the intended outcome, and repeated runs can differ.
- **Single-player only.** The agent refuses multiplayer, dedicated-server and capture modes. `-agent` and `-capture` can't be combined.
- **MotS is unproven here.** Staging supports MotS, but Coda's live acceptance used JK data, so treat a first MotS session as exploratory and say so.
- **Not retail.** It's OpenJKDF2 on a software renderer.

## Gameplay and other routes

- **SED's gameplay contract (PLAY-01):** `engine_play_contract.py`, `new_playable_fixture.py` and `test_playable_workflow.py` pass a `gameplay` block through SED's capture runner. That hasn't been exercised on Linux yet; treat a first run as exploratory and say so. The agent workspace is the more direct route for live checks.
- **The device shell on Andrei's computer** has Xvfb, but it lacks GTK 3, which the engine links against, and it can't build the engine (no CMake, development headers or root). Each call is also capped at three minutes. Engine runs stay in the cloud workspace.
- **Andrei's own playtests and Coda's Windows harness** remain the evidence for feel, timing, sound and multiplayer.
