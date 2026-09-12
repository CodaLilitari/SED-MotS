# Renderer lighting and shader verification

The mesh renderer carries four independent lighting components: monochrome plus red, green, and blue. `MeshVertex` retains its previous position, normal, RGB, and UV offsets; the monochrome float is appended at byte 44, giving a 48-byte stride. `RenderScene.LightingMode` selects which baked channels the shader uses. This does not change saved JKL lighting data.

- For textured materials in the OpenJK preview profile (`UsePaletteLightRamp = false`), `Mono` multiplies the base palette RGB by the scalar light, following the accelerated engine's main albedo path. `UsePaletteLightRamp = true` retains an explicit CMP comparison profile: it selects row `floor(63 * clamp(mono))` of the sector CMP's 64-row light table, then resolves the resulting palette index. Bare `RenderScene` instances default to that earlier tool profile; level scene assembly selects the OpenJK profile.
- For textured Gouraud materials, `Rgb` resolves the unshaded palette index using the sector CMP, then multiplies the color by the three independently clamped RGB components. The monochrome component does not multiply RGB.
- Solid-color MATs carry `IndexedTexture.IsSolid`. Gouraud RGB uses each channel of the CMP row-zero palette baseline plus `255 * channel - 0.5`, clamped and truncated to a byte. This conversion occurs per vertex before interpolation. Solid monochrome also resolves the CMP per vertex before interpolation; unlit/diffuse solid faces use monochrome even in RGB display mode. Solid palette index zero remains a visible color.
- `Submesh.LightMode` carries the effective face/mesh light mode. Mode zero bypasses the ramp and uses the original palette entry, even when the top ramp row is not identity.
- `Submesh.UseMonochromeLighting` selects the ordinary-model scalar draw even within a MotS RGB scene. A world/model pair can share its material and CMP descriptors while retaining independent per-draw channel selection. This override does not change the stored alternate RGB values.
- Flat marker and selection drawing keeps its existing direct vertex-color path. Sky and the editor's full-bright override use full light in both modes.

Scene assembly is responsible for supplying the correct surface/model channels, ambient and extra-light terms, light modes, and validated architectural-light bindings. The GPU stage does not infer those inputs. These static preview paths do not by themselves establish gameplay lighting parity, dynamic-light behavior, transparency-table fidelity, or support for every engine rendering backend.

Textured RGB currently interpolates floating-point light. The inspected accelerated engine truncates `255 * light` to vertex bytes after CPU clipping and applies optional tint/effects to those bytes; reproducing only the quantization without the same clipping order would still differ at generated clip corners. That quantization/clipping behavior and the engine's separate emissive-luminance path remain explicit limits of this preview. Solid-material vertex conversion is tested with gradients, but this is not a claim that the complete clipping pipeline is pixel-identical to the engine.

`Submesh.Colormap` selects a CMP independently of its material. The renderer shares a material's indexed texture across CMPs and uses a separate descriptor set for each material/CMP pair. `SetScene` accepts a `ColormapLookup` alongside the material resolver. A missing named CMP produces a diagnostic and uses the configured default palette/ramp; a malformed palette or light-table length is rejected. Callers should display `SceneRenderer.Diagnostics` after loading. `UpdateGeometry` reloads resources when an edit introduces a new material/CMP pair.

The accelerated engine converts its stored 90/255 alpha into an effective **165/255 opacity**. The preview now uses that effective opacity, retaining accumulated destination alpha. This corrects the earlier checkpoint's direct use of 90/255; see the [source contract](audit/MOTS_TRANSPARENCY_CONTRACT.md). MAT masking comes from the selected texture's `alpha_en & 1`. A masked or translucent indexed texture discards source index zero before CMP mapping; opaque unmasked index zero and solid color zero stay visible. The first cel's texture ID is respected, including preceding mip chains. Mixed solid/textured cel tables and true-color MATs remain outside this decoder.

Adjoined masked/translucent world surfaces disable depth writes; ordinary translucent surfaces/models retain them. Blend and depth-write pipeline states are independent. Deferred triangles are ordered on every frame by minimum forward camera depth, allowing separate faces sharing a material to interleave. This is editor triangle sorting: the engine sorts clipped faces, and exact ordering at frustum boundaries or intersections is not established. Large-scene interactive performance also needs measurement; this path currently creates per-triangle deferred draws. The engine ignores authored face flag `0x20`, so this preview does not claim the Delphi editor's depth-write override is implemented. Sky projection/depth remains separate work.

The final push word at byte 156 packs RGB selection in bit zero, solid-material selection in bit one, OpenJK textured scalar modulation in bit two, MAT masking in bit three, and effective light mode in bits 8–15. The complete push range stays 160 bytes. Palette and light-ramp descriptors are visible to both shader stages so solid lighting can resolve before raster interpolation.

Both GLSL sources and compiled SPIR-V files are checked in. A normal build verifies their SHA-256 values against `Shaders/ShaderHashes.props`, so editing GLSL without refreshing the compiled binaries fails the build. No shader compiler is required just to build an unchanged checkout. To regenerate with installed tools:

```powershell
./tools/Compile-Shaders.ps1 -Compiler glslangValidator -Validator spirv-val
```

An existing WSL distribution can run the tools instead:

```powershell
./tools/Compile-Shaders.ps1 -WslDistribution Ubuntu `
  -Compiler /absolute/linux/path/glslangValidator `
  -Validator /absolute/linux/path/spirv-val
```

The script targets Vulkan 1.0, compiles all four shaders to a fresh directory under `build`, and runs `spirv-val` before replacing any committed binary. It then regenerates the hash file and writes a receipt containing tool versions and source/binary hashes. `-Check` repeats compilation/validation and requires byte-identical committed SPIR-V without updating it. Compiler versions can change output bytes; review intentional compiler updates together with regenerated binaries and receipts.

`RendererLightingLayoutTests` checks the actual packed vertex bytes, packed draw state, and strict CMP sizes. `dotnet/tools/Sed.LightingProbe` exercises the real Vulkan mesh pipeline offscreen with original synthetic textures and geometry. It checks independent mono/RGB behavior, channel clamping, full-bright and flat drawing, simultaneous CMPs on one material, CMP-only edits, missing-CMP diagnostics, and rejected palette updates. It also contrasts OpenJK scalar modulation with CMP comparison, updates the profile without reloading a material, and draws ordinary scalar models beside RGB world geometry using the same MAT/CMP. Solid-material cases include distinct baseline channels, row truncation, nonidentity fullbright ramp bypass, index-zero visibility, hardware alpha, and gradients that distinguish per-vertex conversion from per-fragment conversion. It requires a new output directory, saves pixels and a receipt, and never launches the editor UI or game.

`dotnet/tools/Sed.TransparencyProbe` adds independent framebuffer-composition expectations, opposite cameras, interleaved same-material geometry, the zero-index masking matrix, solid-zero preservation, opaque cutout depth, all blend/depth pipeline choices, stable ties, and shared-material state updates. It also runs without a window or game assets and saves its frames and receipt to a new directory.
