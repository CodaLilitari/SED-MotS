# Local agent protocol, version 1

`agent/sed-agent.exe` is a local JSON command-line interface. It does not start a GUI, shell, game or network service. Only `snapshot` creates an offscreen Vulkan graphics device. Editing uses the same model and Core edit commands as the editor; material-table registration also updates the format document.

Supported outcomes are JSON on stdout. Exit codes: 0 success, 2 invalid request, 3 validation failure, 4 changed input or existing output, 5 I/O failure. Unexpected host failures can terminate the process; callers must check both its exit code and valid JSON.

## Commands

| Command | Behavior |
| --- | --- |
| `help [command]`, `--help`, `-h` | Compact JSON command help; also returned when no command is supplied |
| `--version` | Executable build version and supported capabilities |
| `schema [patch\|query\|commands\|launch-plan\|snapshot\|resources\|operation]` | Full discovery, or a focused JSON Schema/reference that fits a smaller context |
| `inspect <level.jkl> [sector-index]` | Source hash, game, counts, materials, sector and thing indices; optional detailed geometry |
| `query <level> <query.json\|->` | Bounded, paged readback of fields used by existing edits, including templates and lighting |
| `snapshot <level> <snapshot.json\|-> <new-output.png>` | Explicit-camera diagnostic or static lit PNG, rendered offscreen; no window or game |
| `validate <level.jkl> [resources.json\|-]` | Structural diagnostics, with optional current-resource ARCH validation |
| `plan <level.jkl> <patch.json\|->` | Edit an isolated model, serialize and reopen; no writes |
| `apply <level.jkl> <patch.json\|-> <new-output.jkl> [expected-output-sha256]` | Prepare, optionally require the reviewed output hash, recheck inputs and atomically create a new file |
| `new <new-output.jkl> [mots|jk]` | Create an untextured room scaffold; defaults to MotS |
| `launch-plan <launch-request.json\|->` | Return executable, working directory, arguments and required loose files; no launch |
| `archive <archive.gob\|archive.goo>` | List exact JKL entry names and the archive SHA256 |
| `extract <archive> <exact-entry-name> <new-output.jkl>` | Recover the selected JKL byte-for-byte into a new file |
| `convert <legacy.jed> <new-output.jkl> [resources.json\|-] [expected-output-sha256]` | Import supported native text projects and export a validated JKL; optionally bind ARCH resources and reviewed output bytes |

`inspect`, `query`, `validate`, `plan` and `apply` also accept supported native `.jed`, `.sed` and `.ned` text projects. Conversion reports preservation warnings. Native template libraries are read from the tool's `data` folder and a project companion `master.tpl` or `mots.tpl`; preserve those inputs with the project. `inputSha256` continues to identify the project file. The additional `sourceRevision` binds that file and the native template inputs, including missing companion files whose later appearance could change import behavior. Native patches must include the revision returned by inspection or query. The importer supports observed layouts, not every historical editor dialect.

Selectors are zero-based and valid only for the exact `sourceRevision` (`inputSha256` for JKL). Later operations can address objects appended earlier in the same plan. Version 1 has no delete/reorder operations because these require fuller COG and unknown-section reference remapping.

Requests supplied using `-` are a single UTF-8 JSON document on stdin, terminated by EOF. Files use the same bounded reader; UTF-8 BOM is accepted. No shell commands are accepted inside requests. A client should invoke the executable with an argument array, redirect stdin/stdout/stderr, close stdin after writing, and check the exit code and JSON result. This is a tool-neutral CLI and does not claim to implement MCP.

## Bounded readback

Use `schema query` for the exact request schema. A first query can be as small as:

```json
{ "schemaVersion": 1, "collection": "summary" }
```

Collections cover `summary`, `sectors`, `things`, `lights`, `templates`, `materials`, `colormaps`, `surfaces` and `vertices`. `surfaces` and `vertices` require a `sector` index. Search applies to things, templates, materials and colormaps; it is case-insensitive. Use the returned index, rather than assuming a name is unique. The default page is 50 items and the maximum is 200. Follow `nextOffset` until it is null; a response may return fewer items to respect its 1 MiB limit. `total` is the filtered count and `sourceTotal` is the unfiltered count. A zero-result search succeeds with an empty `items` array; an oversized single record fails explicitly.

For example, find candidate templates without putting the entire template library in the agent's context:

```json
{ "schemaVersion": 1, "collection": "templates", "search": "weapon", "offset": 0, "limit": 20 }
```

Read sector ambient and flags using `sectors`; thing position, orientation and parameter values using `things`; and editor-light settings using `lights`. `summary` includes the header and counts. Detailed surface and vertex pages expose stored lighting and geometry. Corner `mono` is the nullable stored channel; when absent, the implicit scalar is `rgb[0]`. Each response identifies the source, so clients can detect changes between pages. `inspect` remains available for existing callers, but its legacy response is not paginated.

Sector `geometryBounds` are calculated from current vertices and kept separate from authored `boundBox` metadata. `neighbors` lists adjoined sectors, without claiming that every link is visible or traversable. Surface queries supply inward `normal`, corner-average `centroid` and `geometryBounds`; these let a client choose a face by direction and location without hardcoded surface numbers.

## Example edit

Run `inspect` first and use its `inputSha256`:

```json
{
  "schemaVersion": 1,
  "inputSha256": "REPLACE_WITH_64_HEX_DIGITS_FROM_INSPECT",
  "operations": [
    { "op": "setGravity", "value": 4 },
    { "op": "setSectorAmbient", "sector": 0, "value": 0.25 },
    { "op": "addBox", "center": [4, 0, 0], "halfSize": 1 }
  ]
}
```

```powershell
./agent/sed-agent.exe plan ./original.jkl ./patch.json
./agent/sed-agent.exe apply ./original.jkl ./patch.json ./edited.jkl
```

For a reviewed edit, take `outputSha256` from `plan` and pass it as the final `apply` argument. The tool recomputes the patch and refuses to write if the resulting bytes differ. This catches a changed request as well as changes to output generation. Omitting the argument retains the original apply behavior. Native input patches additionally need `"sourceRevision": "..."` from the current query or inspection.

Planning never changes the source. Failure of a later operation cannot persist an earlier operation. The output must end in `.jkl`, its directory must exist, and its destination must not exist. There is no overwrite flag.

## Architectural resources and reviewed exports

Patches can include `resourceArchives`: one through sixteen explicit absolute GOB/GOO paths with SHA256 hashes, in first-archive precedence order. `schema resources` describes the corresponding request for `validate` and `convert`: `schemaVersion`, `inputSha256`, `resourceArchives`, and a native `sourceRevision` when applicable. This resolves architectural 3DO models; it does not establish closure of every MAT, COG, sound or runtime override.

With explicit resources, shared validation checks resolved thingflags `0x8`, ARCH indices and model names, the normalized native fingerprint when stored, exact geoset-zero mesh/vertex counts, and every lower LOD's array coverage. Lower LODs may use prefixes of the stored mesh/vertex slots. Numeric row labels in 3DO data tables are ignored like the engine; physical row counts and referenced array bounds are checked. A lower LOD requiring more slots is refused even when the highest-detail preview would fit.

Each report includes `architectureValidation` and a separate `resourceRevision`. Copy the reviewed plan's `resourceRevision` into the patch before `apply`, and pass its `outputSha256` as the final argument. The revision binds ordered archive identities/hashes, the used model entry identities/hashes, and validator version. A same-name model replacement can leave the exported JKL byte-for-byte identical; the separate revision detects this when the output hash alone cannot. Omitting `resourceRevision` performs fresh validation without asserting that a previous resource result was reviewed. Revisions are scoped to the resources used by that command; a lit snapshot may additionally resolve MAT/CMP entries.

The internal prepared edit retains immutable archive requests and the exact validated output hash. Commit reopens those archives, recomputes resource resolution, checks source/template dependencies, writes a temporary output, then rechecks dependencies and resources immediately before the no-overwrite move. Resources are checked through both held read handles and their current named paths. Windows read sharing prevents writes to those open archives; final named-path races cannot be eliminated on every filesystem by hashing alone.

`architectureValidation.status` is `not-applicable`, `unverified-no-resources`, `compatible-current-resources` or `invalid`. Each binding reports typed diagnostics, `topologyCompatible`, `canUseBinding`, `baselineStatus` and `historicalBaselineVerified`. A JKL's missing historical fingerprint is a warning: today's compatible model cannot certify the original bake. A native matching fingerprint is stronger evidence; unsupported normalization is distinguished from a proven mismatch. Neither result certifies sensible vertex correspondence, current placement lighting or a correct bake. No lighting is silently cleared or recalculated.

Resource-free editing remains available with an explicitly unverified ARCH result. An explicit clear operation can repair an incompatible binding while retaining its stored arrays. GUI Save As, GOB/GOO export and Save and Test use the same validator against the current loaded archives, stage their outputs and recheck document/resource state before promotion. Recovery, autosave and backup remain available for unresolved documents. GUI validation covers those editor archives; loose files and other engine resource overrides may change what a launched game resolves.

Each plan change includes the targeted object, its `before` state, the state `afterOperation`, and `serializedFinal` read back from the generated JKL. Simple appended objects have a null `before` and an explicit new target index. Extrusion instead reports its source sector before editing and both the source and appended sector afterward. Later operations may intentionally replace an earlier value; the final readback is checked against the final model. If the affected state cannot survive serialization, planning fails instead of advertising an edit that will disappear on save.

Operations cover thing position/orientation/name/template/parameter, vertex position and lighting, surface material/flags/UV/render modes, sector flags/scalar ambient/colormap, gravity, resource-name registration, extrusion, adjoining, and appending boxes, things and editor lights. `schema` is the authoritative field reference; `schema setThingPosition`, for example, returns only that operation's patch schema. Templates must already exist. Geometry uses engine world units; orientation is pitch/yaw/roll in degrees. Editor-light RGB fields contain three values in [0,1]; colored editor lights require MotS. Sector ambient is a scalar as stored in JK/MotS JKL.

`registerMaterial` and `registerColormap` append unique `.mat` and `.cmp` basenames, returning their indices. Existing names are refused so clients query and reuse their indices. This registers references only: it does not find, verify or copy assets. `setSurfaceMaterial` selects a registered material; `setSectorColormap` selects a registered palette. `fitSurfaceTexture` fits UVs to caller-supplied dimensions in texels; `setSurfaceUv` takes one `[u,v]` pair per corner in query order. `setSurfaceRenderModes` sets geometry, lighting and texture modes explicitly; zero geometry mode hides the face.

`extrudeSurface` extends a closed convex sector outward by a positive distance and appends a room with inward-facing surfaces and a reciprocal open portal. It refuses existing adjoins, malformed/nonconvex geometry, possible overlap with another sector and unknown sections that may contain topology references. Its conservative overlap check can refuse separated rotated shapes. `adjoinSurfaces` joins complete coincident opposite boundaries in distinct valid sectors. Both create `Geo=0` portal faces with visible/move adjoin flags. Existing sector and global surface indices remain in order; no unsupported COG/reference remapping is attempted. `addBox` still creates an unconnected sector.

`setVertexLight` takes `sector`, `vertex`, `channel` (`mono`, `red`, `green`, or `blue`) and `value` in [0,1000]. It updates that component on all corners sharing the selected vertex in that sector and preserves the other components. Independent RGB channels require MotS; JK accepts `mono`. This edits stored lighting and does not run a lighting bake.

Duplicate/unknown fields, missing fields, stale hashes, invalid indices, non-finite numbers and out-of-range values are rejected. Limits are 1 MiB and 1,000 operations per request, and 128 MiB per JKL input.

Operation request errors include `details.operationIndex`, `details.operation` and a `details.jsonPointer` so a client can repair the failing part of a batch. Parser errors include location details where available. Structural issues retain `itemIndex` and `parameterIndex` in addition to sector/surface selectors. Indices refer to the relevant issue type; they are not all thing indices.

## Source-light authoring

Discover `schema addLight` and `schema setLight` from the selected executable. `addLight` still requires `position`, `rgb`, `intensity` and `range`; it additionally accepts optional `rgbIntensity`, `legacyRgbRange`, `flags` and `layer`. `setLight` requires a zero-based `light` index from `query lights` and at least one of those eight editable fields. Omitted fields remain unchanged during `setLight`; omission on creation gives inherited RGB intensity, absent legacy RGB range, flags zero and layer zero.

| Field | Agent input and meaning |
| --- | --- |
| `position` | Three finite world coordinates, each from -1,000,000 through 1,000,000. |
| `rgb` | Three finite color components in [0,1]. Nonwhite authoring requires MotS. |
| `intensity` | White/mono intensity in [0,1000]. Supplies RGB intensity when no override is stored. |
| `rgbIntensity` | Independent RGB intensity in [0,1000], or `null` to inherit `intensity`. A non-null authored override requires MotS. |
| `range` | Primary falloff range in [0.0001,100000], used by both channels when baking. |
| `legacyRgbRange` | Preserved legacy value in [-100000,100000], or `null` to clear it. It does not control falloff. |
| `flags` | Unsigned 32-bit mask, expressed as a decimal JSON integer. |
| `layer` | Index of an existing named editor layer; only zero is accepted when the level has no named layers. The schema permits nonnegative integers through 2,147,483,647, and runtime validation checks the current level's layer list. |

An explicit `rgbIntensity: 0` contributes no RGB even when white intensity is positive. An explicit value equal to `intensity` remains independent: setting both to 2 and later changing only white to 3 leaves RGB at 2. `null` restores inheritance, so RGB follows later white edits. `legacyRgbRange: 0` is a stored value; it differs from `null`. Both nullable fields reject non-finite numbers. Imported inactive JK RGB metadata remains readable and survives conversion and unrelated edits; agents may clear its override with `null`, but cannot author a new non-null RGB intensity in JK.

`query lights` and each light operation's effect state report `position`, `rgb`, `intensity`, `range`, `rgbIntensity`, `legacyRgbRange`, `flags` and `layer`, plus read-only `effectiveRgbIntensity` and `rgbIntensityInherited`. These fields participate in the plan's serialization/readback checks. Neither operation bakes corners or architectural lighting. MotS's explicit static bake uses white and RGB intensities separately with the primary range; existing stored lighting changes only when an edit or bake explicitly changes it.

Supported native JED/SED/NED text projects import into JKL. Their extra source-light values are written in additive, versioned `SED_SOURCE_LIGHTS` editor metadata, leaving ordinary JKL `LIGHTS` rows in their established layout. The extension also preserves inactive JK source RGB color. A semantic checksum binds the extension to the ordinary rows; malformed metadata, unsupported versions or a mismatched checksum refuse parsing rather than applying stale controls. There is no native project writer in this path.

Keep the native project and companion templates as recovery sources. Older editors may discard `SED_SOURCE_LIGHTS`, losing the additional authoring controls; an older editor that retains the extension but changes its base rows can instead trigger the checksum refusal. This metadata does not replace baked engine lighting or establish retail compatibility. Retail acceptance and rendering of newly exported files remain unverified.

Inspection, queries and validation report `sourceLightMetadata.state` as `absent` or `validated`; `binding` is null or `ordinary-lights-by-index`, respectively. Plans and conversions additionally report `outputSourceLightMetadata` from their reparsed output. This describes the current input/output extension and its consistency with indexed ordinary rows. It cannot establish preservation history or detect a wholly removed extension without an earlier artifact to compare. Malformed or unsupported metadata refuses parsing. The canonical v1 contract is in [SOURCE_LIGHT_METADATA.md](SOURCE_LIGHT_METADATA.md).

## Offscreen visual feedback

Use `schema snapshot` for the exact camera contract. The request requires the current `inputSha256`, `position`, `target`, `fieldOfViewDegrees`, `width` and `height`, plus `schemaVersion: 1`; native inputs also require `sourceRevision`. Coordinates are Z-up. Output is a new `.png`; source/dependencies are checked before rendering and before the atomic output move. No window opens.

The default `mode: "diagnostic"` draws polygon geometry, including untextured solid faces, and omits reciprocal visible open portal faces. Its colors distinguish surfaces. It does not depict materials or game lighting. Diagnostic requests reject the lit-only options `lightingMode`, `renderProfile`, `strictAssets` and `resourceArchives`.

`mode: "lit"` uses the same static scene assembler and Vulkan renderer as the editor: indexed MAT textures and solid-color materials, sector CMP palettes, independent MotS mono/RGB corner light, model base light and compatible ARCHLIGHTING bindings. Supply `resourceArchives: [{"path":"C:/absolute/resources.goo","sha256":"...64 hex digits..."}]` in precedence order; the first matching archive wins. Nothing discovers an installation or scans directories. MATs, CMPs and models resolve from those archives; missing templates can leave things without a model and are reported.

The lit request has two independent choices. `lightingMode: "game"` selects RGB for MotS and mono for JK; `"mono"` and `"rgb"` select the scene channel explicitly. `renderProfile` selects how textured scalar light is shaded:

| `renderProfile` | Textured scalar shading |
| --- | --- |
| `"openjk"` (default) | Multiply the CMP base palette color by scalar light, following the targeted OpenJKDF2 hardware path. |
| `"palette"` | Select a CMP light-table row, for a palette-ramp comparison. This does not establish retail pixel parity. |

Changing `lightingMode` does not change `renderProfile`. Keep the profile, resources and camera fixed when comparing mono with RGB; choose `renderProfile: "palette"` explicitly when testing CMP ramps. Both profiles use CMP base palettes, and the profile switch does not change solid-material shading. Ordinary model things retain the mono pass even in an RGB world or with `lightingMode: "rgb"`. Models marked as level geometry by resolved `thingflags` bit `0x8` follow the scene lighting selection and can use compatible ARCHLIGHTING. A binding on an ordinary thing is diagnosed instead of applied.

`strictAssets: true` is the default for lit requests. Missing, ambiguous or unsupported resources, malformed architectural bindings and unsupported special model-lighting modes refuse the snapshot before GPU rendering. `strictAssets: false` permits explicit fallback diagnostics. Ordinary things with no model are omitted and reported; they can be legitimate invisible controllers. Each result records hashes of supplied archives and all used entries. Archive contents are checked again before output promotion. The request is limited to 16 archives, 512 MiB per archive, 2 GiB total archives, 128 MiB per used entry and 256 MiB of resolved asset bytes.

The preview uses the same all-LOD ARCH validator as resource-aware export, including a native project's nonempty normalized model baseline. This historical fingerprint strips comments, trims and drops blank lines, appends Windows CRLF line endings, then hashes UTF-8 text with SHA256; it is distinct from the raw archive-entry hash in the receipt. Verification is supported for ASCII input without NUL and with lines at most 4095 characters. Unsupported text or a mismatch makes the binding unverifiable: strict mode refuses the snapshot, while permissive mode reports the issue and uses model base/sector light without that binding. JKL inputs do not carry this native content baseline. Checking lower-LOD shape does not render those LODs or rebake lighting.

Static lit snapshots do not simulate dynamic lights, COG, animations, engine portal visibility, fog, exact sky mapping or retail software transparency tables. They currently use the first MAT cel/mip, indexed pixels, model geoset zero and the model rest pose. Unsupported truecolor MATs are diagnosed rather than interpreted as palette indices. Double-sided triangle fans can differ on concave/nonplanar faces. A lit image supports design review; it is not a retail or engine gameplay screenshot. The [renderer notes](RENDERER_LIGHTING.md) and [source audit](audit/MOTS_LIGHTING_REFERENCE_AUDIT.md) distinguish implemented behavior from comparison evidence.

The result reports the selected `lightingMode` and `renderProfile` for lit images, camera settings, source/dependency revision, PNG/RGBA hashes, dimensions, graphics device, geometry counts, background coverage and limitations. Image bytes can differ between graphics drivers. Dimensions are 64..2048 with at most 2,097,152 pixels; geometry is capped at 250,000 triangles. A usable Vulkan loader and device are required. Invoke snapshots with a process timeout (the supplied design smoke uses 30 seconds); driver fence waits are not cancellable inside this command.

The repository/distribution includes a [Codex skill](../.agents/skills/sed-mots/SKILL.md). Codex can call the CLI and inspect PNG files directly. No MCP server is required or registered. The [design roadmap](AGENT_DESIGN_ROADMAP.md) describes an optional shared-library MCP adapter and the remaining domain capabilities.

## Read-only launch planning

```json
{
  "game": "MysteriesOfTheSith",
  "gameDirectory": "E:/Games/MotS",
  "projectDirectory": "E:/JKProjects/test01",
  "levelFileName": "test01.jkl",
  "executable": "openjkdf2-64.exe",
  "engine": "OpenJKDF2"
}
```

Games: `JediKnight`, `MysteriesOfTheSith`. Engines: `Auto`, `OpenJKDF2`, `Retail`. The planner reads current local paths. A successful plan establishes that the invocation passes those checks, not that the game or project was tested.

## Preservation limits

Planning checks structural validity and the affected fields after serialization. This is not a whole-document equivalence check. Separate corpus audits compare a broader modeled-field digest. Neither proves gameplay, resources or every possible extension. Unknown sections are retained and may contain indexes requiring separate interpretation after topology changes. Existing malformed levels remain inspectable, but `apply` refuses outputs with validation errors.

Architectural lighting is preserved, not rebaked. Changed model names with old bindings are refused during editing. Explicit `resourceArchives` extend `validate`, `plan`, `apply` and `convert` with the same current-model all-LOD and optional historical-fingerprint checks as the lit preview. Requests without those archives remain structurally checked and resource-unverified. The native `sourceRevision` covers imported template files; `resourceRevision` separately covers the declared archives and used model entries. Neither identifies every installed or runtime resource. Retain source inputs and use a new output for each reviewed edit.

When deliberately changing an architectural model, add `{"op":"clearThingArchitecture","thing":0}` before the template/model change. This explicitly removes the old thing binding while retaining its archived lighting arrays. It does not calculate replacement lighting.
