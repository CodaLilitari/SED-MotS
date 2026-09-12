#requires -Version 7.2
<#
.SYNOPSIS
Verifies packaged source-light conversion and reviewed agent edits without graphics.
.DESCRIPTION
Uses one original synthetic native fixture, new JKL outputs, and direct sed-agent
processes. Records exact requests, replies and hashes. No native writer, retail
parity, baking, GUI, game, shell child, Vulkan device or external assets are tested.
#>
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
if (Test-Path -LiteralPath $scratch) { throw 'OutputDirectory must be fresh; existing paths are never reused.' }
if (-not (Test-Path -LiteralPath (Split-Path -Parent $scratch) -PathType Container)) { throw 'The output parent must exist.' }
$null = New-Item -ItemType Directory -Path $scratch -ErrorAction Stop
$receipt = [ordered]@{
    schemaVersion = 1; startedUtc = [DateTime]::UtcNow.ToString('o'); status = 'running'
    agentPath = $agent; agentSha256 = (Get-FileHash -LiteralPath $agent -Algorithm SHA256).Hash.ToLowerInvariant()
    outputDirectory = $scratch; processTimeoutSeconds = 30
    guiLaunched = $false; gameLaunched = $false; shellChildLaunched = $false; graphicsRequested = $false
    processBoundary = 'Only the supplied sed-agent executable, ArgumentList, redirected streams, UseShellExecute=false, CreateNoWindow=true.'
    limitations = @(
        'Synthetic source-light metadata and transaction checks only; no baking, graphics, gameplay or retail parity.',
        'Output is JKL plus SED_SOURCE_LIGHTS editor metadata; no native JED/SED/NED writer is claimed.',
        'Conversion has no standalone caller sourceRevision argument. An identity edit plans the conversion with sourceRevision; conversion captures and rechecks its inputs internally, then its returned revision and bytes are compared with that reviewed plan.'
    )
    steps = [Collections.Generic.List[object]]::new(); checks = [Collections.Generic.List[object]]::new()
    artifacts = [Collections.Generic.List[object]]::new()
}
function Get-Hash([string]$Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() }
function Convert-Request($Value) { ConvertTo-Json -InputObject $Value -Depth 50 -Compress }
function Confirm-Check([string]$Name, [bool]$Condition) {
    $receipt.checks.Add([ordered]@{ name = $Name; passed = $Condition })
    if (-not $Condition) { throw "Source-light assertion failed: $Name" }
}
function Invoke-Agent {
    param([string]$Name, [string[]]$Arguments, [AllowEmptyString()][string]$InputText = '', [int]$ExpectedExit = 0)
    if ($Arguments[0] -notin @('--version', 'schema', 'inspect', 'query', 'plan', 'apply', 'convert', 'validate')) {
        throw 'Source-light workflow only permits document and discovery commands.'
    }
    $stem = '{0:D2}-{1}' -f ($receipt.steps.Count + 1), $Name
    $bytes = $utf8.GetBytes($InputText)
    $step = [ordered]@{
        name = $Name; executable = $agent; arguments = $Arguments
        stdinFile = "$stem.request.json"; stdinBytes = $bytes.Length
        stdinSha256 = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant()
        stdoutFile = "$stem.stdout.json"; stderrFile = "$stem.stderr.txt"
        expectedExitCode = $ExpectedExit; exitCode = $null; timedOut = $false; passed = $false
    }
    [IO.File]::WriteAllBytes((Join-Path $scratch $step.stdinFile), $bytes)
    $receipt.steps.Add($step)
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = $agent; $start.WorkingDirectory = $scratch
    $start.UseShellExecute = $false; $start.CreateNoWindow = $true
    $start.RedirectStandardInput = $true; $start.RedirectStandardOutput = $true; $start.RedirectStandardError = $true
    $start.StandardInputEncoding = $utf8; $start.StandardOutputEncoding = $utf8; $start.StandardErrorEncoding = $utf8
    foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::new(); $process.StartInfo = $start
    $clock = [Diagnostics.Stopwatch]::StartNew(); $started = $false; $stdoutTask = $null; $stderrTask = $null
    try {
        $started = $process.Start()
        if (-not $started) { throw "Could not start agent: $Name" }
        $step.processId = $process.Id
        $stdoutTask = $process.StandardOutput.ReadToEndAsync(); $stderrTask = $process.StandardError.ReadToEndAsync()
        try {
            $write = $process.StandardInput.BaseStream.WriteAsync($bytes, 0, $bytes.Length)
            if (-not $write.Wait([Math]::Max(0, 30000 - [int]$clock.ElapsedMilliseconds))) {
                $step.timedOut = $true; throw 'Agent stdin write timed out.'
            }
            $null = $write.GetAwaiter().GetResult()
        } finally { $process.StandardInput.Close() }
        if (-not $process.WaitForExit([Math]::Max(0, 30000 - [int]$clock.ElapsedMilliseconds))) {
            $step.timedOut = $true; throw "Agent process timed out: $Name"
        }
        $step.exitCode = $process.ExitCode
        $stdout = $stdoutTask.GetAwaiter().GetResult(); $stderr = $stderrTask.GetAwaiter().GetResult()
        [IO.File]::WriteAllText((Join-Path $scratch $step.stdoutFile), $stdout, $utf8)
        [IO.File]::WriteAllText((Join-Path $scratch $step.stderrFile), $stderr, $utf8)
        $parsed = ConvertFrom-Json -InputObject $stdout -AsHashtable -Depth 100 -ErrorAction Stop
        if ($parsed -isnot [Collections.IDictionary]) { throw "Expected one JSON response: $Name" }
        if ($step.exitCode -ne $ExpectedExit) { throw "Agent $Name exited $($step.exitCode), expected $ExpectedExit; see $($step.stdoutFile)." }
        if ($stderr.Length -ne 0) { throw "Unexpected agent stderr: $Name" }
        $step.passed = $true
        return $parsed
    } finally {
        if ($started -and -not $process.HasExited) { $process.Kill($true); $null = $process.WaitForExit(5000) }
        if ($null -ne $stdoutTask -and $stdoutTask.IsCompletedSuccessfully) {
            [IO.File]::WriteAllText((Join-Path $scratch $step.stdoutFile), $stdoutTask.GetAwaiter().GetResult(), $utf8)
        }
        if ($null -ne $stderrTask -and $stderrTask.IsCompletedSuccessfully) {
            [IO.File]::WriteAllText((Join-Path $scratch $step.stderrFile), $stderrTask.GetAwaiter().GetResult(), $utf8)
        }
        foreach ($stream in @('stdout', 'stderr')) {
            $path = Join-Path $scratch $step["${stream}File"]
            if (Test-Path -LiteralPath $path -PathType Leaf) { $step["${stream}Sha256"] = Get-Hash $path }
        }
        $step.elapsedMilliseconds = $clock.ElapsedMilliseconds; $process.Dispose()
    }
}
function Read-Lights([string]$Name, [string]$Path) {
    $query = Invoke-Agent $Name @('query', $Path, '-') (Convert-Request @{ schemaVersion = 1; collection = 'lights'; limit = 10 })
    Confirm-Check "$Name is complete and hash-bound" ($null -eq $query.nextOffset -and $query.inputSha256 -eq (Get-Hash $Path))
    return $query
}
function New-Patch($Source, [object[]]$Operations) {
    Convert-Request @{ schemaVersion = 1; inputSha256 = $Source.inputSha256; sourceRevision = $Source.sourceRevision; operations = $Operations }
}
function Commit-Edit([string]$Name, [string]$InputPath, $Source, [object[]]$Operations) {
    $patch = New-Patch $Source $Operations
    $plan = Invoke-Agent "$Name-plan" @('plan', $InputPath, '-') $patch
    Confirm-Check "$Name planning preserves input" ((Get-Hash $InputPath) -eq $Source.inputSha256)
    $output = Join-Path $scratch "$Name.jkl"
    $apply = Invoke-Agent "$Name-apply" @('apply', $InputPath, '-', $output, $plan.outputSha256) $patch
    Confirm-Check "$Name bytes match reviewed plan" ((Get-Hash $output) -eq $plan.outputSha256 -and $apply.plan.outputSha256 -eq $plan.outputSha256)
    return @{ path = $output; query = (Read-Lights "$Name-readback" $output); plan = $plan; patch = $patch }
}
function Confirm-Light([string]$Name, $Light, [double]$White, $RgbIntensity, $LegacyRange, [long]$Flags, [int]$Layer) {
    $effective = if ($null -eq $RgbIntensity) { $White } else { $RgbIntensity }
    Confirm-Check $Name ($Light.intensity -eq $White -and $Light.rgbIntensity -eq $RgbIntensity -and
        $Light.effectiveRgbIntensity -eq $effective -and $Light.rgbIntensityInherited -eq ($null -eq $RgbIntensity) -and
        $Light.legacyRgbRange -eq $LegacyRange -and $Light.flags -eq $Flags -and $Light.layer -eq $Layer)
}

try {
    $version = Invoke-Agent 'version' @('--version')
    Confirm-Check 'agent exposes stdin and reviewed-output capability' ($version.name -eq 'sed-agent' -and 'stdin-json' -in $version.capabilities -and 'reviewed-output-hash' -in $version.capabilities)
    $receipt.agentVersion = $version.version
    $null = Invoke-Agent 'command-schema' @('schema', 'commands')
    $null = Invoke-Agent 'query-schema' @('schema', 'query')
    $setSchema = Invoke-Agent 'set-light-schema' @('schema', 'setLight')
    $setVariant = $setSchema.properties.operations.items.oneOf[0]
    Confirm-Check 'setLight schema requires a selector and at least one field' ($setVariant.minProperties -eq 3 -and 'light' -in $setVariant.required -and 'rgbIntensity' -in $setVariant.properties.Keys)
    $addSchema = Invoke-Agent 'add-light-schema' @('schema', 'addLight')
    $addVariant = $addSchema.properties.operations.items.oneOf[0]
    Confirm-Check 'legacy addLight fields remain sufficient' (@($addVariant.required).Count -eq 5 -and 'rgbIntensity' -notin $addVariant.required -and 'legacyRgbRange' -notin $addVariant.required)

    $native = Join-Path $scratch 'synthetic-native.jed'
    $fixture = @'
JED 0.95
SEC: MOTS
END
SEC: HEADER
HDR 1 4 15 2 3 - 320
HOR 100 768 0 0
CEIL 0 0
MIP 1 2 3 4
LOD 0.1 0.2 0.3 0.4
END
SEC: LIGHTS
LTS 1
1 2 3 1 0.25 8 80000001 99 1.5 0.25 0.5 0.75
END
SEC: LAYERS
LAYERS 2
Main
Accent
END
'@
    [IO.File]::WriteAllText($native, $fixture, $utf8)
    $nativeHash = Get-Hash $native
    $inspection = Invoke-Agent 'inspect-native' @('inspect', $native)
    $source = Read-Lights 'query-native-lights' $native
    Confirm-Check 'native input reports absent JKL extension without a provenance claim' ($inspection.sourceLightMetadata.state -eq 'absent' -and $null -eq $source.sourceLightMetadata.binding)
    Confirm-Check 'native query matches inspection and captures template dependencies' ($inspection.sourceRevision -eq $source.sourceRevision -and $source.sourceRevision -ne $source.inputSha256 -and @($inspection.dependencies).Count -eq 2)
    Confirm-Light 'native import retains independent channels and metadata' $source.items[0] .25 1.5 99 2147483649 1
    $identity = @(@{ op = 'setLight'; light = 0; position = @(1, 2, 3) })
    $missingRevision = Convert-Request @{ schemaVersion = 1; inputSha256 = $nativeHash; operations = $identity }
    $missing = Invoke-Agent 'native-revision-required' @('plan', $native, '-') $missingRevision 2
    Confirm-Check 'native edits reject missing source revision' ($missing.error -eq 'invalid_request' -and $missing.details.jsonPointer -eq '/sourceRevision')
    $nativePatch = New-Patch $source $identity
    $nativePlan = Invoke-Agent 'native-identity-conversion-plan' @('plan', $native, '-') $nativePatch
    $converted = Join-Path $scratch 'converted.jkl'
    $conversion = Invoke-Agent 'convert-native' @('convert', $native, $converted)
    Confirm-Check 'conversion agrees with reviewed source and output' ($conversion.plan.sourceRevision -eq $source.sourceRevision -and $conversion.plan.outputSha256 -eq $nativePlan.outputSha256 -and (Get-Hash $converted) -eq $nativePlan.outputSha256)
    Confirm-Check 'native conversion preserves source bytes' ((Get-Hash $native) -eq $nativeHash)
    $convertedQuery = Read-Lights 'query-converted' $converted
    Confirm-Check 'converted metadata validates indexed ordinary rows' ($conversion.plan.outputSourceLightMetadata.state -eq 'validated' -and $convertedQuery.sourceLightMetadata.state -eq 'validated' -and $convertedQuery.sourceLightMetadata.binding -eq 'ordinary-lights-by-index')
    Confirm-Light 'conversion readback preserves all controls' $convertedQuery.items[0] .25 1.5 99 2147483649 1

    $metadata = Commit-Edit 'metadata-only' $converted $convertedQuery @(@{ op = 'setLight'; light = 0; legacyRgbRange = -24; flags = 5; layer = 0 })
    Confirm-Light 'metadata edit preserves both intensities' $metadata.query.items[0] .25 1.5 -24 5 0
    $independent = Commit-Edit 'independent-intensities' $metadata.path $metadata.query @(@{ op = 'setLight'; light = 0; intensity = .75; rgbIntensity = 2.5 })
    Confirm-Light 'independent intensity edit survives apply' $independent.query.items[0] .75 2.5 -24 5 0
    $unrelated = Commit-Edit 'unrelated-gravity' $independent.path $independent.query @(@{ op = 'setGravity'; value = 6 })
    Confirm-Light 'unrelated edit preserves source light' $unrelated.query.items[0] .75 2.5 -24 5 0
    $legacy = Commit-Edit 'legacy-add-light' $unrelated.path $unrelated.query @(@{ op = 'addLight'; position = @(0, 0, 0); rgb = @(1, 1, 1); intensity = 2; range = 4 })
    Confirm-Check 'ordinary legacy add appends one light' (@($legacy.query.items).Count -eq 2)
    Confirm-Light 'legacy add inherits white intensity' $legacy.query.items[1] 2 $null $null 0 0
    $zero = Commit-Edit 'explicit-zero-and-equal' $legacy.path $legacy.query @(
        @{ op = 'setLight'; light = 0; rgbIntensity = 0 },
        @{ op = 'setLight'; light = 1; rgbIntensity = 2 },
        @{ op = 'setLight'; light = 1; intensity = 5 })
    Confirm-Light 'zero remains independent of white' $zero.query.items[0] .75 0 -24 5 0
    Confirm-Light 'explicit equal value does not become inheritance' $zero.query.items[1] 5 2 $null 0 0
    $cleared = Commit-Edit 'restore-inheritance' $zero.path $zero.query @(@{ op = 'setLight'; light = 0; rgbIntensity = $null; legacyRgbRange = $null; intensity = 3 })
    Confirm-Light 'null restores inheritance and clears metadata' $cleared.query.items[0] 3 $null $null 5 0
    Confirm-Light 'other light stays unchanged when clearing metadata' $cleared.query.items[1] 5 2 $null 0 0
    Confirm-Check 'color and primary range survive every operation' ($cleared.query.items[0].rgb[0] -eq .25 -and $cleared.query.items[0].rgb[1] -eq .5 -and $cleared.query.items[0].rgb[2] -eq .75 -and $cleared.query.items[0].range -eq 8)
    $validated = Invoke-Agent 'validate-final' @('validate', $cleared.path)
    Confirm-Check 'final structural validation passes' ([bool]$validated.valid)

    $overwrite = Invoke-Agent 'refuse-original-output' @('apply', $converted, '-', $converted, $metadata.plan.outputSha256) $metadata.patch 4
    Confirm-Check 'original output cannot be overwritten' ($overwrite.error -eq 'conflict' -and (Get-Hash $converted) -eq $convertedQuery.inputSha256)
    $existing = Invoke-Agent 'refuse-existing-output' @('apply', $converted, '-', $metadata.path, $metadata.plan.outputSha256) $metadata.patch 4
    Confirm-Check 'existing reviewed output cannot be overwritten' ($existing.error -eq 'conflict' -and (Get-Hash $metadata.path) -eq $metadata.plan.outputSha256)
    $wrongPath = Join-Path $scratch 'wrong-reviewed-hash-refused.jkl'
    $wrong = Invoke-Agent 'refuse-wrong-reviewed-hash' @('apply', $converted, '-', $wrongPath, ('0' * 64)) $metadata.patch 4
    Confirm-Check 'wrong reviewed hash produces no output' ($wrong.error -eq 'conflict' -and -not (Test-Path -LiteralPath $wrongPath))
    # The companion is synthetic and intentionally appears only after the reviewed native plan.
    [IO.File]::WriteAllText((Join-Path $scratch 'mots.tpl'), "source_light_fixture none type=ghost`n", $utf8)
    $stalePath = Join-Path $scratch 'stale-native-refused.jkl'
    $stale = Invoke-Agent 'refuse-stale-native-dependency' @('apply', $native, '-', $stalePath, $nativePlan.outputSha256) $nativePatch 4
    Confirm-Check 'native dependency revision refuses stale apply' ($stale.error -eq 'conflict' -and -not (Test-Path -LiteralPath $stalePath) -and (Get-Hash $native) -eq $nativeHash)
    Confirm-Check 'agent executable remained unchanged' ((Get-Hash $agent) -eq $receipt.agentSha256)
    $receipt.status = 'passed'
} catch {
    $receipt.status = 'failed'; $receipt.error = $_.Exception.Message
    throw
} finally {
    # Only files created inside this new synthetic workflow directory are enumerated.
    foreach ($file in Get-ChildItem -LiteralPath $scratch -File | Sort-Object Name) {
        $receipt.artifacts.Add([ordered]@{ path = $file.Name; bytes = $file.Length; sha256 = (Get-Hash $file.FullName) })
    }
    $receipt.finishedUtc = [DateTime]::UtcNow.ToString('o')
    [IO.File]::WriteAllText((Join-Path $scratch 'receipt.json'), (ConvertTo-Json -InputObject $receipt -Depth 100), $utf8)
    Write-Host "Agent source-light workflow $($receipt.status): $(Join-Path $scratch 'receipt.json')"
}
