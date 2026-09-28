# PLAY-01: background MotS mission authoring

Status: shared authoring and native gameplay acceptance passed locally on
2026-09-28; exact source review and distribution handoff are in progress.

The milestone connects the existing shared editor/CLI/MCP authoring operations
to a small original gameplay exercise. Four rooms run along a single corridor.
The player activates the gate's integrated control, the gate follows an authored
two-frame path, entering the objective room completes one goal, and entering the
last room completes the exit-room goal. This is an exit-room interaction, not a
tested episode transition. Materials, meshes and the placed mission COG are
original. Required game bootstrap resources remain external, hash-manifested
inputs; they are not distributed with source or review packets.
The guarded runtime stages custom resources as unique loose episode files.
Original objective UI strings remain in the authoring archive because the
capture resource policy refuses bootstrap-name overrides. Objective inventory
and exit-room state are tested; objective-menu wording is not.

## Shared authoring scope

- `addCog` resolves a script from explicit hash-bound archives and requires its
  reviewed script digest and complete named typed bindings. The initial scope
  is thing, sector, surface and placed-COG references. No guessed defaults or
  execution of COG code in the editor. Existing placed COG schemas must also be
  available: an older unchecked non-static numeric reference prevents append,
  because the new COG could capture it or shift later runtime allocations.
- `setThingFrames` writes the ordered absolute position/orientation frames of a
  path mover as one undoable edit. Repeated native frame records must survive
  round trips; unrelated source parameters remain intact. Final batch validation
  rejects later changes that leave the mover non-path, ARCH-bound or with
  inconsistent frame records.
- Both operations enter the existing source-bound plan/apply path, including
  output hashes, new output files and MCP outcome receipts. They do not require
  another MCP wrapper or a desktop automation layer.

`tools/new_playable_fixture.py` exercises the actual stdio server. An explicit
original seed supplies the basic room, templates and finite bootstrap resource
capacity. All room extensions, placed objects, mover frames and COG placement
use tool operations. The script is a reproducible client, not evidence that a
native Codex MCP session designed the level. Its original episode/resource
packaging is fixture support; no general-purpose recursive asset packager is
claimed. The existing GUI episode editor and test packaging remain separate
features.

## Runtime contract

Capture-v1 by itself proves an image was rendered. PLAY-01 adds an optional,
versioned request to a separately built OpenMotS executable, using the existing
hidden, muted, private-directory launch path. Input is supplied at semantic
control reads, then passes through ordinary player movement, activation reach,
collision, path motion and COG execution. Test controls must never directly
send a COG message, move the gate, set inventory, or teleport the player.

The bounded trajectory uses real elapsed time. It is not deterministic stepping
and no retail parity is inferred. Initial state and step boundaries report the
actual player pose/sector/health, path mover pose/frame/state, named numeric COG
state and goal inventory bin. Acceptance combines these independent signals;
an echoed request, COG latch alone, or screenshot cannot establish success.

The launcher retains exact source/staged hashes, script and telemetry identities,
bounded logs, process exit, and sampled visibility observations. It stops the
owned process if an owned window becomes visible or foreground. Polling does
not exclude a transient window between samples. Existing capture constraints
and negative tests continue to apply when gameplay is absent.

## Acceptance to run

1. Read back the four connected sectors, two placed objects, ordered mover
   frames and complete resolved COG references after actual MCP plan/apply.
   Verify exactly one reciprocal portal pair per corridor edge and no bypasses,
   complete box-face polygons, and unchanged topology after save/reopen.
2. Attempt forward travel while the gate is closed: the player remains before
   the gate and the objective is incomplete.
3. Activate through ordinary player controls, observe real path motion and
   arrival, then travel through the doorway and objective and exit sectors.
4. Run the same input without activation: the player must remain blocked and
   the objective incomplete.
5. Run a separately authored wrong-objective binding: the gate and traversal
   may work, but the mission objective must remain incomplete. Verify that the
   positive acceptance predicate rejects both negative controls.
6. Leave an additional collision barrier behind the moving gate: the gate can
   open but the mission must fail. Start with a completed objective in another
   variant: the initial-state predicate must reject it even if traversal works.
7. Repeat positive runs to expose timing sensitivity. Record actual variability;
   no repeat count can prove deterministic simulation or general engine parity.

Exact source review and runtime observations are separate gates. Reviews receive
only original project source and bounded technical summaries. Scraped Discord
logs, copied forum posts and retail assets remain private/local.

## Evidence

The corrected development editor passed 1,376 Core and 269 MCP tests. Fixture
generation performed 139 actual stdio calls, creating four variants and checking
exact ordered frames, binding targets, complete corridor topology and a second
save/reopen. The Python tool suite passed 104 tests, including 39 independent
PLAY contract/oracle tests and nine topology counterexamples. The corrected
authoring run's four level files and resource archives match the exact bytes
used in the successful runtime matrix below.
Native ASan/UBSan contract tests and capture regressions are recorded separately.

`build/play01-20260928/runtime-05/acceptance.json` passed all seven fresh engine
processes with candidate executable SHA256
`f85d015451ea805f52a648440efd65c227023ace9dfd1420fe02c1b1ab7c4879`.
That candidate is identified as engine base `74e4657c` plus its recorded PLAY-01
source diff, not a previously published engine binary. Its complete build,
source-overlay and runtime manifests remain in `engine-build-02/`.

| Case | Observed final sector / script phase / goal-bin 100 | Result |
| --- | --- | --- |
| Three positive processes | 3 / 4 / 3 | Full causal predicate passed |
| No activation | 0 / 0 / 1 | Closed gate prevented traversal |
| Wrong objective binding | 3 / 2 / 1 | Traversal worked, objective did not |
| Residual collision barrier | 0 / 2 / 1 | Gate opened, passage remained blocked |
| Precompleted goal | 3 / 4 / 3 | Rejected by initial-state predicate |

Each positive recorded the real activation collision hit and COG dispatch,
ARRIVED, the objective ENTERED event and the exit ENTERED event. Goal-bin 100
changed from visible/incomplete `1` to visible/complete `3` only after the
objective entry. These semantics were independently checked in the engine's
`SetGoalFlags` implementation and `SITHBIN_GOAL00 = 100` definition. Exit-room
phase is observed; no next-level transition or goal-menu rendering is claimed.
The largest observed sampling gap across the matrix was 40 ms. All seven
launches retained unchanged inputs and observed zero visible or foreground
owned windows under 50 ms polling; transient visibility between polls is not
excluded. The matrix receipt SHA256 is
`771a1f5d458566dcb7fbabad25257589356aac90dcb3ef5b0a02aee75b625db1`.

The first real diagnostic exposed two correctable errors: the observer initially
queried a parser hash table absent from live COG instances, and the original
recipe used the wrong MoveToFrame speed units. The observer now reads copied
instance symbol names/values. The recipe passes `7`, because the engine scales
that verb's speed argument by `0.1`, producing `0.7` world units per second.
Failed attempts are retained separately from the accepted matrix.

Exact editor-source review led to independently verified corrections before
packaging: append refuses unresolved runtime COG references and missing existing
schemas, COG append undo preserves in-memory numbering, complete parameter
records participate in roundtrip verification, and final batch validation keeps
authored movers eligible. Multi-sector and retained-global-order tests distinguish
surface indices from sector-local addresses. Source reviewers did not execute
the local tests or game processes; those are separate evidence.

A separate oracle review exposed a synthetic false success: an inflated
simulation-clock delta could relax the mover speed check enough to accept a
teleport. The fixture now uses observed wall time alone for physical motion
allowances. Raw trace validation also refuses regressing/wrapped sample game
clocks and event game clocks outside the ordered sample interval. Regression
tests include both a single spike and a retained monotonic offset; monotonicity
alone would not fix the speed loophole. Revalidation of all seven original raw
runtime traces retained the expected three positive and four negative results.
This counterexample concerned the evaluator, not a native teleport capability.

## Reproduce the authoring exercise

Use the matching Windows distribution and a new output directory:

```powershell
python tools/new_playable_fixture.py --server mcp/sed-mcp.exe --agent agent/sed-agent.exe --output C:/LevelWork/PLAY01
```

This starts only the local stdio editor server and its agent executable. It does
not start a game. The receipt records requests/replies, reviewed hashes,
save/reopen results, original assets and exact runtime staging files. A lit
editor snapshot can inspect the authored gate independently of gameplay.

Runtime acceptance requires a separate build implementing
`OPENJKDF2_PLAY_REQUEST` / `sed-engine-play-v1`, plus an explicit bootstrap
manifest in the [capture contract](ENGINE_CAPTURE.md). Supply the exact local
retail archive/configuration paths and hashes; no installation is discovered or
downloaded. Use only the matching rebuilt runtime DLLs:

```powershell
python tools/test_playable_workflow.py --fixture C:/LevelWork/PLAY01/authoring-receipt.json --bootstrap C:/LevelWork/engine-inputs.json --engine C:/LevelWork/runtime/openjkdf2-64.exe --output C:/LevelWork/PLAY01-runs
```

The full run uses three positive processes and four fault controls. `--only`
selects a diagnostic case and is labeled diagnostic, never full acceptance.
An ordinary engine without the extension cannot satisfy the required telemetry.
