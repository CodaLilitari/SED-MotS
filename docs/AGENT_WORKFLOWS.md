# Local agent workflows

The packaged `agent/sed-agent.exe` supports local tools that can start an executable and read JSON. It works without an editor window, graphics device, model SDK, credentials, or a network service. The calling agent chooses edits; the executable handles parsing, validation, provenance checks, and new output creation. This command-line protocol is not an MCP server.

Start with `sed-agent --help`, `sed-agent schema commands`, and `sed-agent schema query`. Use `sed-agent schema setGravity` (or another operation name) for a focused patch schema. All normal command responses, including expected failures, are JSON. Check the process exit code before using the result; the full contract is in [AGENT_PROTOCOL.md](AGENT_PROTOCOL.md).

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

## Discover exact targets

Use `query` collections `sectors`, `things`, `lights`, `templates`, and `materials` for bounded discovery. `surfaces` and `vertices` require a `sector` index. `summary` includes header values and counts. Templates expose existing definitions; things expose current position, orientation, and parameters. Use `search` only with `things`, `templates`, or `materials`.

Requests use `offset` and `limit` (maximum 200). Continue from the returned `nextOffset` until it is null; response-size limits may shorten a page before the requested limit. Returned indices identify objects in the original collection, even after a search. Bind selectors to the returned input hash and source revision; never treat a page position as an object index.

For a GOB/GOO archive, call `archive` to discover exact JKL entry names, then `extract` one selected entry into a fresh `.jkl` output. Extraction preserves the selected bytes. Follow with query, plan, reviewed apply, and validation. The CLI does not expose general archive extraction or start the game. `launch-plan` only prepares a launch description.

## Native project provenance

For supported native text `.jed`, `.sed`, or `.ned` inputs, copy both `inputSha256` and `sourceRevision` from `inspect` or `query` into the patch. The file hash identifies the main project; `sourceRevision` also binds the importer template dependencies reported in `dependencies`. Native patches require this revision. A changed, removed, or newly present companion template can invalidate the inspection even when the project file itself is unchanged.

Retain the dependency files with the project, examine conversion warnings, and regenerate the patch after a provenance conflict. This dependency check covers the reported native import inputs; it is not a hash inventory of every game resource. Existing architectural lighting is preserved subject to the protocol's restrictions and is not rebaked.

## Repeatable smoke test

Run the bundled workflow check with PowerShell 7.2 or later and an explicit executable. The output directory must not exist; its parent must exist.

```powershell
./tools/Test-AgentWorkflow.ps1 -AgentPath 'C:/Tools/SED/agent/sed-agent.exe' `
    -OutputDirectory ('C:/Temp/sed-agent-smoke-' + [Guid]::NewGuid().ToString('N'))
```

The test creates only synthetic JK/MotS scaffolds and receipts in that directory. It checks discovery, stdin transport, template pagination, reviewed apply, hash/readback agreement, stale input, overwrite refusal, operation diagnostics, input limits, Unicode rejection, and malformed launch plans. It starts the supplied agent directly with `ProcessStartInfo.ArgumentList`, drains stdout/stderr asynchronously, closes stdin, and limits each process to 30 seconds. No child shell, GUI, game, or Python runtime is used. `receipt.json` records each command, expected/actual exit codes, parsed JSON, and hash checks; a failed check throws and retains the evidence.
