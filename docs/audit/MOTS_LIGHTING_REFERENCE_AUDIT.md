# MotS lighting reference audit

Audit date: 2026-09-09. This is a bounded static comparison of downloaded editor sources and public engine sources. It records source behavior, not verified retail screenshots, executable equivalence, or a completed lighting implementation. Only this report was written by this research pass; no reference project was built or run, and no game assets were copied into the repository.

## Decisions supported by the evidence

1. **Keep MotS monochrome and RGB lighting independently.** Its four surface values are `(mono, red, green, blue)`, not RGBA and not a scalar brightness that must multiply the RGB triplet. Historical editor source lights also have separate scalar and RGB intensities.
2. **Treat architectural lighting as per-thing model data with mesh and vertex identities.** It is a different domain from world-surface corner lighting. The historical editors inspected here cannot establish its complete runtime behavior: JED/NJED write an empty table and disable thing bindings on export.
3. **Choose engine-specific rendering behavior deliberately.** Later OpenJones RGB/RGBA structures and falloff rules are useful comparators, but they are not MotS format definitions. OpenSith and Gorc are weaker MotS references because their inspected JKL representations are scalar-only.
4. **Retain source-light metadata separately from baked JKL data.** NJED preserves a richer light record in its native project than in the JKL editor-light section. A successful geometry export does not prove a reusable colored-light authoring project was preserved.
5. **Test color through every drawing path.** Surface clipping/interpolation, translucent surfaces, model meshes, extra light, dynamic light, palette lookup and output transfer functions can independently discard or alter color. A correctly parsed RGB array is only the first step.

These are implementation requirements derived from the inspected sources. Current SED implementation and executed validation belong in its implementation reports and test receipts, rather than being implied by this reference audit.

## Pinned sources and licensing evidence

Local source checkouts were read from the sibling `sed-editor-audit/sources` inventory. Their HEADs and clean working-tree status were rechecked. Remote revisions were resolved through GitHub and files read at immutable commit URLs. Public source availability is distinguished from an explicit license; no third-party code was transplanted during this audit.

| Reference | Pinned revision | Source and license finding |
| --- | --- | --- |
| [anov/JED](https://github.com/anov/JED/tree/8993c1dea4ba766a08cb5269d165b4c808f25824) | `8993c1dea4ba766a08cb5269d165b4c808f25824` | Published Delphi source. No repository-wide license file or general grant found in the inspected tree; `README.md` is a revision history. Historical behavior evidence, with no blanket reuse permission inferred. |
| [JKBoBoFett/NJED, Community branch](https://github.com/JKBoBoFett/NJED/tree/6fa7ac5be28638fda7495ff62fe6834d86600e39) | `6fa7ac5be28638fda7495ff62fe6834d86600e39` | Delphi source. No repository-wide license found. Dependency-specific notices are not a license for all new editor contributions. |
| [Ecki81/jediknight2blender](https://github.com/Ecki81/jediknight2blender/tree/88aed6f7182dad72d5042d2f70d5eef0b7ed6b78) | `88aed6f7182dad72d5042d2f70d5eef0b7ed6b78` | Blender JKL importer. [`__init__.py:1–12`](https://github.com/Ecki81/jediknight2blender/blob/88aed6f7182dad72d5042d2f70d5eef0b7ed6b78/__init__.py#L1-L12) states GPL version 3 or later. |
| [smlu/blender-sith](https://github.com/smlu/blender-sith/tree/ecc90131bcfdeabd400225e8a72ce94cbff1ec5c) | `ecc90131bcfdeabd400225e8a72ce94cbff1ec5c` | 3DO/KEY and MAT tooling. [`sith/blender_manifest.toml:15`](https://github.com/smlu/blender-sith/blob/ecc90131bcfdeabd400225e8a72ce94cbff1ec5c/sith/blender_manifest.toml#L15) explicitly identifies GPL-3.0-or-later; LICENSE is included. |
| [smlu/OpenJones3D](https://github.com/smlu/OpenJones3D/tree/a675b395a5d5cff238332d091368483b849e8b5b) | `a675b395a5d5cff238332d091368483b849e8b5b` | Public reverse-engineering implementation of the later Indiana Jones and the Infernal Machine engine. [`LICENSE:4–7`](https://github.com/smlu/OpenJones3D/blob/a675b395a5d5cff238332d091368483b849e8b5b/LICENSE#L4-L7) expressly identifies **GNU Affero GPL version 3** for OpenJones3D contributions. GitHub returned `NOASSERTION`, which must not be mistaken for absence of a license or ordinary GPL. |
| [r1sc/OpenSith](https://github.com/r1sc/OpenSith/tree/f600a9157f2c48aa37f3a25ae446014d746b6ab3) | `f600a9157f2c48aa37f3a25ae446014d746b6ab3` | Public Unity reimplementation. [`README.md:32–35`](https://github.com/r1sc/OpenSith/blob/f600a9157f2c48aa37f3a25ae446014d746b6ab3/README.md#L32-L35) specifies GPL version 3; LICENSE is included. |
| [jdmclark/jkgfxmod](https://github.com/jdmclark/jkgfxmod/tree/6612413810129ebeae2affeb956d0a5fdb41dd0e) | `6612413810129ebeae2affeb956d0a5fdb41dd0e` | Direct3D replacement renderer and shaders. [`LICENSE.md`](https://github.com/jdmclark/jkgfxmod/blob/6612413810129ebeae2affeb956d0a5fdb41dd0e/LICENSE.md) identifies Jonathan Clark and the MIT license. |
| [jdmclark/gorc](https://github.com/jdmclark/gorc/tree/a03d6a38ab7684860c418dd3d2e77cbe6a6d9fc8) | `a03d6a38ab7684860c418dd3d2e77cbe6a6d9fc8` | Public engine/tool code. [`LICENSE`](https://github.com/jdmclark/gorc/blob/a03d6a38ab7684860c418dd3d2e77cbe6a6d9fc8/LICENSE) is Apache 2.0. `game/restricted/README.txt` describes a resource-override directory; the directory name alone does not establish an additional license restriction. This audit does not classify every bundled resource. |
| [joelhays/go-jk, archived community specs](https://github.com/joelhays/go-jk/tree/2fea40a1d1716ef580b51621639522e24a907924) | `2fea40a1d1716ef580b51621639522e24a907924` | Its `_jkspecs/jkl.htm` pins a historical copy of the community JKL specification. No repository-wide license was found in the earlier source inventory; the copy is documentary evidence, not an official LucasArts SDK. |

The historical JED 0.951 and NED archives were also searched for architectural-lighting implementations. Both write empty tables. Their archive hashes and license findings remain in [the alternative editor source audit](alternative-editor-source-audit.md); they are not independent evidence of a working architectural-light baker. No official LucasArts SDK or leaked original engine source is claimed by this report.

## JED/NJED: exact authoring and export semantics

### Independent source-light channels

NJED native `SEC: LIGHTS` records contain XYZ, layer, scalar intensity, primary range, flags, RGB range, RGB intensity and RGB color. The native loader restores those fields. Only for JK does it then make RGB intensity equal scalar intensity and color white. See [`level_io.inc:802–807,1084–1095`](https://github.com/JKBoBoFett/NJED/blob/6fa7ac5be28638fda7495ff62fe6834d86600e39/level_io.inc#L802-L807).

The MotS inspector exposes RGB color and RGB intensity. Its RGB-range control and setter are commented out. This is evidence that RGB range should be preserved as legacy metadata, not proof that it controls the audited bake. See [`Item_edit.pas:841–845,910–911`](https://github.com/JKBoBoFett/NJED/blob/6fa7ac5be28638fda7495ff62fe6834d86600e39/Item_edit.pas#L841-L845).

### Static vertex bake

In [`lev_utils.pas`, `CalcLightingNew:6411–6580`](https://github.com/JKBoBoFett/NJED/blob/6fa7ac5be28638fda7495ff62fe6834d86600e39/lev_utils.pas#L6411-L6580), selected vertex channels are cleared; lights outside every sector are skipped. A light contributes only on the inward-facing side and within its **primary** range. Reachability recursively follows intersected adjoins, excluding `AF_BlockLight`; `LF_NoBlock` bypasses that visibility check. The constants are editor definitions in [`J_level.pas:20,30`](https://github.com/JKBoBoFett/NJED/blob/6fa7ac5be28638fda7495ff62fe6834d86600e39/J_level.pas#L20-L30), not evidence that the high adjoin bit has the same runtime meaning.

For distance `d` and primary range `r`, the contribution weight is `(1-d/r)^2`. Mono accumulates scalar intensity times that weight; each color accumulates RGB intensity times its corresponding color component times that weight. The commented normal-dot attenuation does not run. Thus this algorithm is not ordinary Lambert falloff and does not use RGB range.

`CalcSectorAmbients:6061–6088` uses the larger of mean nonnegative mono-corner intensity and mean surface extra light. It does not derive ambient from RGB. JED's [`LEV_UTILS.PAS:5240,5392–5395`](https://github.com/anov/JED/blob/8993c1dea4ba766a08cb5269d165b4c808f25824/LEV_UTILS.PAS#L5392-L5395) corroborates the same independent-channel bake. These are editor algorithms, not demonstrated engine rules.

### What survives JKL export

[`Savejkl.inc`, `WriteGeometry:600–601`](https://github.com/JKBoBoFett/NJED/blob/6fa7ac5be28638fda7495ff62fe6834d86600e39/Savejkl.inc#L600-L601) writes one scalar per JK corner and four values per MotS corner in the order mono/R/G/B. The data belongs to surface corners, so shared world positions must not imply shared lighting across a seam.

The same file's `WriteThings:768–792` writes `-1` as every MotS architectural-lighting binding. Its final exporter writes `Num ArchObjects 0` at 913–919. `WriteLights:794–807` emits only XYZ, scalar intensity and primary range, losing the native record's RGB source-light parameters. Therefore baked MotS surface colors can survive this output even while recolorable source-light metadata and model architectural lighting do not. JED's [`SAVEJKL.INC:783,914–915`](https://github.com/anov/JED/blob/8993c1dea4ba766a08cb5269d165b4c808f25824/SAVEJKL.INC#L914-L915) has the same empty-table behavior.

The older loaders' weak `count > vertexCount` format heuristic and manual vertex editor's scalar-only changes are documented in [A3 and A8 of the existing audit](alternative-editor-source-audit.md). Do not reproduce those limitations when adding richer light controls.

## ARCHLIGHTING: documentation, evidence and remaining questions

The [Massassi JKL specification](https://www.massassi.net/jkspecs/jkl.htm#s_arch) describes an arch-object index after a MotS thing's sector, with `-1` disabling it; a table then contains objects, meshes and per-vertex records. It identifies mesh numbers with 3DO meshes. However, its example labels the mono/R/G/B columns **“? - not checked.”** and explicitly questions the correspondence of mesh count to the model. This historical document is useful for structure but cannot alone settle rendering, ownership, LOD behavior or interpolation. Its pinned local counterpart is [`go-jk/_jkspecs/jkl.htm:610–643`](https://github.com/joelhays/go-jk/blob/2fea40a1d1716ef580b51621639522e24a907924/_jkspecs/jkl.htm#L610-L643).

The concurrent OpenJKDF2 engine audit uses baseline `4699f64ab5719a0efc4d31e2759b433308e85e98` and traces four independent arrays through `src/World/sithArchLighting.c:109–120` and thing/mesh binding through `src/Engine/sithRender.c:2448–2491`. These are the engine auditor's communicated source findings; implementation repairs and executable checks belong in that engine repository's separate `docs/MOTS_LIGHTING_AUDIT.md` report. That code trace is stronger evidence than the uncertain historical comment. The existing editor preservation work is recorded in [NET_MOTS_FORMAT_AUDIT.md](NET_MOTS_FORMAT_AUDIT.md): it owns architectural records and preserves thing bindings, and it already distinguishes preserving source data from safely changing a bound model.

**Implementation implications, requiring engine and model validation:** retain each record's object, mesh and vertex identities; validate a binding against the actual selected model and LOD layout; keep two things using the same model but different architectural records independent; use owned transient render buffers rather than modifying a cached shared model. Moving a thing or replacing its model can invalidate a static bake without changing the table's text syntax. An editor should report that consequence and provide explicit rebake behavior.

**Not established by this external-reference pass:** the retail engine's exact LOD fallback, whether mismatched mesh counts were tolerated, original authoring defaults, and parity of model lighting under every dynamic-light/face-mode combination. Those require the engine implementation trace and bounded executable comparisons. No new architectural-light baker is claimed here.

## Related-engine comparisons

### OpenJones3D: later Sith, different color layout

The source is public reverse engineering of Indiana Jones and the Infernal Machine, with explicit implementation stages described in its [README](https://github.com/smlu/OpenJones3D/blob/a675b395a5d5cff238332d091368483b849e8b5b/README.md). It is not an original MotS SDK.

In [`sithSurface_ReadSurfacesListText:607–814`](https://github.com/smlu/OpenJones3D/blob/a675b395a5d5cff238332d091368483b849e8b5b/Libs/sith/World/sithSurface.c#L607-L814), face extra light is **four RGBA values** (752–756), while each corner supplies **three RGB values** (802–809). The in-memory corner alpha comes from the face. The text writer mirrors the four-component face layout; CND serialization copies four-component corner values. Neither is the MotS interleaved mono/R/G/B record.

[`sithWorld_Startup:287–309`](https://github.com/smlu/OpenJones3D/blob/a675b395a5d5cff238332d091368483b849e8b5b/Libs/sith/World/sithWorld.c#L287-L309) registers its text section parsers without an ARCHLIGHTING parser. This bounded registration check and the inspected thing/surface structures do not provide an implementation of MotS architectural tables.

Two different lighting calculations also coexist:

- [`rdLight_CalcVertexIntensities:77–115`](https://github.com/smlu/OpenJones3D/blob/a675b395a5d5cff238332d091368483b849e8b5b/Libs/rdroid/Engine/rdLight.c#L77-L115) starts from existing vertex colors. It gates on distance and positive normal dot, adds a full color inside `maxRadius`, and linearly attenuates between `maxRadius` and `minRadius`. The field names must be interpreted from that control flow: in this function `minRadius` is the outer cutoff. Alpha is not recomputed by these RGB additions.
- [`sithRender_BuildDynamicLights:906–962`](https://github.com/smlu/OpenJones3D/blob/a675b395a5d5cff238332d091368483b849e8b5b/Libs/sith/Engine/sithRender.c#L906-L962) instead calculates world-vertex dynamic colors, using distance times camera attenuation, RGB subtraction/addition and a clamp after each light. It does not use the previous function's normal-dot rule. The same render file combines face and sector extra light for world polygons at 768–779 and 1205–1209; the source itself questions differences in ambient treatment between ordinary and alpha surfaces.

These paths are useful for detecting accidental channel loss and for designing explicit renderer stages. Their differing rules are evidence against treating one generic colored-light function as a proven replacement for every JK/MotS path. OpenJones lighting-mode numeric values also need their own enum and branch interpretation; matching integer values alone does not establish matching semantics.

### Blender references: distinguish JKL colors from 3DO colors

[`jediknight2blender/import_jkl.py:324–336`](https://github.com/Ecki81/jediknight2blender/blob/88aed6f7182dad72d5042d2f70d5eef0b7ed6b78/import_jkl.py#L324-L336) consumes four values per MotS corner. At 592–605 it uses positions 1–3 as Blender RGB and sets alpha to 1, rather than treating mono as alpha. Its importer adds surface and sector extra light, but this is a visualization choice, not retail verification; the local `sector_ambient` read is not part of that corner-color expression. The inspected project is an importer, not a round-trip MotS lighting editor.

In [`blender-sith/model3doLoader.py:141–158,185–201`](https://github.com/smlu/blender-sith/blob/ecc90131bcfdeabd400225e8a72ce94cbff1ec5c/sith/model/model3doLoader.py#L141-L158), 3DO version 2.1 converts scalar vertex intensity to equal RGB with alpha 1; 2.2 reads RGB; 2.3 reads RGBA. Face extra-light color follows the same version distinction. Those are **3DO version rules**, not a reason to reinterpret MotS JKL's four values as RGBA. MAT/CMP palette decoding in this project is likewise separate from baked vertex lighting.

### OpenSith: scalar JKL visualization, not a MotS RGB authority

[`JKLParser.cs:311–347`](https://github.com/r1sc/OpenSith/blob/f600a9157f2c48aa37f3a25ae446014d746b6ab3/Assets/Scripts/JKL/JKLParser.cs#L311-L347) allocates one float per surface corner and reads the next N numeric tokens, with no four-channel MotS branch. Given interleaved MotS input, these reads cannot represent all four channels correctly. [`Sith.cs:177–196`](https://github.com/r1sc/OpenSith/blob/f600a9157f2c48aa37f3a25ae446014d746b6ab3/Assets/Scripts/Sith.cs#L177-L196) clamps those floats and duplicates them into grayscale mesh colors; the inspected path also skips adjoined surfaces. The README explicitly reports no COG parser/VM and no playable game. It can illustrate a Unity import workflow, but is not a colored-light or gameplay fidelity oracle.

### Gorc: useful staged rendering, scalar format representation

[`level_loader.cpp`, `ParseGeoresourceSection:213–240`](https://github.com/jdmclark/gorc/blob/a03d6a38ab7684860c418dd3d2e77cbe6a6d9fc8/src/libs/libold/content/loaders/level_loader.cpp#L213-L240) reads one float per corner. [`level_surface.hpp:29–30`](https://github.com/jdmclark/gorc/blob/a03d6a38ab7684860c418dd3d2e77cbe6a6d9fc8/src/libs/libold/content/assets/level_surface.hpp#L29-L30) stores scalar extra light and `(vertex, texvertex, intensity)` tuples. These inspected structures cannot preserve MotS's four independent values.

[`level_view::draw_surface:413–480`](https://github.com/jdmclark/gorc/blob/a03d6a38ab7684860c418dd3d2e77cbe6a6d9fc8/src/client/world/level_view.cpp#L413-L480) combines corner intensity with surface and sector extra light, then applies a colormap tint. Separate dynamic-light passes use [`light.frag`](https://github.com/jdmclark/gorc/blob/a03d6a38ab7684860c418dd3d2e77cbe6a6d9fc8/game/restricted/misc/glsl/light.frag), including white light and a specular term. This provides a stage-separation reference, not evidence that its shading matches original MotS. The `restricted` folder's [README](https://github.com/jdmclark/gorc/blob/a03d6a38ab7684860c418dd3d2e77cbe6a6d9fc8/game/restricted/README.txt) calls it a resource-override directory.

### jkgfxmod: downstream rendering and transfer-function evidence

[`renderer.cpp:1159–1161,1243–1268`](https://github.com/jdmclark/jkgfxmod/blob/6612413810129ebeae2affeb956d0a5fdb41dd0e/renderer/renderer.cpp#L1243-L1268) reads already transformed `D3DTLVERTEX` data supplied by the game. It extracts packed RGBA, premultiplies RGB by alpha, and calls `gamma18_to_linear`. The [vertex shader](https://github.com/jdmclark/jkgfxmod/blob/6612413810129ebeae2affeb956d0a5fdb41dd0e/assets/shaders/game.vert) passes that color onward. The [opaque shader at 106–109](https://github.com/jdmclark/jkgfxmod/blob/6612413810129ebeae2affeb956d0a5fdb41dd0e/assets/shaders/game_opaque_pass.frag#L106-L109) optionally quantizes interpolated vertex color in 1/64 steps, then multiplies it with albedo. The [translucent shader at 95–104](https://github.com/jdmclark/jkgfxmod/blob/6612413810129ebeae2affeb956d0a5fdb41dd0e/assets/shaders/game_trns_pass.frag#L95-L104) has its corresponding color path.

Thus this renderer is useful for checking interpolation, transparency and intentional visual enhancements. It cannot explain how the original JKL mono/RGB or architectural arrays became the supplied D3D colors. Gamma conversion, material enhancements and quantization settings must be recorded when using its output as a visual comparison.

## Concrete regression and design targets

These targets follow from the comparison and should be adopted only with the intended compatibility profile made explicit:

| Case | What it distinguishes |
| --- | --- |
| One corner with mono `0.2` and RGB `(0.8,0.1,0.4)` | Independent mono/RGB selection versus accidental grayscale, RGBA interpretation, or mono-times-RGB multiplication. |
| Adjacent surfaces sharing positions but with different corner colors | Per-corner storage versus accidental world-vertex deduplication of baked light. |
| Surface extra light, sector extra light and ambient varied independently | Different source fields versus one catch-all brightness slider; surface/model/alpha paths may have different engine rules. |
| Explicit light/geometry modes on ordinary, sky, masked and translucent faces | Mode/flag dispatch versus applying Gouraud color everywhere or losing RGB on an alpha path. |
| Source light with distinct scalar/RGB intensity and nondefault legacy RGB range | Native-project preservation, documented bake policy and JKL metadata losses. |
| Point light across an open, blocked and closed portal | Occlusion behavior and selected bake policy; zero-distance and range-boundary cases should stay finite. |
| Two things sharing one model but different architectural records | Per-instance binding and buffer ownership, with no cached-model mutation leaking between things. |
| Multiple meshes/LODs with distinct corner values and deliberately mismatched counts | Explicit model topology validation and safe handling of unsupported bindings. |
| Move/rotate a bound thing, replace its model, then undo | Authored-data preservation, stale-bake reporting and reversible changes. |
| RGB through world clipping, model clipping and deferred translucency | End-to-end propagation rather than parser-only success. |
| Palette material and direct-color material under fixed camera/exposure | Texture decoding, output transfer functions and lighting evaluated separately. |

Agent operations should expose the same distinct quantities as the human inspector, report the selected lighting policy, bind reads and previews to a content hash, and return which corners/things/bakes will change before mutation. Bake results should include skipped lights, missing models, invalid architectural bindings and changed counts, with one coherent undoable application. Preserve manual lighting unless an explicit bake operation is requested. A saved JKL, a headless diagnostic image and an engine gameplay comparison are three different evidence levels.

## Scope and unresolved work

This pass manually followed the specific files and functions above. Full-tree searches located ARCHLIGHTING occurrences in the downloaded editor inventory; that does not mean every editor line or every public engine repository was audited. The licensing descriptions report included notices and discovery limits, not a complete dependency or legal review. Publicly readable proprietary game data, archived executable downloads and repository source code remain separate provenance classes.

The implementation still needs evidence from the chosen OpenJKDF2 branch and, for retail claims, controlled comparison against the actual retail MotS engine. The audit does not establish that the modern editor already implements every listed capability or that any comparator is visually identical to retail.
