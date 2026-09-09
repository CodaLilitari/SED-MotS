# Local agent protocol, version 1

`agent/sed-agent.exe` is a local JSON command-line interface. It does not start a GUI, graphics device, shell, game or network service. It uses the same model and Core edit commands as the editor.

Supported outcomes are JSON on stdout. Exit codes: 0 success, 2 invalid request, 3 validation failure, 4 changed input or existing output, 5 I/O failure. Unexpected host failures can terminate the process; callers must check both its exit code and valid JSON.

## Commands

| Command | Behavior |
| --- | --- |
| `schema` | Command reference, limits and patch JSON Schema |
| `inspect <level.jkl> [sector-index]` | Source hash, game, counts, materials, sector and thing indices; optional detailed geometry |
| `validate <level.jkl>` | Structural diagnostics; no engine or resource-load claim |
| `plan <level.jkl> <patch.json>` | Edit an isolated model, serialize and reopen; no writes |
| `apply <level.jkl> <patch.json> <new-output.jkl>` | Prepare, recheck source hash and atomically create a new file |
| `new <new-output.jkl> [mots|jk]` | Create an untextured room scaffold; defaults to MotS |
| `launch-plan <launch-request.json>` | Return executable, working directory, arguments and required loose files; no launch |
| `archive <archive.gob\|archive.goo>` | List exact JKL entry names and the archive SHA256 |
| `extract <archive> <exact-entry-name> <new-output.jkl>` | Recover the selected JKL byte-for-byte into a new file |
| `convert <legacy.jed> <new-output.jkl>` | Import supported native text JED/SED/NED project data and export a validated JKL |

`inspect`, `validate`, `plan` and `apply` also accept supported native `.jed`, `.sed` and `.ned` text projects. Conversion reports preservation warnings. Native template libraries are read from the tool's `data` folder and a project companion `master.tpl` or `mots.tpl`; preserve those inputs with the project. The input hash identifies the project file, not all external resources. The importer supports observed layouts, not every historical editor dialect.

Selectors are zero-based and valid only for the exact input hash. Later operations can address objects appended earlier in the same plan. Version 1 has no delete/reorder operations because these require fuller COG and unknown-section reference remapping.

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

Planning never changes the source. Failure of a later operation cannot persist an earlier operation. The output must end in `.jkl`, its directory must exist, and its destination must not exist. There is no overwrite flag.

Operations cover thing position/orientation/name/template/parameter, vertex position and lighting, surface material/flags, sector flags/scalar ambient, gravity, and appending boxes, things and editor lights. `schema` is the authoritative field reference. Templates must already exist; material selection uses the current material table. Geometry uses engine world units; orientation is pitch/yaw/roll in degrees. Editor-light RGB fields contain three values in [0,1]; sector ambient is a scalar as stored in JK/MotS JKL.

`setVertexLight` takes `sector`, `vertex`, `channel` (`mono`, `red`, `green`, or `blue`) and `value` in [0,1000]. It updates that component on all corners sharing the selected vertex in that sector and preserves the other components. Independent RGB channels require MotS; JK accepts `mono`. This edits stored lighting and does not run a lighting bake.

Duplicate/unknown fields, missing fields, stale hashes, invalid indices, non-finite numbers and out-of-range values are rejected. Limits are 1 MiB and 1,000 operations per request, and 128 MiB per JKL input.

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

Planning compares structural validity and basic counts after serialization. Separate corpus audits compare a broader modeled-field digest. Neither proves gameplay, resources or every possible extension. Unknown sections are retained and may contain indexes requiring separate interpretation after topology changes. Existing malformed levels remain inspectable, but `apply` refuses outputs with validation errors.

Architectural lighting is preserved, not rebaked. Changed model names with old bindings are refused; replacing a same-name 3DO file is not yet verified against a hash or topology. Retain source inputs and use a new output for each reviewed edit.

When deliberately changing an architectural model, add `{"op":"clearThingArchitecture","thing":0}` before the template/model change. This explicitly removes the old thing binding while retaining its archived lighting arrays. It does not calculate replacement lighting.
