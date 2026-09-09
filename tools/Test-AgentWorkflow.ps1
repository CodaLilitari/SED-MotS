#requires -Version 7.2
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$AgentPath,
    [Parameter(Mandatory)][string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$utf8 = [Text.UTF8Encoding]::new($false, $true)
$agent = (Resolve-Path -LiteralPath $AgentPath).ProviderPath
if (-not (Test-Path -LiteralPath $agent -PathType Leaf)) { throw 'AgentPath must name an executable file.' }
$scratch = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputDirectory)
if (Test-Path -LiteralPath $scratch) { throw 'OutputDirectory must be a fresh directory; existing paths are never reused.' }
if (-not (Test-Path -LiteralPath (Split-Path -Parent $scratch) -PathType Container)) { throw 'The parent of OutputDirectory must exist.' }
$null = New-Item -ItemType Directory -Path $scratch -ErrorAction Stop
$receipt = [ordered]@{
    schemaVersion = 1
    startedUtc = [DateTime]::UtcNow.ToString('o')
    agentPath = $agent
    agentSha256 = (Get-FileHash -LiteralPath $agent -Algorithm SHA256).Hash.ToLowerInvariant()
    outputDirectory = $scratch
    status = 'running'
    guiLaunched = $false
    shellChildLaunched = $false
    processTimeoutSeconds = 30
    steps = [Collections.Generic.List[object]]::new()
    checks = [Collections.Generic.List[object]]::new()
}

function Get-Hash([string]$Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() }
function Convert-Request($Value) { ConvertTo-Json -InputObject $Value -Depth 30 -Compress }
function Confirm-Check([string]$Name, [bool]$Condition) {
    $receipt.checks.Add([ordered]@{ name = $Name; passed = $Condition })
    if (-not $Condition) { throw "Workflow assertion failed: $Name" }
}
function Invoke-Agent {
    param([string]$Name, [string[]]$Arguments, [AllowEmptyString()][string]$InputText = '', [int]$ExpectedExit = 0)
    $number = $receipt.steps.Count + 1
    $stem = '{0:D2}-{1}' -f $number, $Name
    $inputBytes = $utf8.GetBytes($InputText)
    $step = [ordered]@{
        name = $Name; executable = $agent; arguments = $Arguments
        stdinBytes = $inputBytes.Length
        stdinSha256 = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($inputBytes)).ToLowerInvariant()
        expectedExitCode = $ExpectedExit; exitCode = $null; timedOut = $false
        stdoutFile = "$stem.stdout.json"; stderrFile = "$stem.stderr.txt"
        stdoutJson = $null; passed = $false
    }
    $receipt.steps.Add($step)
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = $agent
    $start.WorkingDirectory = $scratch
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardInput = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.StandardInputEncoding = $utf8
    $start.StandardOutputEncoding = $utf8
    $start.StandardErrorEncoding = $utf8
    foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $start
    $clock = [Diagnostics.Stopwatch]::StartNew()
    $started = $false
    $stdoutTask = $null
    $stderrTask = $null
    try {
        $started = $process.Start()
        if (-not $started) { throw "Could not start agent for $Name." }
        $stdoutTask = $process.StandardOutput.ReadToEndAsync()
        $stderrTask = $process.StandardError.ReadToEndAsync()
        try {
            $writeTask = $process.StandardInput.BaseStream.WriteAsync($inputBytes, 0, $inputBytes.Length)
            if (-not $writeTask.Wait(30000)) { $step.timedOut = $true; throw 'Agent stdin write timed out.' }
            $null = $writeTask.GetAwaiter().GetResult()
        }
        catch {
            # An input-limit rejection may close the pipe before every excess byte is accepted.
            $step.stdinWriteError = $_.Exception.GetBaseException().Message
            if ($step.timedOut) { throw }
        }
        finally { $process.StandardInput.Close() }
        $remaining = [Math]::Max(0, 30000 - [int]$clock.ElapsedMilliseconds)
        if (-not $process.WaitForExit($remaining)) { $step.timedOut = $true; throw "Agent process timed out: $Name" }
        $step.exitCode = $process.ExitCode
        $stdout = $stdoutTask.GetAwaiter().GetResult()
        $stderr = $stderrTask.GetAwaiter().GetResult()
        [IO.File]::WriteAllText((Join-Path $scratch $step.stdoutFile), $stdout, $utf8)
        [IO.File]::WriteAllText((Join-Path $scratch $step.stderrFile), $stderr, $utf8)
        $parsed = ConvertFrom-Json -InputObject $stdout -AsHashtable -Depth 100 -ErrorAction Stop
        if ($parsed -isnot [Collections.IDictionary]) { throw "Agent stdout must be one JSON object: $Name" }
        $step.stdoutJson = $parsed
        if ($step.exitCode -ne $ExpectedExit) { throw "Agent $Name returned $($step.exitCode); expected $ExpectedExit. See $($step.stdoutFile)." }
        if ($stderr.Length -ne 0) { throw "Unexpected stderr from $Name. See $($step.stderrFile)." }
        $step.passed = $true
        return $parsed
    }
    finally {
        $step.elapsedMilliseconds = $clock.ElapsedMilliseconds
        if ($started -and -not $process.HasExited) {
            $process.Kill($true)
            $null = $process.WaitForExit(5000)
        }
        $process.Dispose()
    }
}

try {
    $version = Invoke-Agent 'version' @('--version')
    Confirm-Check 'version identifies agent and stdin capability' ($version.name -eq 'sed-agent' -and 'stdin-json' -in $version.capabilities)
    $help = Invoke-Agent 'help' @('--help')
    Confirm-Check 'help discovers query command' ('query' -in @($help.commands.name))
    $commands = Invoke-Agent 'command-schema' @('schema', 'commands')
    Confirm-Check 'command schemas describe apply' ('apply' -in @($commands.commands.name))
    $querySchema = Invoke-Agent 'query-schema' @('schema', 'query')
    Confirm-Check 'query schema is an object' ($querySchema.type -eq 'object')
    $patchSchema = Invoke-Agent 'focused-schema' @('schema', 'setGravity')
    Confirm-Check 'focused patch schema is an object' ($patchSchema.type -eq 'object')

    $mots = Join-Path $scratch 'mots-original.jkl'
    $jk = Join-Path $scratch 'jk-original.jkl'
    $created = Invoke-Agent 'new-mots' @('new', $mots, 'mots')
    $createdJk = Invoke-Agent 'new-jk' @('new', $jk, 'jk')
    $originalHash = Get-Hash $mots
    Confirm-Check 'new MotS bytes match reported hash' ($originalHash -eq $created.inputSha256)
    Confirm-Check 'new JK bytes match reported hash' ((Get-Hash $jk) -eq $createdJk.inputSha256)
    $summaryRequest = Convert-Request @{ schemaVersion = 1; collection = 'summary'; offset = 0; limit = 1 }
    $summary = Invoke-Agent 'query-mots-summary' @('query', $mots, '-') $summaryRequest
    $summaryJk = Invoke-Agent 'query-jk-summary' @('query', $jk, '-') $summaryRequest
    Confirm-Check 'MotS scaffold reports correct game' ($summary.items[0].game -eq 'MysteriesOfTheSith')
    Confirm-Check 'JK scaffold reports correct game' ($summaryJk.items[0].game -eq 'JediKnight')
    Confirm-Check 'summary binds to input hash and source revision' ($summary.inputSha256 -eq $originalHash -and $summary.sourceRevision -eq $originalHash)
    $templates = Invoke-Agent 'query-templates' @('query', $mots, '-') (Convert-Request @{ schemaVersion = 1; collection = 'templates'; offset = 0; limit = 1 })
    Confirm-Check 'bundled templates are available' ($templates.total -gt 0 -and @($templates.items).Count -eq 1 -and $templates.items[0].name.Length -gt 0)
    if ($null -ne $templates.nextOffset) {
        $next = Invoke-Agent 'query-templates-next' @('query', $mots, '-') (Convert-Request @{ schemaVersion = 1; collection = 'templates'; offset = $templates.nextOffset; limit = 1 })
        Confirm-Check 'template pagination advances stable indices' ($next.items[0].index -gt $templates.items[0].index -and $next.total -eq $templates.total)
    }
    $gravity = 6.25
    $patch = Convert-Request @{ schemaVersion = 1; inputSha256 = $originalHash; sourceRevision = $summary.sourceRevision; operations = @(@{ op = 'setGravity'; value = $gravity }) }
    [IO.File]::WriteAllText((Join-Path $scratch 'reviewed-patch.json'), $patch, $utf8)
    $plan = Invoke-Agent 'plan-stdin' @('plan', $mots, '-') $patch
    Confirm-Check 'plan leaves input unchanged and reports output hash' ((Get-Hash $mots) -eq $originalHash -and $plan.outputSha256 -match '^[0-9a-f]{64}$')
    $wrongOutput = Join-Path $scratch 'wrong-hash-refused.jkl'
    $wrong = Invoke-Agent 'apply-wrong-reviewed-hash' @('apply', $mots, '-', $wrongOutput, ('0' * 64)) $patch 4
    Confirm-Check 'wrong reviewed hash is conflict without output' ($wrong.error -eq 'conflict' -and -not (Test-Path -LiteralPath $wrongOutput))
    $edited = Join-Path $scratch 'mots-edited.jkl'
    $applied = Invoke-Agent 'apply-reviewed-stdin' @('apply', $mots, '-', $edited, $plan.outputSha256) $patch
    $editedHash = Get-Hash $edited
    Confirm-Check 'written bytes equal reviewed plan' ($editedHash -eq $plan.outputSha256 -and $applied.plan.outputSha256 -eq $plan.outputSha256)
    $readback = Invoke-Agent 'query-edited-summary' @('query', $edited, '-') $summaryRequest
    Confirm-Check 'query reads back edited gravity and exact bytes' ($readback.items[0].header.gravity -eq $gravity -and $readback.inputSha256 -eq $editedHash)
    $overwrite = Invoke-Agent 'apply-no-overwrite' @('apply', $mots, '-', $edited, $plan.outputSha256) $patch 4
    Confirm-Check 'existing output is unchanged' ($overwrite.error -eq 'conflict' -and (Get-Hash $edited) -eq $editedHash)
    $invalidPatch = Convert-Request @{ schemaVersion = 1; inputSha256 = $originalHash; operations = @(@{ op = 'setGravity'; value = 8 }, @{ op = 'setGravity'; value = -1 }) }
    $invalid = Invoke-Agent 'invalid-second-operation' @('plan', $mots, '-') $invalidPatch 2
    Confirm-Check 'invalid operation identifies second operation' ($invalid.error -eq 'invalid_request' -and $invalid.details.operationIndex -eq 1 -and $invalid.details.operation -eq 'setGravity' -and $invalid.details.jsonPointer -like '/operations/1*')
    Confirm-Check 'failed plan preserves original input' ((Get-Hash $mots) -eq $originalHash)
    $oversized = Invoke-Agent 'oversized-stdin' @('plan', $mots, '-') (' ' * (1024 * 1024 + 1)) 2
    Confirm-Check 'stdin byte limit is enforced' ($oversized.error -eq 'invalid_request' -and $oversized.message -match '1 MiB')
    $unicodePatch = Convert-Request @{ schemaVersion = 1; inputSha256 = $originalHash; operations = @(@{ op = 'setThingTemplate'; thing = 0; template = ('caf' + [char]0xe9) }) }
    $unicode = Invoke-Agent 'unicode-token-stdin' @('plan', $mots, '-') $unicodePatch 2
    Confirm-Check 'UTF8 reaches token validation without character substitution' ($unicode.error -eq 'invalid_request' -and $unicode.message -match 'ASCII')

    $missing = Invoke-Agent 'launch-missing-required' @('launch-plan', '-') '{}' 2
    Confirm-Check 'launch missing required fields is invalid request' ($missing.error -eq 'invalid_request')
    $duplicate = Invoke-Agent 'launch-duplicate-field' @('launch-plan', '-') '{"game":"JediKnight","game":"MysteriesOfTheSith"}' 2
    Confirm-Check 'launch duplicate property is rejected' ($duplicate.error -eq 'invalid_request' -and $duplicate.message -match 'duplicate')
    $numeric = Invoke-Agent 'launch-numeric-enum' @('launch-plan', '-') (Convert-Request @{ game = 1; gameDirectory = (Join-Path $scratch 'absent-game'); projectDirectory = (Join-Path $scratch 'absent-project'); levelFileName = 'missing.jkl'; executable = 'missing.exe'; engine = 'Auto' }) 2
    Confirm-Check 'launch numeric enum is invalid request' ($numeric.error -eq 'invalid_request' -and $numeric.details.jsonPath -eq '$.game')
    $valid = Invoke-Agent 'validate-edited' @('validate', $edited)
    Confirm-Check 'edited scaffold passes structural validation' ([bool]$valid.valid)

    [IO.File]::AppendAllText($mots, "`n# workflow stale-input fixture`n", $utf8)
    $staleOutput = Join-Path $scratch 'stale-input-refused.jkl'
    $stale = Invoke-Agent 'apply-stale-source' @('apply', $mots, '-', $staleOutput, $plan.outputSha256) $patch 4
    Confirm-Check 'stale source is conflict without output' ($stale.error -eq 'conflict' -and -not (Test-Path -LiteralPath $staleOutput))
    $receipt.hashChecks = [ordered]@{ originalInputSha256 = $originalHash; reviewedOutputSha256 = $plan.outputSha256; writtenOutputSha256 = $editedHash; staleFixtureSha256 = (Get-Hash $mots) }
    $receipt.status = 'passed'
}
catch {
    $receipt.status = 'failed'
    $receipt.error = $_.Exception.Message
    throw
}
finally {
    $receipt.finishedUtc = [DateTime]::UtcNow.ToString('o')
    [IO.File]::WriteAllText((Join-Path $scratch 'receipt.json'), (ConvertTo-Json -InputObject $receipt -Depth 100), $utf8)
    Write-Host "Agent workflow $($receipt.status): $(Join-Path $scratch 'receipt.json')"
}
