# Resource-aware architectural lighting validation

Status: **planned, not implemented**. Source review completed 2026-09-12 against
editor commit `9de27d3d967752bfc0898603c2c8aa325f53ee04` and engine commit
`5fa4644d03080fef69d7137eaa2c8f295c47bd63`. Concurrent editor rendering and hierarchy
changes are outside this review. No engine was launched. No user input or new
software installation is currently needed to implement this design.

## Problem and existing protection

A MotS thing's `ArchLightingIndex` addresses per-mesh mono/RGB arrays. A same-name
3DO can change its contents or lower-detail geometry while leaving that index
unchanged. Export currently cannot establish that those arrays still fit the
resolved resource.

- [AgentWorkspace](../dotnet/src/Sed.Formats/Automation/AgentWorkspace.cs) binds
  native `sourceRevision` to the project and the content, role and existence of
  its bundled/companion template inputs. Plan executes shared Core commands on
  an isolated document, serializes and reparses it, checks operation readback,
  and retains input dependencies for no-overwrite commit. Resource archives are
  not inputs to plan/apply or conversion.
- [JklWriter](../dotnet/src/Sed.Formats/Jkl/JklWriter.cs) checks architectural
  index bounds and imported/current model-name equality when generating things.
  It cannot verify external model content or topology.
- [SnapshotResources](../dotnet/tools/Sed.Agent/SnapshotResources.cs) already
  accepts explicit archive paths and SHA256s, applies declared archive order,
  refuses ambiguous resources, bounds decoded input, records used entry hashes,
  and rechecks both open handles and current named paths before PNG promotion.
  It is currently internal to the CLI and includes rendering-specific adapters.
- [SceneAssembler](../dotnet/src/Sed.Rendering/SceneAssembler.cs) checks an
  available native normalized model fingerprint and exact geoset-zero mesh and
  vertex counts for preview. [ThreeDoParser](../dotnet/src/Sed.Formats/ThreeDo/ThreeDoParser.cs)
  currently loads geoset zero; full lower-LOD metadata is unavailable.

These checks are valuable but do not constitute resource-aware export safety or
an architectural-lighting authoring workflow. The agent can clear a binding;
it cannot yet author or explicitly establish a new binding baseline.

## Shared resource and validation contract

Extract archive capture/resolution into `Sed.Formats`, leaving texture conversion
in the renderer/CLI adapter. The shared resource provider should expose bounded
resource bytes, model identity/topology, used-resource receipts and an unchanged
check. Keep explicit paths, hashes, first-archive precedence and current resource
budgets. Do not discover installations or recursively scan directories.

Add a bounded all-LOD topology reader without requiring lower-LOD rendering.
Validate declared geometry-set/mesh/vertex counts, header ordering, duplicates,
truncation and aggregate budgets. The separate Delphi fork's
`src/ARCHLIGHTING_IO.INC` and `src/ArchitecturalLighting.pas` supply the reviewed
behavior: geoset zero matches the architectural arrays exactly; each later LOD
may use a prefix of the available mesh and vertex slots. Current engine
`src/Engine/sithRender.c`, in `sithRender_DrawLevelGeometryThing`, likewise checks
all LOD array accesses before binding. JK/MotS models support at most four
geometry sets in that engine path.

A shared, side-effect-free validator should accept the level/thing, resolved
model name, normalized fingerprint, all-LOD shapes and resource identity. Return
typed per-thing diagnostics for:

- Missing architectural object or model; changed model name; ineligible thing
  flags after template and instance resolution.
- Missing historical baseline, unsupported fingerprint normalization, or changed
  model content under the same name.
- Geoset-zero mismatch or a lower LOD requiring more mesh/vertex slots than the
  stored arrays provide.

Preview and export should use this validator rather than independent checks.
Topology coverage prevents invalid array access; it does not prove that a lower
LOD's vertex order was sensibly authored or that the original bake was correct.

## Planning, applying and exporting

Extend the versioned edit/conversion contract with an explicit resource manifest
and a separate `resourceRevision`. Keep `sourceRevision` for the document and
template dependencies; resource identity should not silently change its meaning.
The resource revision and receipt should bind archive order/hashes, used model
entry identities/hashes, and the validator version.

Plan resolves and validates against captured resources and includes the exact
validation result in the reviewed output receipt. Apply recomputes the plan with
the same manifest and rejects changed resources or an unexpected reviewed output
hash. Commit checks captured dependencies again immediately before promoting a
new file. GUI export must consume a result bound to the exact current document
and resource state, not a persistent boolean saying validation once passed.

Do not make old resource-free operations claim this new guarantee. Distinguish
structural preservation from verified-resource export in capabilities and
receipts. An unsafe or unverified binding must remain available in the working
document for diagnosis; do not silently erase arrays or substitute another model.
An explicit clear/rebind operation can produce a reviewed export after the
diagnostic is understood.

## Historical provenance and authoring

An imported JKL has no historical normalized model fingerprint. Resolving a model
today can establish current topology compatibility; it cannot prove which model
was used for the original bake. Report that state separately from a native
project whose stored baseline fingerprint matches.

The existing normalized fingerprint is not the raw resource SHA256. Reuse
[ArchitectureModelFingerprint](../dotnet/src/Sed.Formats/ThreeDo/ArchitectureModelFingerprint.cs)
and preserve its unsupported-encoding/line-length result rather than guessing a
Windows text conversion. Raw entry hashes remain separate receipt fields.

A future shared Core `bindThingArchitecture` command should explicitly establish
a new authoring baseline from resolved resources, retain all four lighting
channels and support undo. It must not label a newly captured baseline as
recovered historical evidence. Moving/rotating a thing can preserve topology
while making world-space lighting stale; report the need for rebaking separately.
Binding validation itself does not calculate new lighting.

## Acceptance cases

Use original synthetic fixtures for a valid geoset zero with overflowing LOD1;
valid lower-LOD prefixes; changed model text under the same name; missing or
unsupported historical fingerprints; ambiguous archive entries; archive changes
between plan/apply and before output promotion; changed companion templates;
explicit rebind/undo; and failure paths preserving original arrays and producing
no output. Exercise the same validator through preview and export.

## Background engine comparison is separate work

The inspected engine supports automatic episode/map startup and a hidden
bootstrap window for `-headless`. Its `src/Win95/Window.c` subsequently frees
renderer resources and destroys the GL context/window in headless mode. That
mode cannot supply a rendered comparison frame. The timer-driven autoscreenshot
in `src/Main/jkGame.c` is guarded by `RDRASTER_SOFTWARE_RENDERER` and captures the
presented player view; it is not an explicit matched-camera capture interface.
Recognized `-fixed` and `-record` flags still have commented implementation bodies.

The narrow next engine feature is a hidden rendered capture mode that retains
its graphics context, suppresses window showing/focus, loads an explicit isolated
session, applies declared camera and visual settings, captures on the render
thread, writes an image and build/content/camera receipt without overwriting,
then exits. A full gameplay controller or MCP bridge is unnecessary for this
first comparison feature. Actual retail comparison may later need a brief retail
run if no private capture route works; that does not block the source work above.
