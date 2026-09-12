# MotS lighting implementation checkpoint — 2026-09-09

Historical checkpoint. The [September 12 continuation](MOTS_RENDERING_CONTINUATION_20260912.md) supersedes its transparency-opacity assumption and closes additional cutout, ordering, depth-routing and 3DO preservation gaps. The validation numbers below belong to the September 9 build.

This checkpoint upgrades the shared C# editor/agent renderer and includes a
separate local OpenJKDF2 engine repair. It is a development build with static
lighting evidence, not a claim of complete retail visual or gameplay parity.

## Editor and headless agent

- Independent world-corner mono/RGB channels survive scene assembly; newly
  authored RGB appears before saving, without depending on an engine load-time
  surface flag. Per-sector CMPs, face extra light, model ambient floors, mesh/face
  light modes, and scalar 3DO base lighting feed the preview.
- Level-geometry model instances resolve inherited `thingflags 0x8` and compatible
  architectural lighting, including index zero. Ordinary model instances retain
  monochrome lighting even in an RGB world. Historical normalized native model
  fingerprints are checked when present; this check applies to preview only.
- The default OpenJK textured monochrome profile modulates base palette color by
  scalar light. A separate `palette` comparison profile uses the CMP light table.
  Solid materials use their distinct per-vertex mono/RGB conversion rules.
- `sed-agent snapshot` renders textured, lit PNGs without opening the editor or
  game. Requests fix the source revision, camera, lighting/profile choice, and
  explicitly supplied archive paths/hashes. Results record used entry hashes,
  omissions, warnings and limitations. Strict asset errors prevent PNG promotion.
- Bounded MAT/3DO decoding rejects malformed or truncated resources; Vulkan
  submission/readback errors are checked. These checks do not make the driver
  cancellable: callers still need a process timeout.
- The Delphi history audit additionally yielded six-field legacy JED editor-light
  import, JK/MotS texture wrapping, GUI horizon-offset plumbing and native model
  fingerprint verification. See [the commit audit](audit/DELPHI_DOTNET_DELTA_AUDIT.md).

The versioned CLI and repository skill provide the direct Codex interface. No
MCP server is required for these local design and rendering operations. A future
MCP adapter can expose the same domain commands without duplicating editing
semantics or driving the GUI.

## Validation

- Full Windows Release solution build: zero warnings/errors; **677 tests passed**.
- Real Vulkan renderer probe: **160 independent pixel assertions passed** on an
  NVIDIA RTX 5070 Ti, covering separate mono/RGB, OpenJK/CMP profiles, shared
  material with distinct CMPs, solid-material gradients, clamping and fullbright.
- End-to-end lit snapshot script: **93 assertions across 19 agent calls passed**,
  including missing/malformed assets and existing-output refusal. The package
  workflow reruns this against the actual self-contained executable.
- All four shaders compiled and passed `spirv-val`; independent recompilation
  reproduced the checked SPIR-V. The normal build verifies source/binary hashes.
- Original owned MotS Rebel Base assets rendered offscreen: 701 sectors, 4,738
  visible-mode surfaces and 44,384 triangles. Geometry-mode-zero portal surfaces
  and 183 things without resolved models were explicitly omitted. The preview
  was visually inspected. This is an original-asset loading/rendering check, not
  a matched retail screenshot comparison. No retail assets enter the package.

Build, probe and package tools retain JSON/TRX/log receipts in ignored output
directories. GUI interaction and gameplay were not exercised for this checkpoint.

## Coordinated engine repair

The separate local engine commit
`9510c471` fixes RGB deferred-alpha world rendering, solid RGB channel/scaling
errors, architectural pointer ownership/restoration and LOD bounds, the placed
ARCH index overwritten by template copying, per-vertex base iteration, doubled
model RGB base lighting, and a 64-bit clipped-solid pointer issue.

The engine Windows x64 build passed. Its production-function harness passed
**1,185 assertions** with AddressSanitizer, UndefinedBehaviorSanitizer and leak
detection; seven old-behavior negative controls failed as intended. Explicit
fixtures/stubs remain in the harness, so it is not a full gameplay test. Detailed
source and executable receipts reside in that engine checkout. The engine commit
and binary remain local at this checkpoint.

## Remaining work

The next rendering work is camera-dependent transparent polygon sorting, opaque
cutout metadata, authored/sky depth-write state, accurate sky projection, dynamic
lights and runtime modes, and matched-camera comparison with both the patched
engine and retail. CPU clipping/vertex quantization, tint/emissive effects, fog,
CMP transparency tables, animations and lower model LODs are not fully reproduced.

Resource-aware ARCH validation must also reach authoring/apply/export and all
LODs. Native source-light mono and RGB intensities still need independent rebake
support. Existing hierarchy-node name/type/pivot round-trip gaps remain separate
format work. These limits are not closed by a successful static PNG or by the
historical format-preservation corpus.

See [renderer semantics](RENDERER_LIGHTING.md), the
[agent protocol](AGENT_PROTOCOL.md), and the
[other-editor/engine source audit](audit/MOTS_LIGHTING_REFERENCE_AUDIT.md).
