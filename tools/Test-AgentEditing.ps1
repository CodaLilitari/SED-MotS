#requires -Version 7.4
<#
.SYNOPSIS
Exercises EDIT-01 discovery, guarded COG edits, diagnostics and texture alignment
through the actual agent process, using original synthetic inputs only.
#>
[CmdletBinding()]
param([Parameter(Mandatory)][string]$AgentPath, [Parameter(Mandatory)][string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$agent = (Resolve-Path -LiteralPath $AgentPath).Path
$scratch = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputDirectory)
if (Test-Path -LiteralPath $scratch) { throw 'Use a new output directory.' }
$null = New-Item -ItemType Directory -Path $scratch
$utf8 = [Text.UTF8Encoding]::new($false)
$receipt = [ordered]@{ status='running'; startedUtc=[DateTime]::UtcNow.ToString('o');
    agentPath=$agent; agentSha256=(Get-FileHash -LiteralPath $agent -Algorithm SHA256).Hash.ToLowerInvariant();
    steps=[Collections.Generic.List[object]]::new(); checks=[Collections.Generic.List[object]]::new();
    guiLaunched=$false; gameLaunched=$false; scope='Original synthetic agent-process contract, not gameplay or retail parity.' }
$receipt.runtimeFiles = @('sed-agent.exe','sed-agent.dll','Sed.Core.dll','Sed.Formats.dll','Sed.Rendering.dll','Sed.Rendering.Vulkan.dll') | ForEach-Object {
    $runtimePath = Join-Path (Split-Path -Parent $agent) $_
    if (!(Test-Path -LiteralPath $runtimePath -PathType Leaf)) { throw "Missing expected agent runtime file: $runtimePath" }
    @{name=$_;sha256=(Get-FileHash -LiteralPath $runtimePath -Algorithm SHA256).Hash.ToLowerInvariant()}
}
function Hash([string]$Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() }
function Check([string]$Name, [bool]$Pass) {
    $receipt.checks.Add(@{name=$Name;passed=$Pass})
    if (!$Pass) { throw "Assertion failed: $Name" }
}
function Invoke-Agent([string]$Name, [string[]]$Arguments, $Request=$null, [int]$ExpectedExit=0) {
    $stem = '{0:D2}-{1}' -f ($receipt.steps.Count+1),$Name
    $inputText = if ($null -eq $Request) { '' } else { ConvertTo-Json -InputObject $Request -Depth 40 -Compress }
    [IO.File]::WriteAllText((Join-Path $scratch "$stem.request.json"), $inputText, $utf8)
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName=$agent; $start.WorkingDirectory=$scratch; $start.UseShellExecute=$false; $start.CreateNoWindow=$true
    $start.RedirectStandardInput=$true; $start.RedirectStandardOutput=$true; $start.RedirectStandardError=$true
    foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }
    $process=[Diagnostics.Process]::Start($start)
    try {
        $outTask=$process.StandardOutput.ReadToEndAsync(); $errTask=$process.StandardError.ReadToEndAsync()
        $process.StandardInput.Write($inputText); $process.StandardInput.Close()
        if (!$process.WaitForExit(60000)) { $process.Kill($true); throw "Agent timed out: $Name" }
        $stdout=$outTask.GetAwaiter().GetResult(); $stderr=$errTask.GetAwaiter().GetResult()
        [IO.File]::WriteAllText((Join-Path $scratch "$stem.stdout.json"),$stdout,$utf8)
        [IO.File]::WriteAllText((Join-Path $scratch "$stem.stderr.txt"),$stderr,$utf8)
        $receipt.steps.Add(@{name=$Name;arguments=$Arguments;exitCode=$process.ExitCode;expectedExit=$ExpectedExit;stdoutFile="$stem.stdout.json"})
        Check "$Name exit" ($process.ExitCode -eq $ExpectedExit)
        return ConvertFrom-Json -InputObject $stdout -Depth 100
    } finally { $process.Dispose() }
}
function Write-CogArchive([string]$Path,[string]$Script) {
    # Original one-entry GOB fixture, following its documented little-endian table.
    $bytes=[Text.Encoding]::ASCII.GetBytes($Script)
    $stream=[IO.File]::Open($Path,[IO.FileMode]::CreateNew)
    $writer=[IO.BinaryWriter]::new($stream)
    try {
        $writer.Write([Text.Encoding]::ASCII.GetBytes('GOB ')); $writer.Write([uint32]0x14); $writer.Write([uint32](12+$bytes.Length))
        $writer.Write($bytes); $writer.Write([uint32]1); $writer.Write([uint32]12); $writer.Write([uint32]$bytes.Length)
        $name=[byte[]]::new(128); [Text.Encoding]::ASCII.GetBytes('cog\original.cog').CopyTo($name,0); $writer.Write($name)
    } finally { $writer.Dispose(); $stream.Dispose() }
}
try {
    $version=Invoke-Agent 'version' @('--version')
    foreach ($capability in @('typed-cog-bindings','guarded-cog-rebinding','world-bake-diagnostics','direct-asset-diagnostics','neighbor-texture-alignment','architecture-world-occluded-bake')) {
        Check "capability $capability" ($version.capabilities -contains $capability)
    }
    foreach ($schema in @('bindings','lighting','assets','alignSurfaceTexture','rebindCogReference')) { $null=Invoke-Agent "schema-$schema" @('schema',$schema) }
    $room=Join-Path $scratch 'room.jkl'
    $null=Invoke-Agent 'new' @('new',$room,'mots')
    $roomHash=Hash $room
    # Fixture preparation adds one original COG section to an otherwise agent-created scaffold.
    $source=Join-Path $scratch 'fixture.jkl'
    $roomText=[IO.File]::ReadAllText($room)
    if ($roomText -match '(?im)^SECTION:\s*COGS') { throw 'Unexpected pre-existing COG section in scaffold.' }
    [IO.File]::WriteAllText($source,$roomText+"`nSECTION: COGS`n`nWorld cogs 1`n0: original.cog 0 -1 1.25`nend`n",$utf8)
    $script="symbols`nsector trigger=0 linkid=3`nthing receiver=-1 nolink`nflex speed=1`nmessage startup`nend`ncode`nstartup:`nreturn;`nend`n"
    $archive=Join-Path $scratch 'original.goo'; Write-CogArchive $archive $script
    $archives=@(@{path=$archive;sha256=(Hash $archive)})
    $initial=Hash $source
    $query=Invoke-Agent 'cogs' @('query',$source,'-') @{schemaVersion=1;collection='cogs'}
    Check 'one authored COG' ($query.total -eq 1 -and $query.items[0].values.Count -eq 3)
    $bindingRequest=@{schemaVersion=1;inputSha256=$initial;resourceArchives=$archives;cog=0;limit=2}
    $bindings=Invoke-Agent 'bindings' @('bindings',$source,'-') $bindingRequest
    Check 'typed schema and next page' ($bindings.schemaComplete -and $bindings.parameterCountMatches -and $bindings.nextOffset -eq 2)
    Check 'event candidates distinguished' ($bindings.rows[0].eventLinkCandidate -and !$bindings.rows[1].eventLinkCandidate)
    $bindingRequest.offset=2
    $last=Invoke-Agent 'bindings-page2' @('bindings',$source,'-') $bindingRequest
    Check 'stable parameter index' ($last.rows[0].parameterIndex -eq 2 -and $null -eq $last.nextOffset)
    $patch=@{schemaVersion=1;inputSha256=$initial;resourceArchives=$archives;operations=@(
        @{op='rebindCogReference';cog=0;parameter=0;symbol='trigger';kind='sector';targetIndex=-1;scriptSha256=$bindings.scriptSha256})}
    $plan=Invoke-Agent 'plan-cog' @('plan',$source,'-') $patch
    Check 'planning leaves original unchanged' ((Hash $source) -eq $initial)
    $patch.resourceRevision=$plan.resourceRevision
    $rebound=Join-Path $scratch 'rebound.jkl'
    $null=Invoke-Agent 'apply-cog' @('apply',$source,'-',$rebound,$plan.outputSha256) $patch
    $readback=Invoke-Agent 'cog-readback' @('query',$rebound,'-') @{schemaVersion=1;collection='cogs'}
    Check 'only target changed' (($readback.items[0].values -join ',') -eq '-1,-1,1.25')
    $patch.operations[0].scriptSha256='0'*64
    $rejected=Join-Path $scratch 'refused.jkl'
    $null=Invoke-Agent 'stale-script-refusal' @('apply',$source,'-',$rejected) $patch 4
    Check 'refused output absent' (!(Test-Path -LiteralPath $rejected))
    $assetRequest=@{schemaVersion=1;inputSha256=$initial;resourceArchives=$archives;limit=200}
    $assets=Invoke-Agent 'assets' @('assets',$source,'-') $assetRequest
    Check 'direct assets with bounded claim' ($assets.directScanComplete -and !$assets.recursiveClosureVerified)
    Check 'COG asset captured' (@($assets.rows | Where-Object {$_.origin.kind -eq 'placedCog' -and $_.status -eq 'resolved'}).Count -eq 1)
    $null=Invoke-Agent 'containment' @('query',$source,'-') @{schemaVersion=1;collection='thingContainment';limit=1}
    $withLight=Join-Path $scratch 'light.jkl'
    $lightPatch=@{schemaVersion=1;inputSha256=$initial;operations=@(@{op='addLight';position=@(0,0,0);rgb=@(1,0.5,0.25);intensity=1;range=8})}
    $null=Invoke-Agent 'add-source-light' @('apply',$source,'-',$withLight) $lightPatch
    $lighting=Invoke-Agent 'lighting' @('lighting',$withLight,'-') @{schemaVersion=1;sector=0;limit=1;castShadows=$true}
    Check 'light diagnostics use actual corner counts' ($lighting.items[0].samples.candidate -eq 24 -and $lighting.items[0].samples.eligible -gt 0)
    $texturePatch=@{schemaVersion=1;inputSha256=$initial;operations=@(
        @{op='fitSurfaceTexture';sector=0;surface=0;width=64;height=64},
        @{op='alignSurfaceTexture';sector=0;surface=2;otherSector=0;otherSurface=0})}
    $texturePlan=Invoke-Agent 'plan-alignment' @('plan',$source,'-') $texturePatch
    $aligned=Join-Path $scratch 'aligned.jkl'
    $null=Invoke-Agent 'apply-alignment' @('apply',$source,'-',$aligned,$texturePlan.outputSha256) $texturePatch
    $null=Invoke-Agent 'alignment-readback' @('query',$aligned,'-') @{schemaVersion=1;collection='surfaces';sector=0;offset=2;limit=1}
    Check 'all original input bytes preserved' ((Hash $source) -eq $initial -and (Hash $room) -eq $roomHash)
    $receipt.status='passed'
} catch { $receipt.status='failed'; $receipt.error=$_.Exception.ToString(); throw }
finally {
    $receipt.finishedUtc=[DateTime]::UtcNow.ToString('o')
    [IO.File]::WriteAllText((Join-Path $scratch 'receipt.json'),(ConvertTo-Json -InputObject $receipt -Depth 40),$utf8)
}
