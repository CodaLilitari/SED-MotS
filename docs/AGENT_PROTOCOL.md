# Local agent protocol, version 1

`agent/sed-agent.exe` is a local JSON command-line interface. It does not start a GUI, graphics device, shell, game or network service. It uses the same model and Core edit commands as the editor.

Supported outcomes are JSON on stdout. Exit codes: 0 success, 2 invalid request, 3 validation failure, 4 changed input or existing output, 5 I/O failure. Unexpected host failures can terminate the process; callers must check both its exit code and valid JSON.

## Commands

| Command | Behavior |
| --- | --- |
| `help [command]`, `--help`, `-h` | Compact JSON command help; also returned when no command is supplied |
| `--version` | Executable build version and supported capabilities |
| `schema [patch\|query\|commands\|launch-plan\|operation]` | Full discovery, or a focused JSON Schema/reference that fits a smaller context |
| `inspect <level.jkl> [sector-index]` | Source hash, game, counts, materials, sector and thing indices; optional detailed geometry |
| `query <level> <query.json\|->` | Bounded, paged readback of fields used by existing edits, including templates and lighting |
| `validate <level.jkl>` | Structural diagnostics; no engine or resource-load claim |
| `plan <level.jkl> <patch.json\|->` | Edit an isolated model, serialize and reopen; no writes |
| `apply <level.jkl> <patch.json\|-> <new-output.jkl> [expected-output-sha256]` | Prepare, optionally require the reviewed output hash, recheck inputs and atomically create a new file |
| `new <new-output.jkl> [mots|jk]` | Create an untextured room scaffold; defaults to MotS |
| `launch-plan <launch-request.json\|->` | Return executable, working directory, arguments and required loose files; no launch |
| `archive <archive.gob\|archive.goo>` | List exact JKL entry names and the archive SHA256 |
| `extract <archive> <exact-entry-name> <new-output.jkl>` | Recover the selected JKL byte-for-byte into a new file |
| `convert <legacy.jed> <new-output.jkl>` | Import supported native text JED/SED/NED project data and export a validated JKL |

`inspect`, `query`, `validate`, `plan` and `apply` also accept supported native `.jed`, `.sed` and `.ned` text projects. Conversion reports preservation warnings. Native template libraries are read from the tool's `data` folder and a project companion `master.tpl` or `mots.tpl`; preserve those inputs with the project. `inputSha256` continues to identify the project file. The additional `sourceRevision` binds that file and the native template inputs, including missing companion files whose later appearance could change import behavior. Native patches must include the revision returned by inspection or query. The importer supports observed layouts, not every historical editor dialect.

Selectors are zero-based and valid only for the exact input hash. Later operations can address objects appended earlier in the same plan. Version 1 has no delete/reorder operations because these require fuller COG and unknown-section reference remapping.

Requests supplied using `-` are a single UTF-8 JSON document on stdin, terminated by EOF. Files use the same bounded reader; UTF-8 BOM is accepted. No shell commands are accepted inside requests. A client should invoke the executable with an argument array, redirect stdin/stdout/stderr, close stdin after writing, and check the exit code and JSON result. This is a tool-neutral CLI and does not claim to implement MCP.

## Bounded readback

Use `schema query` for the exact request schema. A first query can be as small as:

```json
{ "schemaVersion": 1, "collection": "summary" }
```

Collections cover `summary`, `sectors`, `things`, `lights`, `templates`, `materials`, `surfaces` and `vertices`. `surfaces` and `vertices` require a `sector` index. Named collections support a case-insensitive `search`; use the returned index, rather than assuming a name is unique. The default page is 50 items and the maximum is 200. Follow `nextOffset` until it is null; a response may return fewer items to respect its byte limit. A zero-result search succeeds with an empty `items` array.

For example, find candidate templates without putting the entire template library in the agent's context:

```json
{ "schemaVersion": 1, "collection": "templates", "search": "weapon", "offset": 0, "limit": 20 }
```

Read sector ambient and flags using `sectors`; thing position, orientation and parameter values using `things`; and editor-light settings using `lights`. `summary` includes the header and counts. Detailed surface and vertex pages expose stored lighting and geometry. Corner `mono` is the nullable stored channel; when absent, the implicit scalar is `rgb[0]`. Each response identifies the source, so clients can detect changes between pages. `inspect` remains available for existing callers, but its legacy response is not paginated.

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

Each plan change includes the targeted object, its `before` state, the state `afterOperation`, and `serializedFinal` read back from the generated JKL. Appended objects have a null `before` and an explicit new target index. Later operations may intentionally replace an earlier value; the final readback is checked against the final model. If the affected state cannot survive serialization, planning fails instead of advertising an edit that will disappear on save.

Operations cover thing position/orientation/name/template/parameter, vertex position and lighting, surface material/flags, sector flags/scalar ambient, gravity, and appending boxes, things and editor lights. `schema` is the authoritative field reference; `schema setThingPosition`, for example, returns only that operation's patch schema. Templates must already exist; material selection uses the current material table. A new scaffold has no material entries, so importing textures remains a GUI task. `addBox` creates an unconnected sector, not an automatically joined corridor. Geometry uses engine world units; orientation is pitch/yaw/roll in degrees. Editor-light RGB fields contain three values in [0,1]; colored editor lights require MotS. Sector ambient is a scalar as stored in JK/MotS JKL.

`setVertexLight` takes `sector`, `vertex`, `channel` (`mono`, `red`, `green`, or `blue`) and `value` in [0,1000]. It updates that component on all corners sharing the selected vertex in that sector and preserves the other components. Independent RGB channels require MotS; JK accepts `mono`. This edits stored lighting and does not run a lighting bake.

Duplicate/unknown fields, missing fields, stale hashes, invalid indices, non-finite numbers and out-of-range values are rejected. Limits are 1 MiB and 1,000 operations per request, and 128 MiB per JKL input.

Operation request errors include `details.operationIndex`, `details.operation` and a `details.jsonPointer` so a client can repair the failing part of a batch. Parser errors include location details where available. Structural issues retain `itemIndex` and `parameterIndex` in addition to sector/surface selectors. Indices refer to the relevant issue type; they are not all thing indices.

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

Architectural lighting is preserved, not rebaked. Changed model names with old bindings are refused; replacing a same-name 3DO file is not yet verified against a hash or topology. The native source revision covers the imported template files, not every MAT, 3DO, COG or installed game resource. Retain source inputs and use a new output for each reviewed edit.

When deliberately changing an architectural model, add `{"op":"clearThingArchitecture","thing":0}` before the template/model change. This explicitly removes the old thing binding while retaining its archived lighting arrays. It does not calculate replacement lighting.
