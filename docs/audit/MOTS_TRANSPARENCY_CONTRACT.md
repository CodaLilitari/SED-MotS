# MotS transparency and depth contract

Source audit, 2026-09-12. Engine checkout: `../openmots`, commit `5fa4644d03080fef69d7137eaa2c8f295c47bd63`. Delphi reference: `../SED-MotS`, commit `8a3d89b70fc7d31eae5ec978ad1c7f336919f73e`. Paths and line numbers below refer to those commits, not an assumed upstream or retail implementation. No executable was launched for this audit. Proposed GPU cases below are specifications, not completed test results.

## Correction to the previous preview contract

The previous statement that accelerated OpenJK translucent faces have **90/255 opacity is superseded**. The engine stores vertex alpha 90/255, but its selected fragment-shader branch inverts that alpha. For ordinary indexed textures, with default albedo factors and no additional color effects, the effective albedo composition is:

```text
a = 90 / 255
shader source RGB = surface RGB * (1 - a)
shader source A   = 1 - a
GL blend factors = ONE, ONE_MINUS_SRC_ALPHA
framebuffer RGB  = surface RGB * 165/255 + previous RGB * 90/255
```

The effective source opacity is therefore **165/255**. A Vulkan shader producing straight RGB can achieve the same RGB result with alpha 165/255 and `SRC_ALPHA, ONE_MINUS_SRC_ALPHA`. Do not premultiply twice. Framebuffer alpha requires the corresponding separate alpha factors if it is to match as well.

Evidence chain at the pinned engine commit:

| Source | Evidence |
|---|---|
| `src/Raster/rdCache.c:410-419` | Face flag `0x2` assigns vertex alpha 90 and triangle flag `0x200`; other faces receive alpha 255. |
| `src/Platform/GL/std3D.c:2737-2750` | Triangle flag `0x200` selects `D3DBLEND_INVSRCALPHA`, with `glBlendFunc(GL_ONE, GL_ONE_MINUS_SRC_ALPHA)`. |
| `resource/shaders/default_f.glsl:341-355` | Combines sampled and vertex colors, then multiplies RGB by one minus alpha and inverts alpha. |
| `resource/shaders/default_f.glsl:373` | Writes that result to the albedo color attachment. Emissive output is separate. |
| `src/Platform/GL/std3D.c:559`, `3187-3190` | Ordinary palette is RGB8, so sampled alpha is one; ordinary texture albedo factors are initialized to one. |

This is the accelerated albedo-pass contract. Gamma, emissive/bloom, SSAO, true-color replacements, and retail software CMP transparency tables are separate behavior. A CMP lighting comparison profile does not establish software-transparency parity.

## Indexed MAT masking

`src/types.h:1246-1254` defines the 24-byte texture-data header as width, height, `alpha_en`, `unk_0c`, `unk_10`, mipmap count. Thus `alpha_en` is at texture-data offset **+8**, and the stored transparent color is at **+16**. `src/Engine/rdMaterial.c:266-275` loads those fields, and `323-324` applies the color key to the display buffer when `alpha_en & 1` is set.

For the accelerated **8-bit indexed** path, the shader uses fixed palette index **zero**, not the stored +16 color value. Preserve the latter as decoded metadata; do not reinterpret it as the accelerated indexed discard value. `src/Raster/rdCache.c:578-602` adds triangle mask flag `0x400` for an ordinary MAT with `alpha_en & 1`. `src/Platform/GL/std3D.c:2737-2745` selects an alpha blend mode for either `0x400` or face translucency `0x200`. `resource/shaders/default_f.glsl:281-310` discards index zero in either alpha mode, before palette-light lookup.

| Material and face | Index zero | Nonzero index |
|---|---|---|
| Textured, MAT mask clear, face opaque | Visible `palette[0]` | Opaque lit palette color |
| Textured, MAT mask set, face opaque | Discard: no color or depth write | Opaque lit palette color |
| Textured, MAT mask clear, face translucent | Discard | 165/255 effective source opacity |
| Textured, MAT mask set, face translucent | Discard | 165/255 effective source opacity |
| Solid-color MAT, color index zero | Visible; no texture-index discard | Face translucency still applies |

Masking is distinct from face translucency. An opaque cutout does not make its nonzero texels partly transparent. The same MAT used by opaque and translucent faces needs independent per-draw behavior. Missing-resource diagnostic textures should not inherit a guessed mask.

## Depth and draw ordering

The current engine declares face `0x20` as `RD_FF_ZWRITE_DISABLED` (`src/Raster/rdFace.h:23`), but the inspected accelerated draw path does not consume it. A repository source search finds the declaration only. **No `ZTestDisabled` face flag was found**; `0x80` is still `RD_FF_UNKNOWN_80` (`rdFace.h:25`). Do not assign it a new meaning.

Engine depth state comes from `rdroid_curZBufferMethod`: `src/Raster/rdCache.c:328-342` maps its four cases to triangle bits `0x800` (test) and `0x1000` (write). `src/Platform/GL/std3D.c:2729-2759` maps those to `GL_LESS` versus `GL_ALWAYS`, and depth mask true versus false.

| Geometry in current accelerated engine | Depth test | Depth write | Source |
|---|---|---|---|
| Normal world surface, including a non-adjoined translucent surface | Less | Yes | `src/Engine/sithRender.c:1805-1808`, `1903-1909` |
| Thing/model face, including a translucent model face | Less | Yes | `src/Engine/sithRender.c:2742-2744` |
| Adjoined world surface with a material and either face translucency or MAT masking | Less | No | Deferred at `src/Engine/sithRender.c:1903-1909`; SDL alpha pass sets read/no-write at `3097-3104` |

Consequently, a blanket "all translucent faces disable depth writes" rule is not an exact engine contract. An opaque masked adjoin also enters the deferred no-write pass. Track adjoin identity independently from translucency and material masking if reproducing this routing.

The deferred pass sets sorting method 2 (`sithRender.c:3104`). `rdCache_Flush` sorts faces when that method is selected (`src/Raster/rdCache.c:128-132`), and its comparator sorts descending **minimum face depth**, not material or Euclidean centroid distance (`1474-1482`). Read/write batches can subsequently be regrouped by texture (`1402-1406`, `1440-1453`). A global camera-dependent transparent sort in an editor can improve presentation, but it should be described as an editor rendering choice if it also reorders geometry outside the engine's deferred adjoin pass. Whole-triangle or whole-face sorting cannot solve intersecting translucent geometry in general.

Delphi does honor authored `FF_ZWriteDisabled`: `src/PRenderGL.pas:283-286` calls `glDepthMask`, and `src/PRenderDX.pas:707-709` sets `D3DRENDERSTATE_ZWRITEENABLE`. Supporting that flag in C# restores an editor feature while differing from the inspected OpenJK implementation. Keep that distinction explicit unless the engine is also changed.

## Sky depth

The engine does not simply disable all sky depth writes:

- Horizon sky sets the already projected vertex depth to the camera far plane (`src/Engine/sithRenderSky.c:67`). It also uses affine UVs and full lighting (`44-46`), with view orientation, horizon distance, focal length and authored offsets (`28-33`, `68-75`).
- Ceiling sky intersects the camera ray against the sky plane (falling back to distance 1000), transforms that intersection into camera space, and uses its camera-space Y as projected depth (`sithRenderSky.c:111-150`). It retains ordinary pass depth state. The surface face flags are subsequently copied to the processed face (`src/Engine/sithRender.c:2199-2215`).
- Delphi instead sets `FF_ZWriteDisabled` on its temporary ceiling-sky polygon (`src/PRENDER.PAS:2023-2029`). Its adjacent comment says "Disable depth testing," but the invoked state is **depth writing**, not testing. This is an editor workaround, not evidence for a new engine face flag or exact sky parity.

Porting sky requires screen coverage and depth together. Merely removing depth writes can leave a nearer sky polygon incorrectly occluding later deferred geometry through color, or render it over previously drawn geometry if testing is disabled. Correct sky projection remains a separate implementation and comparison task.

## Original GPU regression cases

Use synthetic palettes, MAT headers and overlapping planes; no retail asset is needed for these tests. Expected RGB values below assume a linear UNORM target, fullbright input, no postprocessing, and a one-unit rounding tolerance.

1. **Index-zero matrix:** Give palette[0] a bright, nonblack color, place a textured checker over a blue plane, and run all four textured rows in the masking table. Test both RGB and mono profiles; source index zero must be tested before lighting remapping.
2. **Effective opacity:** Fullbright red over opaque blue should produce `(165, 0, 90)`, not `(90, 0, 165)`. Reverse colors and use a nonblack background to detect wrong premultiplication and blend factors. Solid index zero must obey the same opacity while remaining visible.
3. **Mask does not imply translucency:** A masked opaque face's nonzero green texel must remain `(0, 255, 0)`. The hole must reveal the rear plane, and a later plane between the two must remain visible only through the hole when depth writes are enabled.
4. **Stored key is not indexed GL key:** Set texture-data +16 to a nonzero palette index, set mask bit +8, and include both that index and zero. In the OpenJK indexed profile, zero disappears and the nonzero stored-key texel stays visible. Also test alpha flags `0`, `1`, `2`, `3` to establish the low-bit contract.
5. **Draw state independence:** Reuse one MAT across opaque unmasked, translucent, and depth-write-disabled draws; put a different MAT between them. A cache or pipeline transition must not leak mask, alpha, or depth state.
6. **Depth writes:** Use a red near triangle drawn before a green farther triangle. With write enabled the near triangle remains; with write disabled the later farther triangle passes. Retain depth testing against an even nearer opaque occluder in both cases. Repeat for opaque and translucent pipelines.
7. **Deferred routing:** Distinguish opaque cutout adjoins, translucent adjoins, non-adjoined translucent world faces, and translucent model faces. Assert assembled draw state as well as image results; otherwise a renderer-only probe cannot prove adjoin metadata reached it.
8. **Camera-dependent ordering:** Render two separated translucent planes with different materials from opposite sides; expected compositions must reverse with depth. Add equal-depth ties and a scene/geometry update to detect unstable or stale order. Treat crossing planes as a documented sort limitation.
9. **Sky versus foreground:** Cover a foreground model with a sky polygon in source geometry, put a deferred transparent surface between them, and vary view pitch. Verify the chosen sky depth policy without claiming engine equivalence until the sky projection itself is ported.

The audit establishes source behavior and testable expectations. It does not establish retail framebuffer equivalence, true-color MAT behavior, or completion of the proposed tests.
