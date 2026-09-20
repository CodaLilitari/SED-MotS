# Local agent workflows

The packaged `agent/sed-agent.exe` supports local tools that can start an executable and read JSON. Editing works without an editor window, graphics device, model SDK, credentials, or network service; optional diagnostic snapshots use offscreen Vulkan. The calling agent chooses edits; the executable handles parsing, validation, provenance checks, and new output creation. This command-line protocol is not an MCP server.

Start with `sed-agent --help`, `sed-agent schema commands`, and `sed-agent schema query`. Use `sed-agent schema setGravity` (or another operation name) for a focused patch schema. All normal command responses, including expected failures, are JSON. Check the process exit code before using the result; the full contract is in [AGENT_PROTOCOL.md](AGENT_PROTOCOL.md).

For a complete connected-geometry example, run [Test-AgentDesign.ps1](../tools/Test-AgentDesign.ps1) with an explicit `-AgentPath` and fresh `-OutputDirectory`. It discovers face directions, constructs an L-shaped three-room MotS layout through reviewed edits, assigns resource references and UVs, verifies a JK connection, and captures offscreen interior views. Fictional resource names make the test self-contained; no assets or playable episode are implied. `-SkipSnapshot` exercises only the authoring path on machines without Vulkan. Exact requests, results and a receipt remain in the output folder.

## Read, plan, review, apply, read back

This PowerShell 7 example operates on an existing JKL and writes to a destination that does not exist. Choose exact paths first. Its stdin is UTF-8, and the original input remains intact.

```powershell
$agent = 'C:/Tools/SED/agent/sed-agent.exe'
$inputLevel = 'C:/Projects/level/original.jkl'
$outputLevel = 'C:/Projects/level/gravity-reviewed.jkl'
$OutputEncoding = [Text.UTF8Encoding]::new($false)

function Invoke-SedJson([string[]]$AgentArguments, [string]$Request) {
    $resultText = $Request | & $agent @AgentArguments
    if ($LASTEXITCODE -ne 0) { throw ($resultText -join "`n") }
    ($resultText -join "`n") | ConvertFrom-Json
}

$query = @{ schemaVersion = 1; collection = 'summary'; offset = 0; limit = 1 } |
    ConvertTo-Json -Compress
$before = Invoke-SedJson @('query', $inputLevel, '-') $query
$patch = @{
    schemaVersion = 1
    inputSha256 = $before.inputSha256
    sourceRevision = $before.sourceRevision
    operations = @(@{ op = 'setGravity'; value = 6.25 })
} | ConvertTo-Json -Depth 8 -Compress
$plan = Invoke-SedJson @('plan', $inputLevel, '-') $patch
$plan | ConvertTo-Json -Depth 20
```

Review `changes`, `issues`, `preservationWarnings`, and `outputSha256` before the write. A successful plan describes a structurally validated serialization; it does not establish gameplay or resource availability. To apply exactly the reviewed bytes, keep the same patch and supply the plan's output hash:

```powershell
$applied = Invoke-SedJson @('apply', $inputLevel, '-', $outputLevel, $plan.outputSha256) $patch
$after = Invoke-SedJson @('query', $outputLevel, '-') $query
$actualHash = (Get-FileHash -LiteralPath $outputLevel -Algorithm SHA256).Hash.ToLowerInvariant()
if ($actualHash -ne $plan.outputSha256 -or $after.items[0].header.gravity -ne 6.25) {
    throw 'The resulting file did not match the reviewed edit.'
}
```

An existing output, changed source, changed dependency, or mismatched reviewed output hash is a conflict. Inspect again and prepare a new review when the source changes. Never silently substitute a fresh plan after reviewing an earlier one. Without the final expected-output hash, `apply` still validates and prevents overwrites, but does not bind the write to a previous plan's serialized bytes.

## Select a room face and extrude it

The following extends the PowerShell helper above. Supply the current input and
a fresh output path; the example chooses the east wall of the room containing
the origin. No sector or surface index is calculated by the caller.

```powershell
$selectionRequest = @{
    schemaVersion = 1; kind = 'surface'
    sectorPoint = @(0, 0, 0); outward = @(1, 0, 0)
} | ConvertTo-Json -Depth 8 -Compress
$selected = Invoke-SedJson @('select', $inputLevel, '-') $selectionRequest
if ($selected.status -ne 'unique' -or $null -eq $selected.selection) {
    throw "Select a more precise target: $($selected.status) at $($selected.stage)."
}
$patch = @{
    schemaVersion = 1
    inputSha256 = $selected.inputSha256
    sourceRevision = $selected.sourceRevision
    operations = @(@{
        op = 'extrudeSurface'; target = $selected.selection; distance = 2
    })
} | ConvertTo-Json -Depth 12 -Compress
$plan = Invoke-SedJson @('plan', $inputLevel, '-') $patch
# Inspect the plan and its selectionBindings, then apply the reviewed bytes.
$applied = Invoke-SedJson @('apply', $inputLevel, '-', $outputLevel, $plan.outputSha256) $patch
```

The source point identifies the room, and outward direction identifies the face.
Selection includes boundaries; an interior point avoids a two-room portal
ambiguity. A geometry-changing operation invalidates the old containing-sector
token, so select again from the new file before the next geometry edit. The
existing material, UV and lighting operations remain shared editor commands.

[Test-AgentSpatial.ps1](../tools/Test-AgentSpatial.ps1) exercises an eight-room
Relay Outpost with a loop, shortcut, annex and upper observation room. It uses
returned tokens for geometry edits, revisits existing rooms, checks stale and
ambiguous targets, and records reviewed applies, exact topology and optional
offscreen images. Run it with an explicit `-AgentPath`, new `-OutputDirectory`
and optional `-SkipSnapshot`. The fixture uses synthetic resource names and does
not establish a playable episode or game-resource availability.

## Discover exact targets

Use `query` collections `sectors`, `things`, `lights`, `templates`, and `materials` for bounded discovery. `surfaces` and `vertices` require a `sector` index. `summary` includes header values and counts. Templates expose existing definitions; things expose current position, orientation, and parameters. Use `search` only with `things`, `templates`, or `materials`.

Requests use `offset` and `limit` (maximum 200). Continue from the returned `nextOffset` until it is null; response-size limits may shorten a page before the requested limit. Returned indices identify objects in the original collection, even after a search. Bind selectors to the returned input hash and source revision; never treat a page position as an object index.

For a GOB/GOO archive, call `archive` to discover exact JKL entry names, then `extract` one selected entry into a fresh `.jkl` output. Extraction preserves the selected bytes. Follow with query, plan, reviewed apply, and validation. The CLI does not expose general archive extraction or start the game. `launch-plan` only prepares a launch description.

## Native project provenance

For supported native text `.jed`, `.sed`, or `.ned` inputs, copy both `inputSha256` and `sourceRevision` from `inspect` or `query` into the patch. The file hash identifies the main project; `sourceRevision` also binds the importer template dependencies reported in `dependencies`. Native patches require this revision. A changed, removed, or newly present companion template can invalidate the inspection even when the project file itself is unchanged.

Retain the dependency files with the project, examine conversion warnings, and regenerate the patch after a provenance conflict. This dependency check covers the reported native import inputs; it is not a hash inventory of every game resource. Existing architectural lighting is preserved subject to the protocol's restrictions and is not rebaked.

## Edit source lights without rebaking

Use `schema setLight`, `schema addLight`, and `query lights` before choosing a light. With the `Invoke-SedJson` helper above, the following prepares independent white and RGB controls for the first returned light in a MotS project. Use the returned index for the light you intend to edit; the request's revision binds that selection to the inspected source.

```powershell
$lightQuery = @{ schemaVersion = 1; collection = 'lights'; offset = 0; limit = 1 } |
    ConvertTo-Json -Compress
$lights = Invoke-SedJson @('query', $inputLevel, '-') $lightQuery
if ($lights.items.Count -eq 0) { throw 'Choose a project with an existing light, or use addLight.' }
$lightPatch = @{
    schemaVersion = 1
    inputSha256 = $lights.inputSha256
    sourceRevision = $lights.sourceRevision
    operations = @(@{
        op = 'setLight'; light = $lights.items[0].index
        intensity = 0.75; rgbIntensity = 2.5
    })
} | ConvertTo-Json -Depth 8 -Compress
$lightPlan = Invoke-SedJson @('plan', $inputLevel, '-') $lightPatch
$lightPlan | ConvertTo-Json -Depth 20
```

Review the plan, then apply the same request to a fresh output path with `$lightPlan.outputSha256` as the final argument. Query that output and check both stored values and `effectiveRgbIntensity`/`rgbIntensityInherited`. Source-light edits do not automatically change baked corner or architectural lighting.

Leave a field out of `setLight` to retain its current value. Use `rgbIntensity: null` to inherit white intensity, zero to turn off the RGB contribution independently, or a non-null scalar to keep RGB independent through later white edits. `legacyRgbRange` is retained metadata: changing it does not change the shared primary falloff range. `addLight` keeps its original four required fields and accepts optional RGB intensity, legacy RGB range, flags and layer. The [protocol field table](AGENT_PROTOCOL.md#source-light-authoring) gives limits and JK restrictions.

Native input is converted to JKL with additive `SED_SOURCE_LIGHTS` authoring metadata; no native project writer is provided. Retain the original source because older editors may discard the extension. A retained extension whose ordinary light rows were changed is rejected when its checksum no longer matches. Retail handling remains unverified.

## Repeatable source-light workflow

Run the source-light workflow with the selected packaged executable and a fresh directory:

```powershell
./tools/Test-AgentSourceLights.ps1 -AgentPath 'C:/Tools/SED/agent/sed-agent.exe' `
    -OutputDirectory ('C:/Temp/sed-source-lights-' + [Guid]::NewGuid().ToString('N'))
```

It creates an original synthetic native project, discovers the actual CLI schemas, converts to JKL, then plans/applies metadata-only, independent-intensity and unrelated edits. Readback checks inherited, zero and explicitly equal RGB values, original `addLight` compatibility, and stale/overwrite refusals. Direct processes have redirected streams, no console window and a 30-second deadline. The receipt retains request/reply files and hashes, artifact hashes, the executable identity and the outcome. It does not request Vulkan, a GUI or a game; these checks do not establish rendering or gameplay compatibility. Packaging invokes this workflow by default and records a successful result in `PROVENANCE.json` only after it passes.

The existing `convert` command has no standalone caller `sourceRevision` parameter; resource-aware conversion accepts it in the resource request. This resource-free synthetic workflow first obtains a revision-bound identity-edit plan, calls conversion (which captures and rechecks its own inputs), then verifies the returned revision and output hash against that plan. That post-conversion comparison is distinct from a caller revision guard before writing. For reviewed native edits, use `plan`/`apply` with `sourceRevision` and the reviewed output hash.

## Repeatable smoke test

Run the bundled workflow check with PowerShell 7.2 or later and an explicit executable. The output directory must not exist; its parent must exist.

```powershell
./tools/Test-AgentWorkflow.ps1 -AgentPath 'C:/Tools/SED/agent/sed-agent.exe' `
    -OutputDirectory ('C:/Temp/sed-agent-smoke-' + [Guid]::NewGuid().ToString('N'))
```

The test creates only synthetic JK/MotS scaffolds and receipts in that directory. It checks discovery, stdin transport, template pagination, reviewed apply, hash/readback agreement, stale input, overwrite refusal, operation diagnostics, input limits, Unicode rejection, and malformed launch plans. It starts the supplied agent directly with `ProcessStartInfo.ArgumentList`, drains stdout/stderr asynchronously, closes stdin, and limits each process to 30 seconds. No child shell, GUI, game, or Python runtime is used. `receipt.json` records each command, expected/actual exit codes, parsed JSON, and hash checks; a failed check throws and retains the evidence.
