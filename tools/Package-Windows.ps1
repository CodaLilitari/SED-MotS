#requires -Version 7.4
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$DotNet,
    [string]$Version = '0.4.0-mots.1-dev',
    [string]$OutputDirectory,
    [switch]$RunAgentSnapshots
)
$ErrorActionPreference = 'Stop'
if ($Version -notmatch '^\d+\.\d+\.\d+(?:-[a-zA-Z0-9.-]+)?$') { throw 'Version must be a SemVer-style version without spaces.' }
$repo = Split-Path -Parent $PSScriptRoot
$dotnetExe = (Resolve-Path -LiteralPath $DotNet).Path
$head = (& git -C $repo rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or $head -cnotmatch '^(?:[0-9a-f]{40}|[0-9a-f]{64})$') { throw 'Cannot identify the source commit.' }
function Assert-SourceUnchanged {
    $currentHead = & git -C $repo rev-parse HEAD
    if ($LASTEXITCODE -ne 0 -or $currentHead -cne $head) { throw 'Source commit changed during packaging.' }
    $status = @(& git -C $repo status --porcelain)
    if ($LASTEXITCODE -ne 0) { throw 'Cannot check source worktree status.' }
    if ($status.Count -ne 0) { throw 'Commit the reviewed source before packaging; the source archive must match the built files.' }
}
Assert-SourceUnchanged
if (!$OutputDirectory) { $OutputDirectory = Join-Path $repo ('build/packages/' + [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssZ')) }
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $output) { throw 'Choose a new output directory; package creation never replaces a previous handoff.' }
$null = New-Item -ItemType Directory -Path $output
$name = "SED-MotS-$Version-win-x64"
$package = Join-Path $output $name
$null = New-Item -ItemType Directory -Path $package

function Invoke-DotNet([string[]]$Arguments, [string]$LogName) {
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = $dotnetExe; $start.WorkingDirectory = $repo
    foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }
    $start.UseShellExecute = $false; $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true; $start.RedirectStandardError = $true
    $start.Environment['DOTNET_ROOT'] = Split-Path -Parent $dotnetExe
    $start.Environment['DOTNET_CLI_TELEMETRY_OPTOUT'] = '1'
    $start.Environment['DOTNET_GENERATE_ASPNET_CERTIFICATE'] = 'false'
    $process = [Diagnostics.Process]::Start($start)
    try {
        $stdout = $process.StandardOutput.ReadToEndAsync(); $stderr = $process.StandardError.ReadToEndAsync()
        $process.WaitForExit()
        $log = $stdout.GetAwaiter().GetResult() + $stderr.GetAwaiter().GetResult()
        [IO.File]::WriteAllText((Join-Path $output $LogName), $log)
        if ($process.ExitCode -ne 0) { throw "dotnet exited $($process.ExitCode); see $LogName" }
    } finally { $process.Dispose() }
}

& (Join-Path $PSScriptRoot 'Build-Net.ps1') -DotNet $dotnetExe
Invoke-DotNet @('publish','dotnet/src/Sed.App/Sed.App.csproj','-c','Release','-r','win-x64','--self-contained','true','-p:PublishTrimmed=false',"-p:Version=$Version",'-o',$package,'--nologo') 'publish-editor.log'
Invoke-DotNet @('publish','dotnet/tools/Sed.Agent/Sed.Agent.csproj','-c','Release','-r','win-x64','--self-contained','true',"-p:Version=$Version",'-o',(Join-Path $package 'agent'),'--nologo') 'publish-agent.log'
foreach ($required in @('Sed.App.exe','coreclr.dll','agent/sed-agent.exe','agent/coreclr.dll')) {
    if (!(Test-Path -LiteralPath (Join-Path $package $required) -PathType Leaf)) { throw "Missing published runtime component: $required" }
}
# A clean git status excludes ignored files. Copy the tracked support-file set only,
# so a local ignored asset or private scratch document cannot enter the handoff.
$supportFiles = @(& git -C $repo -c core.quotepath=false ls-files -- LICENSE README.md AGENTS.md .agents docs licenses data tools/Test-AgentWorkflow.ps1 tools/Test-AgentDesign.ps1 tools/Test-AgentLitSnapshot.ps1 tools/Test-AgentSourceLights.ps1)
if ($LASTEXITCODE -ne 0 -or $supportFiles.Count -eq 0) { throw 'Cannot enumerate tracked distribution support files.' }
foreach ($relative in $supportFiles) {
    if ($relative -match '["\r\n]' -or [IO.Path]::IsPathRooted($relative) -or $relative.Split('/') -contains '..') { throw "Unsupported tracked support-file path: $relative" }
    $sourceFile = Join-Path $repo $relative
    if ((Get-Item -LiteralPath $sourceFile).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Distribution support files cannot be symbolic links: $relative" }
    $destination = Join-Path $package $relative
    $null = New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force
    Copy-Item -LiteralPath $sourceFile -Destination $destination
    if ($relative.StartsWith('data/')) {
        $agentDestination = Join-Path $package ('agent/' + $relative)
        $null = New-Item -ItemType Directory -Path (Split-Path -Parent $agentDestination) -Force
        Copy-Item -LiteralPath $sourceFile -Destination $agentDestination
    }
}
Copy-Item -LiteralPath (Join-Path $repo 'docs/WINDOWS_QUICKSTART.md') -Destination (Join-Path $package 'START-HERE.md')

# Exercise the actual self-contained CLI without starting the GUI or a game.
$smoke = Join-Path $output 'agent-smoke'; $null = New-Item -ItemType Directory -Path $smoke
$agent = Join-Path $package 'agent/sed-agent.exe'
$schema = & $agent schema; if ($LASTEXITCODE -ne 0) { throw 'Packaged agent schema failed.' }
$schema | Set-Content -LiteralPath (Join-Path $smoke 'schema.json') -Encoding utf8
$smokeInput = Join-Path $smoke 'room.jkl'
& $agent new $smokeInput mots | Set-Content -LiteralPath (Join-Path $smoke 'new.json') -Encoding utf8
if ($LASTEXITCODE -ne 0) { throw 'Packaged agent room creation failed.' }
$inspection = & $agent inspect $smokeInput; if ($LASTEXITCODE -ne 0) { throw 'Packaged agent inspect failed.' }
$inspection | Set-Content -LiteralPath (Join-Path $smoke 'inspect.json') -Encoding utf8
$hash = ($inspection | ConvertFrom-Json).inputSha256
$patch = Join-Path $smoke 'patch.json'
@{ schemaVersion=1; inputSha256=$hash; operations=@(@{op='setGravity';value=6}) } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $patch -Encoding utf8
$edited = Join-Path $smoke 'edited.jkl'
& $agent apply $smokeInput $patch $edited | Set-Content -LiteralPath (Join-Path $smoke 'apply.json') -Encoding utf8
if ($LASTEXITCODE -ne 0) { throw 'Packaged agent apply failed.' }
& $agent validate $edited | Set-Content -LiteralPath (Join-Path $smoke 'validate.json') -Encoding utf8
if ($LASTEXITCODE -ne 0) { throw 'Packaged agent output failed validation.' }
& $agent apply $smokeInput $patch $edited | Set-Content -LiteralPath (Join-Path $smoke 'no-overwrite.json') -Encoding utf8
if ($LASTEXITCODE -ne 4) { throw 'Packaged agent did not refuse an existing output.' }
if ((Get-FileHash -LiteralPath $smokeInput -Algorithm SHA256).Hash.ToLowerInvariant() -ne $hash) { throw 'Packaged agent changed the source.' }

& (Join-Path $PSScriptRoot 'Test-AgentWorkflow.ps1') -AgentPath $agent -OutputDirectory (Join-Path $output 'agent-workflow')
& (Join-Path $PSScriptRoot 'Test-AgentSourceLights.ps1') -AgentPath $agent -OutputDirectory (Join-Path $output 'agent-source-lights')
& (Join-Path $PSScriptRoot 'Test-AgentDesign.ps1') -AgentPath $agent -OutputDirectory (Join-Path $output 'agent-design') -SkipSnapshot:(!$RunAgentSnapshots)
if ($RunAgentSnapshots) {
    & (Join-Path $PSScriptRoot 'Test-AgentLitSnapshot.ps1') -AgentPath $agent -OutputDirectory (Join-Path $output 'agent-lit-snapshot')
}

Assert-SourceUnchanged
$provenance = [ordered]@{ product='SED MotS .NET development build'; version=$Version; commit=$head;
    basedOn='https://github.com/davidagnome/SED/tree/908d53d70d6b95784a92e9f1746ab5b13afcfdf0';
    sourceArchive='SED-MotS-source.zip'; target='win-x64'; selfContained=$true;
    guiLaunched=$false; gameLaunched=$false; packagedAgentSmoke='passed'; packagedAgentWorkflow='passed';
    packagedAgentDesign='passed'; packagedAgentSourceLights='passed'; packagedAgentSnapshots=[bool]$RunAgentSnapshots;
    createdUtc=[DateTime]::UtcNow.ToString('o'); dotnetSha256=(Get-FileHash -LiteralPath $dotnetExe -Algorithm SHA256).Hash.ToLowerInvariant() }
$provenance | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $package 'PROVENANCE.json') -Encoding utf8
$manifest = Get-ChildItem -LiteralPath $package -Recurse -File | Sort-Object FullName | ForEach-Object {
    $relative=[IO.Path]::GetRelativePath($package,$_.FullName).Replace('\','/')
    '{0}  {1}' -f (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant(),$relative
}
$manifest | Set-Content -LiteralPath (Join-Path $package 'SHA256SUMS.txt') -Encoding utf8
Compress-Archive -LiteralPath $package -DestinationPath (Join-Path $output "$name.zip") -CompressionLevel Optimal
& git -C $repo archive --format=zip "--output=$(Join-Path $output 'SED-MotS-source.zip')" $head
if ($LASTEXITCODE -ne 0) { throw 'Corresponding source archive creation failed.' }
& git -C $repo bundle create (Join-Path $output 'SED-MotS-history.bundle') HEAD
if ($LASTEXITCODE -ne 0) { throw 'Source history bundle creation failed.' }
$bundleHeads = @(& git bundle list-heads (Join-Path $output 'SED-MotS-history.bundle'))
if ($LASTEXITCODE -ne 0 -or $bundleHeads.Count -ne 1 -or $bundleHeads[0] -cne "$head HEAD") { throw 'History bundle does not match the packaged HEAD commit.' }
Get-ChildItem -LiteralPath $output -File | Where-Object { $_.Extension -in '.zip','.bundle' } | Sort-Object Name | ForEach-Object {
    '{0}  {1}' -f (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant(), $_.Name
} | Set-Content -LiteralPath (Join-Path $output 'SHA256SUMS.txt') -Encoding utf8
Assert-SourceUnchanged
& (Join-Path $PSScriptRoot 'Verify-Distribution.ps1') -PackageDirectory $output
Assert-SourceUnchanged
Write-Output "Verified self-contained agent smoke, artifact manifests and matching source/history commit; Windows/source handoff: $output"
