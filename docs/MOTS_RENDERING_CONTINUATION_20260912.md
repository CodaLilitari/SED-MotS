# MotS renderer continuation — 2026-09-12

This follows the September 9 development package. OpenJKDF2 remains the primary
target. Engine source was rechecked at `5fa4644d03080fef69d7137eaa2c8f295c47bd63`;
this editor work does not change that newer engine commit.

This is the transparency/preservation checkpoint. The subsequent [resource-aware ARCH implementation](ARCH_RESOURCE_VALIDATION_DESIGN.md) adds all-LOD validation, reviewed resource revisions and guarded agent/GUI export; its validation totals supersede the numbers below. Hidden engine capture is separately versioned in the engine repository.

## Implemented

- Corrected effective accelerated translucency from 90/255 to **165/255 opacity**.
  The engine stores 90/255, then inverts it in its accelerated shader path.
  Correct source-over alpha also keeps an already-composited opaque snapshot
  opaque when displayed by another application.
- Added MAT cutout metadata through decoding, library caches, GUI, command-line
  snapshots and the standalone render tools. Masked/translucent indexed textures
  discard source index zero before lighting lookup; ordinary unmasked texture
  zero and solid material zero remain visible.
- Followed the first cel's actual texture ID, with bounded traversal of earlier
  textures and mip chains. Pixels and mask state come from the selected texture.
- Split material batches by adjoin identity and selected blend/depth-write states
  independently. Masked/translucent adjoined surfaces use no-depth-write draws;
  ordinary translucent surfaces/models retain depth writes.
- Sorted deferred triangles by minimum forward camera depth on every render,
  allowing faces sharing one MAT to interleave with other materials. CPU sorting
  and matrix validation finish before Vulkan command recording starts.
- Preserved 3DO hierarchy flags/type, case-sensitive node names and pivots through
  edits and writing in both 2.1/2.2. Hierarchy numeric output retains small values;
  incomplete rows and names that would corrupt the format are rejected.

The [source contract](audit/MOTS_TRANSPARENCY_CONTRACT.md) records the evidence,
including the correction to the earlier opacity claim. These changes use the
shared editor/agent implementation. No new service, MCP wrapper, Delphi install
or GUI automation is needed to use them.

## Validation

The combined .NET suite passed **714 tests**. The original lighting GPU probe
passed **160 assertions**, and the new transparency probe passed **190 assertions
across 40 frames** on an NVIDIA RTX 5070 Ti. New cases include independent
framebuffer composition, opposite cameras, material interleaving, source-index
masking, opaque holes/depth, all blend/depth pipeline states, stable ties and
successful renderer reuse after rejecting a nonfinite matrix.

All four shaders were compiled and validated with `spirv-val`. Receipts, images
and build logs are retained under ignored `build` output directories. The package
workflow reruns the full suite and the real self-contained agent workflows.
Neither a GUI nor a game was launched for these tests.

## Remaining work and user dependencies

No additional user input is needed for resource-aware ARCH validation or the
remaining source implementation. The [ARCH validation design](ARCH_RESOURCE_VALIDATION_DESIGN.md)
specifies explicit archive inputs, distinct resource revisions, all-LOD bounds,
shared preview/export policy, and honest treatment of absent historical hashes.
That design is **not implemented** by this renderer change.

Accurate sky projection/depth, dynamic/runtime lights, animation, fog,
emissive/gamma behavior, mixed solid/textured cel tables and true-color MATs
remain separate work. The engine ignores face flag `0x20`, while Delphi honors
it; no unsupported depth-test flag or equivalence claim was introduced here.
Preserved model pivots are not yet applied in the static renderer. Child/sibling
links are regenerated from parent relationships; arbitrary authored sibling
ordering and all lower-LOD geometry remain outside the model writer's guarantee.

Triangle ordering uses unclipped minima, while the engine sorts clipped faces.
Intersecting geometry and frustum-edge cases may differ. Large transparent scenes
also need an interactive performance pass: the present implementation creates
per-triangle deferred draw ranges and sorts them each frame.

Matched-camera comparison needs a hidden engine capture mode that retains its GL
context; current `-headless` destroys that context. A short retail reference run
may eventually need the user's screen, depending on the available capture route.
No screen takeover or reference-session approval is needed for the code work
above, and static/synthetic tests do not establish full retail parity.
