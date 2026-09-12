# LIGHT-01 — Independent source-light preservation

State: complete. Packaged implementation commit: `f6100fe6f5cad1b9e7b57d3518bf2406996f4eaf`. Updated 2026-09-12.
Operative decisions: `DEC-20260912-01` through `DEC-20260912-05` in [WORK_LOG.md](WORK_LOG.md).

## Bounded objective

Preserve independently supplied white/scalar intensity, RGB intensity, primary range and legacy RGB range through native import into the supported JKL authoring document, JKL save/reopen, GUI and agent editing, undo/redo and copying. Preserve single-intensity behavior and make authoring-only changes participate in revision safety.

The repository has no native JED/SED/NED writer. Native input -> preserved JKL -> JKL reopen is the supported path; it is not a native-format round trip. New agent baking, ARCH baking, new falloff semantics, renderer upgrades and full human usability acceptance are follow-on work. This milestone cannot recover values discarded by an earlier save.

## Field compatibility contract

| Field | Read and missing-value rule | Edit rule | Write rule | Known consumer |
|---|---|---|---|---|
| `Intensity` | Native white/scalar value; ordinary single-intensity sources keep their existing scalar | Existing setter changes white; inherited RGB follows, explicit RGB stays independent | Existing round-trip-precision ordinary LIGHTS value | White bake; RGB fallback when override absent |
| `RgbIntensity` | Legacy 12-field input keeps explicit RGB including zero/equal; ordinary single-intensity input is null | Explicit value targets RGB only; null restores inheritance. Agent nonnull authoring requires MotS | Bound versioned source-light metadata retains value/presence | MotS RGB bake through `EffectiveRgbIntensity` |
| `Range` | Existing primary range | Existing setter changes primary range | Existing round-trip-precision ordinary LIGHTS value | Both channels in the inspected bake |
| `LegacyRgbRange` | Legacy supplied value retained; absent input is null | Metadata-only edit or explicit removal | Bound versioned source-light metadata retains value/presence | Retained; not consumed by the inspected bake path |
| Color | Preserve source ColorF values, including inactive JK metadata | Existing GUI color controls and MotS agent authoring; no implicit grayscale conversion of retained data | Ordinary MotS LIGHTS color; bound metadata when otherwise lost | MotS RGB bake; inactive in JK bake |
| Flags/layer/position | Preserve existing known fields | Shared commands; agent optional fields are explicit | Ordinary LIGHTS fields; exact supported numeric precision | Existing selection/lighting/editor consumers; no new layer policy |

`setLight` has one optional property per distinct quantity. `intensity` always means white/shared fallback; `rgbIntensity` always means RGB override. There are no competing legacy/scalar aliases whose assignment order could override one another. Duplicate JSON keys are refused. Null and omission have different meanings: omission preserves current state; null explicitly removes an override/metadata value.

Ordinary LIGHTS layouts remain unchanged. Additive `SED_SOURCE_LIGHTS` metadata is tied to the canonical ordinary LIGHTS records, so stale or unsupported metadata is rejected rather than overriding an edited base record. Original raw source and edited model authority are tested separately. Other editors that do not understand this extension may discard it; runtime format acceptance is separate from authoring fidelity.

## Acceptance record

| Gate | Evidence required | Result |
|---|---|---|
| Native preservation | Unequal 12-field fixture; missing/zero/equal variants; supported fields survive import, JKL reopen and unrelated edit; explicit token/value assertions | Passed in final 908-test suite; 73 focused format tests including independent canonical digest |
| Shared editing/reversibility | Independent channels, legacy fallback, inactive metadata range, copy isolation, undo/redo; actual Avalonia controls exercised headlessly without desktop takeover | Passed: Core/agent regressions plus 71 headless UI checks, 50 specific to source lights |
| Revision/template safety | Metadata-only revision change, stale source/template/output refusal, malformed/unknown metadata rejection and raw-source bypass checks | Passed: final unit suite and 34-call/47-assertion CLI preflight |
| Legacy compatibility | Existing supported fixture/bake behavior unchanged; ordinary LIGHTS layout retained; source loss clearly scoped | Passed: JK/MotS/Infernal Machine fallback and ordinary-layout fixtures; runtime/other-editor behavior unverified |
| Former-behavior negative control | Reintroducing the old collapsing import/calculation behavior fails an independent regression | Passed: intended MotS failure, JK control pass, restored 2/2; receipt in `build/negativecontrol-source-light-20260912-02/` |
| Integrated build/regression | Full Release build and appropriate test suite at recorded source state | Passed: 908/908, zero warnings/errors; `Release-98b4d3d157b74fcf972e2e42260284a1` |
| Packaged CLI | Built distribution exercises query/schema/convert/plan/apply and output readback | Passed: self-contained `0.4.0-mots.7-dev`, 34 source-light calls / 47 assertions; all package workflows and 681-member distribution verification passed |

Evidence basis, work status and verification scope are independent. A code review may establish a finding before tests run. Headless control tests are GUI implementation evidence; a native-window/manual usability review is separately not run unless recorded. Retain a source snapshot or patch/manifest with hashes including relevant new files, plus tested executable hashes. Close the app goal only after the required rows have evidence.
