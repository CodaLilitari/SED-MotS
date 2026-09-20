# SED MotS decisions and discoveries

Append dated entries with stable IDs, evidence basis, work status, verification scope, reason and next action. Correct earlier entries explicitly using supersedes, retracts or narrows relationships. Keep this log concise; detailed receipts live in ignored build outputs and stable tests/source provide reproducible evidence. The log records decisions but remains answerable to contradictory source or test evidence.

## DEC-20260912-01 — Authoring roadmap and continuity

**Authority:** The user authorized implementation of agreed recommendations, continued bounded collaboration with Lily if willing, and durable lessons/discovery tracking. Goals may organize the work; they do not replace project files or create permission to publish unrelated material.

**Supplied review and engineering judgment:** Prioritize native authoring preservation, spatial/reference-safe editing, independent lighting authoring, controlled engine state and a complete playable outpost workflow. MCP should expose shared domain behavior. Treat build, structural, visual, behavioral and human-UX evidence separately.

**Direct source evidence:** `Light.cs` originally had a single intensity. `JedImporter.ReadLights` replaced legacy MotS white intensity with RGB intensity and discarded legacy RGB range, with a warning. `LightCalculator` used the same intensity for both channel families. There is a native importer but no native writer. The existing JKL writer already uses round-trip formatting for primary light numbers.

**Decision:** Preserve white intensity plus nullable independent RGB intensity and nullable legacy RGB-range metadata. Null retains legacy shared behavior; explicit equal/zero is authoring state. Do not make the metadata-only range affect baking. Preserve existing LIGHTS layouts and bind additive versioned authoring metadata to their canonical representation. Exact reader/writer implementation and tests are in progress.

**Additional discovery:** GUI light creation has a separate clone-from-selection path; copying only existing fields would lose the new metadata. Numeric inspector fields render only three decimal places and commit on focus loss, so light controls need a precision-preserving display and unchanged-value guard.

**Reference-safety lesson:** Avoiding deletion/reordering is a restriction, not proof that exported numeric identities remain stable. Every topology or resource-list edit needs appropriate reference accounting and exporter mapping evidence.

**Validation lesson:** Editor/patched-engine agreement may share the same mistaken assumption. Use independent references and feature-specific witnesses. Runtime resource resolution must eventually distinguish static closure, observed loaded dependencies and unresolved dynamic possibilities.

**State:** Implementation underway from editor `9d1d316`; prior validation counts remain historical. No new tests or package have passed for this milestone yet. Continue from [PROJECT_STATE.md](PROJECT_STATE.md).

## DEC-20260912-02 — Bound LIGHT-01 and separate evidence from status

**Narrows:** `DEC-20260912-01` milestone wording; native import -> supported JKL authoring round trip is included, native output writing is not.

**Evidence basis:** Collaborator review of supplied findings plus current source inspection. The collaborator is willing to continue in the same conversation for bounded checkpoints; this does not constitute independent verification of private source or test results.

**Decision and reason:** Adopt [LIGHT-01](LIGHT-01.md) with explicit field-presence rules and acceptance gates. Retain legacy RGB range without assigning unsupported falloff semantics. Distinguish absent values from explicit zero/equal; a fallback value is not historical evidence. Document old setter behavior separately from old input behavior.

**Tracking correction:** Record evidence basis, implementation state and passed/failed/not-run acceptance separately. Use stable IDs and explicit corrections so an old strong statement is not resurrected after compression. Dirty filenames are insufficient validation identity; record tested contents including relevant untracked files and executable hashes.

**Additional source finding:** JED/NJED preserve independent light intensities, but their historical copy routine itself omits fields and assigns blue from green. Delphi SED's native light model already collapsed intensities. Reference editors are evidence to audit, not behavior to copy indiscriminately.

**Work state:** Core fields/editing/copy/bake changes and focused tests are written; formats and GUI/agent integration are in progress. **Verification:** not run. **Next:** integrate, run focused and full checks, exercise packaged CLI and actual GUI controls headlessly, then update the gate record with artifacts. New spatial/rendering discoveries remain follow-on entries.

## DEC-20260912-03 — Integrated evidence and final contract review

**Updates:** Verification state in `DEC-20260912-02`; does not broaden LIGHT-01.

**Executed evidence:** Release build passed with zero warnings/errors and 884 unit tests at `build/dotnet/Release-6ee0e82f18f247f69958d2828723bf79/`. The actual Avalonia headless probe passed 49 checks, including 28 source-light checks (`build/light01-uiprobe-first.log`). The built, framework-dependent CLI passed 34 calls and 45 assertions (`build/light01-agent-preflight/receipt.json`); this was not yet the self-contained package. Earlier integration attempts found test-harness API signatures and nullable theory-argument typing mistakes, corrected before these results.

**Negative control:** `build/negativecontrol-source-light-20260912-02/receipt.json` records 677 captured source files, the isolated former MotS white/RGB collapse, its intended assertion failure (expected white 2.25, actual 0), an unaffected JK control, and 2/2 passing after byte-exact restoration. A compile failure was not counted as regression evidence. Shared sources and outputs were untouched.

**Review corrections:** New explicit agent layer assignments require an existing layer, with default 0 for an unnamed-layer document. Parser declared-count tails must not silently discard extra light rows. Canonical metadata binding needs a written byte/number specification and fixed independent digest witness. Signed-zero distinctions must agree between ordinary rows and metadata. An absent extension cannot distinguish a genuine legacy document from one whose metadata was stripped. A checksum is index-based consistency, not historical identity or authentication.

**Collaborator basis:** A bounded review accepted the field semantics and narrowed scope, and proposed request-presence/order, consumer-routing and pending GUI selection checks. These are reviewed recommendations until executed against current sources. Retain the distinction between actual headless controls and a native-window human usability review. OpenJK unknown-section behavior remains source-backed; newly emitted metadata has no claimed runtime/retail test in this milestone.

**Next:** Complete the bounded review checks, rerun integrated gates against frozen source, and build/test a clean-source Windows package. Keep native writing, ARCH authoring and renderer parity separate.

## DEC-20260912-04 — Freeze candidate and executable acceptance

**Updates:** `DEC-20260912-03` integrated results. The final Release build passed 908/908 tests, zero warnings/errors (`build/dotnet/Release-98b4d3d157b74fcf972e2e42260284a1/`). The focused format subset passed 73/73; this subset overlaps the full suite. The final headless probe passed 71 checks, including 50 source-light checks (`build/light01-uiprobe-accepted.log`). The updated CLI preflight passed 34 calls and 47 assertions (`build/light01-agent-final-preflight/receipt.json`).

**Finding and fix:** The newly added pending-edit/selection tests exposed reentrant `InspectorPanel.Refresh` calls during focused-content replacement. The pending value stayed bound to its original light, but duplicate visual controls appeared. Refresh now coalesces nested requests until content assignment finishes. The same unchanged test then passed for white, RGB, legacy range, undo/redo and replacement-document boundaries.

**Format/agent freeze:** [SOURCE_LIGHT_METADATA.md](SOURCE_LIGHT_METADATA.md) pins v1 grammar, indexed binding, numeric rules and an independent fixed checksum. Extra declared-count tails and mismatched signed-zero RGB refuse. Agent readback distinguishes absent from validated metadata on both sides of a conversion. Omitted/null/numeric requests and JSON order independence are exercised. Consumer reads were audited; MotS RGB baking uses effective RGB, while white and existing JK/Infernal Machine behavior keep the scalar field. The community/stock audit digests now include retained source-light fields; those corpus tools have been built but their historical corpus runs were not repeated for this milestone.

**State:** Source candidate is ready for a clean-worktree package. Self-contained distribution acceptance remains pending until its actual executable and matching source archive/history bundle pass the packaging workflow. The package receipt will identify its commit; any later checkpoint-only commit must not be confused with the packaged source commit.

## DEC-20260912-05 — LIGHT-01 complete and packaged

**Updates:** `DEC-20260912-04` package state. All required LIGHT-01 acceptance gates now have executable evidence. This does not close the overall editor/engine roadmap.

**Source identity:** Implementation and package commit `f6100fe6f5cad1b9e7b57d3518bf2406996f4eaf`. Packaging used a separate clean worktree; the three pre-existing `.DS_Store` deletions remain untouched in the working checkout. This entry and the completion checkpoint are a later documentation-only commit, not the packaged source revision. `build/light01-accepted/` retains the pre-package source-content manifest (678 present files), tested binary hashes and gate receipt.

**Package:** `build/packages/SED-MotS-0.4.0-mots.7-dev-f6100fe/`. `verification.json` records 681 verified distribution members, the exact ZIP member set, matching corresponding source archive, and a complete Git history bundle at the same commit. Windows ZIP SHA256: `4663f18edc27df69469070c5f425267148e375f9b7f66294da8f9d4bb6787d5e`. Source ZIP: `022c9f71d04e499ace3a53ec74b68614e08839b90e4ee5df8055ae76fb700a17`. History bundle: `25e8c939d0bdccda79f25ec13201d2e2be32f43f54f5f80d35bd5322e46cf3d9`.

**Executed package gates:** Clean-source Release build and 908/908 tests; self-contained smoke; workflow 24 calls/26 assertions; source-light workflow 34/47; design workflow 62/108; lit-snapshot workflow 34/111. Those four workflows total 154 process calls and 292 assertions, excluding the separate smoke checks. Snapshots were offscreen. Native GUI, game, retail and installer execution were not part of this run. All receipts are within the package output directory.

**Next:** Spatial selection and reference-safe editing remains planned. Build shared domain operations and a representative level-design acceptance task before adding a thin MCP transport. Native writers, full ARCH authoring and deterministic engine verification retain independent milestones. The collaborator remains willing to use the existing conversation for bounded checkpoints; technical recovery depends on these files and artifacts.

## DEC-20260913-01 — Public source location documentation

**Updates:** Current hosting/navigation information only; the September 9 private-hosting record and `DEC-20260912-05` package evidence remain historical facts.

**Change and provenance:** Document `CodaLilitari/SED-MotS` on `main`, with immediate GitHub parent `davidagnome/SED` and network source `smlu/SED`. Preserve the Andrei/Coda collaboration, original authors, GPL notices and exact source ancestry. The documentation follows imported source `2a1461aea49f56ef834282b89d67a899bc5f2838`.

**Verification scope:** Only Markdown navigation and hosting descriptions changed in an isolated source clone. Source identity, diff whitespace and linked source paths were checked. No editor build, runtime test or new binary package is claimed. Coda binary releases remain pending; each active checkout's remotes must be inspected separately.

## DEC-20260920-01 — Reproduce geometry preservation prerequisites

**Scope:** Resume the SED editor in `SED-MotS-Net`, publishing through `CodaLilitari/SED-MotS/main`. The unrelated engine checkout is not the editor workspace. Local main was safely fast-forwarded from `2a1461a` to the documentation-only account handoff `65e94af`; existing `.DS_Store` deletions were preserved.

**Direct source and executable evidence:** The writer's canonical sector-traversal ordering can disagree with original world-surface and vertex tables. `JklExportReferenceTests.SurfaceEditPreservesNoncanonicalGlobalSurfaceAndVertexReferents` compiled and failed after a light edit, reopening the first sector at `(10, 0, 0)` instead of `(0, 0, 0)`. An earlier test-literal compile error was corrected before this result and is not regression evidence. Receipt: `build/geometry-preservation-20260920/baseline-corrected/`. The eight new agent geometry/light tests yielded four intended failures and four passing controls at `baseline-agent/`. The old self-contained `.7` executable failed the enhanced design workflow's extrusion on the supported source-light section at `old-package-design/`.

**Decision and reason:** Complete [GEO-01](GEOMETRY_REFERENCE_PRESERVATION.md) before adding spatial selectors or MCP. Retain actual original serialized geometry slots where representable; refuse unsupported identity-changing exports rather than guessing COG types. Recognize validated source-light metadata and put `addBox` through the existing opaque-section guard. Test semantic geometry targets and stored light states after reopening, not just unchanged numeric tokens.

**Ownership/state:** One worker owns JKL parser/document/writer mapping and dedicated format tests; another owns geometry section guards and dedicated agent tests; root owns process-level workflow, review, documentation and integration. Baseline failures are recorded. Fixes are being implemented; post-fix build, workflow and package acceptance are not yet claimed. No game/retail run, general reference remapper or completed spatial selector milestone is implied.

## DEC-20260920-02 — Central writer contract and integrated evidence

**Updates:** Implementation and source/executable validation in `DEC-20260920-01`; package/publication still pending. The shared writer, not an individual topology command, enforces geometry identity and section capability whenever GEORESOURCE or SECTORS changes. Validated source-light metadata is accepted by addBox/extrude/adjoin. Imported slot mappings, owner/coverage checks, sequential sector labels, reciprocal adjoins, signed-zero comparisons and retained-source hashes support bounded preservation. Existing matching material slots remain stable under duplicate names; inferred registrations commit after successful output generation. No arbitrary COG typing/remapping or complete direct-model rollback is claimed.

**Independent review/corrections:** Reviewer suggestions were checked against local code and tests. A proposed trailing-comment header defect was rejected because FindSection already strips comments; actual extra pre-comment tokens are refused and normal comments have a positive control. Tests resolve synthetic COG witnesses through emitted tables, assert adjoin flags as well as mirrors, and cover equal-count overlapping/orphaned ranges. The process harness checks every input after apply and exact room bounds/counts/neighbors. Four older geometry-positive fixtures use an explicit known-sections-only derivative without changing assertions; opaque recovery and negative tests remain. The initial post-fix 82/83 result and corrected fixture are preserved rather than relabeled as a pass.

**Executed evidence:** Full Release solution build passed with zero warnings/errors and 952/952 unit tests, including 26 export-reference and 18 agent-geometry cases. Receipt/TRX: `build/dotnet/Release-5a3b21746918409f908e20492e52ac4c/`. Actual Avalonia headless probe passed 71 checks (`build/geometry-ui-probe.log`). Enhanced framework-dependent process workflow passed 69 calls/129 assertions at `build/geometry-preservation-20260920/design-final/receipt.json`. Both diagnostic PNGs were inspected: distinct interior directions show continuous geometry through open portals; diagnostic colors do not establish textured lighting or gameplay. Source manifests and negative baselines remain in the milestone build folder.

**Consultation:** With explicit user authorization, the selected source/test packet was delivered to the same existing review conversation using Sol Pro, Personal account. Packet SHA256: `b7514f8b882ef86e249408512194bb6b6f7d4c142b453ce947ff1dad1eb0b6d1`. Full source review response is pending; recommendations remain fallible input, not validation authority. Private correspondence/packet remain ignored build artifacts. No native picker was used; the direct file-chooser API attached only the allowlisted text file.

**Next:** Review any concrete remaining findings, commit explicit editor files, build/test a clean self-contained .8 handoff, verify source/history/distribution, then publish main and append completion evidence. No engine/retail run, native writer, stock/community corpus refresh or spatial-selector completion is claimed.
