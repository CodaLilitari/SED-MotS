# Editor comparison and incorporation scope — 2026-11-01

## Upstream SED

The current `smlu/SED` develop branch is a substantial C++20/Qt rewrite, not a
small Pascal maintenance delta. The exact reviewed source is
`b82255998fb733c8e8c87104eb7c17f27b48376f`. Direct core adoption through a bounded
worker is the selected direction; see the
[architecture decision](CPP_ADOPTION_DECISION_20261101.md). The existing
consolidation also preserves Red's published histories and Lily's Linux/native
and reviewed-packaging ancestry. These ancestry merges do not by themselves
qualify new distributions.

## Holocron

The [Holocron site](https://jkjunkie.com/holocron/) and
[manual](https://jkjunkie.com/holocron/manual.html) were rechecked. Its Linux and
Windows ZIP links had newer October31 artifacts than the October29 comparison.
The Linux ZIP SHA-256 was
`bd390fcae38da6e757107dd001833f4c2250cb9e92a77463bd957f4de5743a87`;
the single ELF binary was11,764,224 bytes and contained version text
`0.2.1+72 (ff954111, modified)`. This was passive archive/string inspection.
The binary was not executed, and no source or code-reuse license was established.

Several older recommendations need current-source qualification. Dependency
packaging is already incorporated. Red's current branch adds true-color MAT
handling and expanded snapshot views, so the old MAT/view comparison is not a
current feature-gap inventory. The useful remaining ideas are:

- Asset and COG browsing that makes unresolved resources visible during edits.
- A lighting-resolution diagnostic that separates stale/manual baked corner
  values from actual interpolation error. Compare fresh interior samples with
  fresh corner interpolation for resolution error; compare fresh and stored
  corner values separately for stale-bake evidence.
- Focused geometry and save/test workflow comparisons against actual runtime
  observations, with undo and authored references preserved.

These are design candidates, not adopted Holocron code or validated performance
claims. `HOLOCRON_LIGHTS`, `HOLOCRON_COGS` and `HOLOCRON_AMBIENT` remain opaque
extensions. Retaining their source text does not authorize geometry rewrites
that might invalidate their references. Do not whitelist them without a grammar
and interoperability fixtures.

## Shred's in-game editor

The requested discovery found Don Robb/Shred's public demonstrations:

- [I turned Jedi Knight into a level editor for itself!](https://www.youtube.com/watch?v=qHbXt-m9vtg), October30.
- [Jedi Knight Level Editor Feature: Map View Editing](https://www.youtube.com/watch?v=GtjcATqvcUc), October31.
- [Don Robb's Jedi Knight projects](https://www.donrobbart.com/jedi-knight-projects.html).

The public video descriptions identify an in-game editing workflow and an
overhead map editing mode. Two paused frames in the first demonstration showed
a level-header editor and a first-person geometry overlay. Those observations
do not independently establish a complete editing transaction, save/reload
fidelity, multiplayer behavior or undo correctness. Transcript export was
unavailable; this was not an audio or full-video review.

No current editor source repository, downloadable source release or reusable
code license was found in the inspected public project/video links. Existing
older Max/Blender/UDK utilities on the project site are not evidence that the new
in-game editor source is available. No code was copied and no author was
contacted.

The useful product ideas are an explicit map/first-person editing transition,
clear geometry guides and a visible distinction between applying values to a
running world and saving level source. A running-engine edit must eventually
return through a reviewed, revision-bound source update; it cannot silently
become a second mutable authority beside either editor core. Source discovery
and executed interoperability are still prerequisites to any code integration.

## Compatibility cases

The [current regression record](LEGACY_CONSOLIDATION_20261101.md) covers nine JKL
documents across DerraIV, Cloud City, Caesar's Palace, Con Krape Canyon and the
DBZ tournament candidate, plus two real native JED projects. Original assets
remain outside the source and distribution. Parse/regeneration success is
separate from resource closure, visuals, game loading and multiplayer.
