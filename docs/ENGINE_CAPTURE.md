# ENGINE-01: background runtime lighting inspection

This workflow takes original SED-authored MotS architecture into the existing
OpenMotS capture build without driving the editor or taking over a Windows screen.
It uses an explicit input manifest, a new private working directory and a fixed
camera. Runtime results are separate from editor snapshots and structural tests.

The acceptance claim is intentionally limited to an observable RGB differential
in this static fixture. Capture v1 runs wall-time simulation. It does not report
effective lighting mode, resolved resource bytes, loaded ARCH arrays or draw-path
counters. Engine mono-mode crossover, retail parity, deterministic simulation and
playable-mission qualification remain separate work.

## Tools and prerequisites

Python 3.10 or newer and Windows are required for actual capture. No Python
packages, API key, model service or MCP transport is required. The supplied
`sed-agent.exe` must offer ARCH-01 authoring. The engine must implement
`sed-engine-capture-v1`; an ordinary retail executable is not a substitute.

1. `new_engine_lighting_fixture.py --agent <exe> --output <new-directory>`
   creates original geometry, model, material and colormap data. It uses real
   agent plan/apply/query/validate calls to author the lighting and checks samples
   against an independent calculation. It never starts the game.
2. `invoke_engine_capture.py <request.json> --output <new-directory>` stages and
   captures one explicit scenario. It accepts exact absolute source paths,
   SHA256 values and relative destinations; it does no installation discovery.
3. `test_engine_lighting.py --fixture <fixture-receipt.json> --inputs
   <engine-inputs.json> --output <new-directory>` executes the fixture matrix and
   image checks. `--case baked` is a diagnostic run, not full acceptance.
4. `compare_engine_lighting.py <request.json> --output <new-json>` compares
   already captured PPMs. Its panel mask is projected from authored geometry and
   the camera, independently of observed pixel differences.

The bootstrap manifest has `schemaVersion: 1`, an optional documentary
`engineCommit`, and `files`. Each file supplies `source`, `destination`, `sha256`
and `role`. Roles are `engine`, `runtime`, `resource`, `scenario` or `config`.
The engine and DLL filenames are `openjkdf2-64.exe`, `OpenAL32.dll` and
`libGameNetworkingSockets.dll`. Include locally owned bootstrap assets explicitly;
they are not distributed by these tools. The original fixture supplies scenario
files separately. The manifest's supplied commit is not a cryptographic proof of
the executable's build source; retain a separate build receipt.

Two original configuration files are required. `registry.json` contains zero
`sfxVolume`, `musicVolume` and `cutsceneVolume`; it may also set
`Window_isFullscreen: false`, `Window_isHiDpi: false` and `InstallType: 9`.
`openjkdf2_cvars.json` contains only `net_disableUpdates: true`. Staged configuration
rewrites are recorded and allowed. All other staged inputs and every source file
must remain unchanged.

## Isolation and receipts

The launcher composes only the capture whitelist: `-capture -autostart -sp
-motsCompat -noHUD -episode <name> -map <name.jkl>`. Ordinary editor launch-plan
arguments such as `-path`, `-debug` and `-devmode` are incompatible with this
capture protocol and are not appended.

Every source is copied and checked. Bounded GOB/GOO index inspection rejects
competing case-insensitive logical paths or basenames for custom scenario files.
It reads archive indexes, not proprietary resource payloads. A final source and
staging hash pass occurs after staging and before process creation. This is
launcher-verified staging and collision exclusion, not engine-observed resource
resolution or a general proof about arbitrary engine builds.

The process has a finite deadline, capped retained stdout/stderr and a sampled
budget for generated files. Only its own window handles are observed; the tool
does not read other applications, window titles or screen pixels. A visible or
foreground owned window causes termination. Polling does not exclude transients
between samples. Windows Job containment applies after assignment; the interval
between process creation and assignment is recorded rather than represented as
atomic containment of every hypothetical early child.

Receipts retain requested camera fields separately from capture-v1's reported
fields, which echo the request. `effectiveCameraObserved` and
`runtimeResourceResolutionObserved` remain false. Success requires exit zero,
the exact P6 RGB8 dimensions, an exclusive new image/engine receipt, matching
reported request fields and unchanged immutable inputs. Accepted output roots
retain failure receipts as well; an already existing output directory is refused
without modification.

The integrated engine additionally reports a complete bounded world/player
state extension: loaded map, MotS compatibility, single-player state, player
model/soundclass names, optional requested model and hierarchy-node count.
When present, the checker requires all these known fields, checks the loaded
map and mode against this request and validates the reported value types and
bounds. Legacy receipts without the extension remain accepted; partial or
unknown extensions are refused. These are engine-reported state facts, not
verification of the loaded resource bytes, effective camera or ARCH arrays.

## Controls and interpretation

The fixture uses a neutral asymmetric panel against a colored world. Its
source-point bake is the existing unoccluded, non-directional ARCH-01 recipe.
The RGB-swap case exchanges red and blue source color while retaining green;
the white-only case changes white intensity with explicit RGB unchanged. Reset
creates a valid private zero allocation; it is distinct from clearing a binding
and exercising the model's scalar fallback.

The image oracle requires a localized bake response, the expected red/blue swap,
per-pixel direction consistency and green preservation inside the independent panel mask and a stable
world outside it. White-only, extension-omitted and repeat comparisons supplement
that combined evidence; none alone establishes ARCH consumption. The interleaved
control places exact editor metadata before meaningful known sections and
requires the same frame. Known-section bytes are checked independently.

The original runtime scaffold includes a separate off-camera player model, an
explicit bootstrap soundclass reference and finite resource table headroom for
inventory initialization. Declared resource counts are
allocation capacities in this engine, not simply populated-row counts. The
fixture uses an original 34-row header accepted by `QOL_IMPROVEMENTS`; builds
that enforce the proprietary historical banner are outside this test.

## Review and execution record

Lilitari, in the existing project conversation using ChatGPT 5.6 Pro, designed
acceptance controls and identified the staging-versus-resolution ambiguity. Her
Git connector could not access the forks, so her contribution is a briefing and
upstream-informed contract review, not a current-fork code audit. The capture
workflow adopts collision exclusion, final staged hashes and metadata preceding
known sections; proposed engine instrumentation remains future work.

Grok Build returned `grok-4.6-build` for a requested `grok-4.6/xhigh` review of
selected committed engine source. Its useful distinction between valid reset
and unbound fallback was adopted. The suggestion to retain the same ARCH index
was corrected: SED deliberately appends a private allocation and binds its new
valid index. Source inspection also resolved its question about scalar base
contamination: the engine excludes that scalar base from RGB dynamic scratch.
The corrected findings and acknowledgment were exchanged in the same session.

Direct inspection of the first complete capture matrix exposed a gap in the
initial differential oracle: a partly black panel could retain enough visible
RGB response to pass. Acceptance now requires red response across at least 98%
of the independent panel interior and the expected green world across at least
98% of its independent outside mask. The original 2x2 materials exposed an
engine packed-row versus SDL surface-pitch loading defect. The earlier passing
matrix remains superseded evidence, not a completed ENGINE-01 gate.

Version reconciliation then established that the selected `b1be98bd` capture
executable predates an existing fix: published engine main already contains
`685dd18e` and the integrated capture build at `197c2625`. Main `74e4657c` has
identical production source to that integrated build. The isolated replacement
implementation made during this investigation is retained as local research;
it is not the engine correction published by this editor milestone. Final
acceptance uses the recovered integrated executable with freshly checked hashes
and source payload, while retaining the unchanged original 2x2 scenario.

Independent local review reproduced acceptance-tool errors: opposite channel
errors could cancel in a mean, and an unreadable final input manifest could
suppress the failure receipt. The channel checks now retain per-pixel evidence.
Manifest snapshot identity and refusal-control provenance are also tested.
Their regressions exercise the counterexamples directly. Runtime gates and final artifact identities are
recorded in `PROJECT_STATE.md` and `WORK_LOG.md` only after actual execution.
