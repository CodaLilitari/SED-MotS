#requires -Version 7.4
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$DotNet,
    [string]$Python = 'python',
    [string]$Version = '0.4.0-mots.1-dev',
    [string]$OutputDirectory,
    [string]$ArtifactsPath,
    [switch]$RunAgentSnapshots
)
$ErrorActionPreference = 'Stop'
if ($Version -notmatch '^\d+\.\d+\.\d+(?:-[a-zA-Z0-9.-]+)?$') { throw 'Version must be a SemVer-style version without spaces.' }
$repo = Split-Path -Parent $PSScriptRoot
$dotnetExe = (Resolve-Path -LiteralPath $DotNet).Path
if (Test-Path -LiteralPath $Python -PathType Leaf) { $pythonExe = (Resolve-Path -LiteralPath $Python).Path }
else {
    $pythonCommand = Get-Command $Python -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if (!$pythonCommand) { throw 'Supply -Python with a Python 3.10+ interpreter for the packaged stdio MCP workflow.' }
    $pythonExe = $pythonCommand.Source
}
function Invoke-Git([string[]]$Arguments, [int]$TimeoutSeconds = 30) {
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = 'git'; $start.WorkingDirectory = $repo
    foreach ($argument in (@('--no-optional-locks', '-C', $repo) + $Arguments)) { $start.ArgumentList.Add($argument) }
    $start.UseShellExecute = $false; $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true; $start.RedirectStandardError = $true
    $process = [Diagnostics.Process]::Start($start)
    try {
        $stdout = $process.StandardOutput.ReadToEndAsync(); $stderr = $process.StandardError.ReadToEndAsync()
        if (!$process.WaitForExit($TimeoutSeconds * 1000)) {
            # This exact owned process is an archive/read-only check, never a Git write to the repository.
            $process.Kill($true); $process.WaitForExit()
            throw "Owned packaging Git command timed out after $TimeoutSeconds seconds: $($Arguments[0]). No retry was started."
        }
        $result = $stdout.GetAwaiter().GetResult(); $errorText = $stderr.GetAwaiter().GetResult()
        if ($process.ExitCode -ne 0) { throw "Packaging Git command failed ($($process.ExitCode)): $errorText" }
        if ($result.Length) { return $result.TrimEnd("`r", "`n") -split '\r?\n' }
    } finally { $process.Dispose() }
}
function Assert-ExternalOutput([string]$Path) {
    $absolute = [IO.Path]::GetFullPath($Path)
    $ancestor = $absolute
    while ($ancestor) {
        if (Test-Path -LiteralPath (Join-Path $ancestor '.git')) { throw "Package/build output must be outside Git worktrees: $absolute" }
        if ((Test-Path -LiteralPath $ancestor) -and ((Get-Item -LiteralPath $ancestor).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw "Package/build output cannot pass through symbolic links or junctions: $absolute"
        }
        $ancestor = Split-Path -Parent $ancestor
    }
    return $absolute
}
function Wait-OwnedProcess([Diagnostics.Process]$Process, [int]$TimeoutSeconds = 1800) {
    if ($Process.WaitForExit($TimeoutSeconds * 1000)) { return $true }
    $Process.Kill($true); $Process.WaitForExit()
    return $false
}
function Invoke-AgentSmoke([string[]]$Arguments, [string]$OutputName, [int]$ExpectedExit = 0, [int]$TimeoutSeconds = 30) {
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = $agent; $start.WorkingDirectory = $smoke
    foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }
    $start.UseShellExecute = $false; $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true; $start.RedirectStandardError = $true
    $process = [Diagnostics.Process]::Start($start)
    try {
        $stdout = $process.StandardOutput.ReadToEndAsync(); $stderr = $process.StandardError.ReadToEndAsync()
        $completed = Wait-OwnedProcess $process $TimeoutSeconds
        $result = $stdout.GetAwaiter().GetResult()
        [IO.File]::WriteAllText((Join-Path $smoke $OutputName), $result)
        [IO.File]::WriteAllText((Join-Path $smoke ($OutputName + '.stderr.log')), $stderr.GetAwaiter().GetResult())
        if (!$completed) { throw "Packaged agent smoke timed out after $TimeoutSeconds seconds; see $OutputName." }
        if ($process.ExitCode -ne $ExpectedExit) { throw "Packaged agent smoke returned $($process.ExitCode), expected $ExpectedExit; see $OutputName." }
        return $result
    } finally { $process.Dispose() }
}
function Assert-SnapshotPath([string]$Relative) {
    if (!$Relative -or $Relative -match '[<>:"\\|?*\x00-\x1f]' -or $Relative.StartsWith('/')) { throw "Unsafe source archive path: $Relative" }
    foreach ($part in $Relative.TrimEnd('/').Split('/')) {
        if (!$part -or $part -in '.', '..' -or $part -match '[ .]$' -or $part -match '^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\.|$)') {
            throw "Unsafe Windows source path: $Relative"
        }
    }
}
function Get-SourceBlobHash([string]$Path, [string]$ExpectedObject) {
    $algorithm = if ($ExpectedObject.Length -eq 64) { [Security.Cryptography.HashAlgorithmName]::SHA256 } else { [Security.Cryptography.HashAlgorithmName]::SHA1 }
    $hash = [Security.Cryptography.IncrementalHash]::CreateHash($algorithm)
    $stream = [IO.File]::OpenRead($Path)
    try {
        $hash.AppendData([Text.Encoding]::UTF8.GetBytes("blob $($stream.Length)`0"))
        $buffer = [byte[]]::new(81920)
        while (($count = $stream.Read($buffer, 0, $buffer.Length)) -gt 0) { $hash.AppendData($buffer, 0, $count) }
        return [Convert]::ToHexString($hash.GetHashAndReset()).ToLowerInvariant()
    } finally { $stream.Dispose(); $hash.Dispose() }
}
function Assert-SourceSnapshot([string]$Directory, [Collections.Generic.Dictionary[string,string]]$Files) {
    foreach ($relative in $Files.Keys) {
        $path = Join-Path $Directory $relative
        if (!(Test-Path -LiteralPath $path -PathType Leaf) -or
            ((Get-Item -LiteralPath $path).Attributes -band [IO.FileAttributes]::ReparsePoint) -or
            (Get-SourceBlobHash $path $Files[$relative]) -cne $Files[$relative]) {
            throw "Snapshot differs from its committed Git blob: $relative"
        }
    }
}
function Expand-VerifiedSourceArchive([string]$ArchivePath, [string]$Directory, [Collections.Generic.Dictionary[string,string]]$Files) {
    if ($Files.Count -eq 0 -or (Test-Path -LiteralPath $Directory)) { throw 'Source snapshot requires a nonempty commit and a new destination.' }
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $archive = [IO.Compression.ZipFile]::OpenRead($ArchivePath)
    try {
        # Validate the entire member set before writing any archive member.
        foreach ($entry in $archive.Entries) {
            Assert-SnapshotPath $entry.FullName
            if ($entry.Name.Length -eq 0) { continue }
            if (!$Files.ContainsKey($entry.FullName) -or !$seen.Add($entry.FullName)) {
                throw "Unexpected, duplicate or case-colliding source archive member: $($entry.FullName)"
            }
        }
        if ($seen.Count -ne $Files.Count) { throw 'Source archive omitted a committed file; remove export-ignore before packaging.' }
        $null = New-Item -ItemType Directory -Path $Directory
        foreach ($entry in $archive.Entries) {
            if ($entry.Name.Length -eq 0) { continue }
            $target = Join-Path $Directory $entry.FullName
            $null = New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force
            $incoming = $entry.Open(); $destination = [IO.File]::Open($target, [IO.FileMode]::CreateNew)
            try { $incoming.CopyTo($destination) } finally { $destination.Dispose(); $incoming.Dispose() }
        }
    } finally { $archive.Dispose() }
    Assert-SourceSnapshot $Directory $Files
}
$head = Invoke-Git @('rev-parse', 'HEAD')
if ($head -isnot [string] -or $head -cnotmatch '^(?:[0-9a-f]{40}|[0-9a-f]{64})$') { throw 'Cannot identify the source commit.' }
function Assert-SourceUnchanged {
    $currentHead = Invoke-Git @('rev-parse', 'HEAD')
    if ($currentHead -cne $head) { throw 'Source commit changed during packaging.' }
    $status = @(Invoke-Git @('status', '--porcelain', '--untracked-files=no'))
    if ($status.Count -ne 0) { throw 'Commit the reviewed source before packaging; the source archive must match the built files.' }
    $untracked = @(Invoke-Git @('ls-files', '--others', '--exclude-standard', '--directory'))
    if ($untracked.Count -ne 0) { throw 'Untracked source is present. Review it before packaging a clean source commit.' }
}
Assert-SourceUnchanged
if (!$OutputDirectory) { $OutputDirectory = Join-Path ([IO.Path]::GetTempPath()) ('SED-MotS-package-' + [Guid]::NewGuid().ToString('N')) }
$output = Assert-ExternalOutput $OutputDirectory
if (Test-Path -LiteralPath $output) { throw 'Choose a new output directory; package creation never replaces a previous handoff.' }
if (!$ArtifactsPath) { $ArtifactsPath = Join-Path $output 'artifacts' }
$artifacts = Assert-ExternalOutput $ArtifactsPath
if (Test-Path -LiteralPath $artifacts) { throw 'Choose a new artifacts directory for clean package compilation; prior compiler outputs are preserved.' }
$null = New-Item -ItemType Directory -Path $output
$name = "SED-MotS-$Version-win-x64"
$package = Join-Path $output $name
$null = New-Item -ItemType Directory -Path $package
$sourceArchive = Join-Path $output 'SED-MotS-source.zip'
$source = Join-Path $output '.source'
$sourceTools = Join-Path $source 'tools'
$sourceFiles = [Collections.Generic.Dictionary[string,string]]::new([StringComparer]::Ordinal)
foreach ($entry in @(Invoke-Git @('-c', 'core.quotepath=false', 'ls-tree', '-r', $head))) {
    if ($entry -cnotmatch '^(100644|100755) blob ([0-9a-f]{40}|[0-9a-f]{64})\t(.+)$') {
        throw "Unsupported source tree entry (symlinks and submodules are not packaged): $entry"
    }
    $objectId = $Matches[2]; $relative = $Matches[3]
    Assert-SnapshotPath $relative
    $sourceFiles.Add($relative, $objectId)
}
Invoke-Git @('-c', 'core.autocrlf=false', '-c', 'core.eol=lf', 'archive', '--format=zip', "--output=$sourceArchive", $head) 120
Expand-VerifiedSourceArchive $sourceArchive $source $sourceFiles
$snapshotReceipt = [ordered]@{ commit=$head; archiveSha256=(Get-FileHash -LiteralPath $sourceArchive -Algorithm SHA256).Hash.ToLowerInvariant();
    verifiedGitBlobs=$sourceFiles.Count; compiledFrom='verified-commit-snapshot'; postBuildInputsVerified=$false }
$snapshotReceipt | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $output 'source-snapshot.json') -Encoding UTF8

function Invoke-DotNet([string[]]$Arguments, [string]$LogName) {
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = $dotnetExe; $start.WorkingDirectory = $source
    foreach ($argument in ($Arguments + @("-p:ArtifactsPath=$artifacts", '-m:1', '-nodeReuse:false', '-p:BuildInParallel=false', '-p:UseSharedCompilation=false', '--disable-build-servers'))) { $start.ArgumentList.Add($argument) }
    $start.UseShellExecute = $false; $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true; $start.RedirectStandardError = $true
    $start.Environment['DOTNET_ROOT'] = Split-Path -Parent $dotnetExe
    $start.Environment['DOTNET_CLI_TELEMETRY_OPTOUT'] = '1'
    $start.Environment['DOTNET_GENERATE_ASPNET_CERTIFICATE'] = 'false'
    $start.Environment['MSBUILDDISABLENODEREUSE'] = '1'
    $process = [Diagnostics.Process]::Start($start)
    try {
        $stdout = $process.StandardOutput.ReadToEndAsync(); $stderr = $process.StandardError.ReadToEndAsync()
        $completed = Wait-OwnedProcess $process
        $log = $stdout.GetAwaiter().GetResult() + $stderr.GetAwaiter().GetResult()
        [IO.File]::WriteAllText((Join-Path $output $LogName), $log)
        if (!$completed) { throw "Owned dotnet process timed out; see $LogName" }
        if ($process.ExitCode -ne 0) { throw "dotnet exited $($process.ExitCode); see $LogName" }
    } finally { $process.Dispose() }
}

& (Join-Path $sourceTools 'Build-Net.ps1') -DotNet $dotnetExe -Python $pythonExe -OutputDirectory (Join-Path $output 'build-evidence') -ArtifactsPath $artifacts
Invoke-DotNet @('publish','dotnet/src/Sed.App/Sed.App.csproj','-c','Release','-r','win-x64','--self-contained','true','-p:PublishTrimmed=false',"-p:Version=$Version",'-o',$package,'--nologo') 'publish-editor.log'
Invoke-DotNet @('publish','dotnet/tools/Sed.Agent/Sed.Agent.csproj','-c','Release','-r','win-x64','--self-contained','true',"-p:Version=$Version",'-o',(Join-Path $package 'agent'),'--nologo') 'publish-agent.log'
Invoke-DotNet @('publish','dotnet/tools/Sed.Mcp/Sed.Mcp.csproj','-c','Release','-r','win-x64','--self-contained','true','-p:RestoreLockedMode=true',"-p:Version=$Version",'-o',(Join-Path $package 'mcp'),'--nologo') 'publish-mcp.log'
foreach ($required in @('Sed.App.exe','coreclr.dll','agent/sed-agent.exe','agent/coreclr.dll','mcp/sed-mcp.exe','mcp/sed-mcp.dll','mcp/coreclr.dll','mcp/ModelContextProtocol.Core.dll')) {
    if (!(Test-Path -LiteralPath (Join-Path $package $required) -PathType Leaf)) { throw "Missing published runtime component: $required" }
}
# Build and support files come from the same blob-verified commit snapshot.
# Ignored files in the live worktree cannot enter SDK globs or the handoff.
Assert-SourceSnapshot $source $sourceFiles
$snapshotReceipt.postBuildInputsVerified = $true
$snapshotReceipt | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $output 'source-snapshot.json') -Encoding UTF8
$supportRoots = @('LICENSE', 'NOTICE.md', 'README.md', 'AGENTS.md', 'CLAUDE.md', '.agents', '.claude', 'docs', 'licenses', 'data', 'tools')
$supportFiles = @($sourceFiles.Keys | Where-Object { $_.Split('/')[0] -cin $supportRoots } | Sort-Object)
if ($supportFiles.Count -eq 0) { throw 'Cannot enumerate tracked distribution support files.' }
foreach ($relative in $supportFiles) {
    if ($relative -match '["\r\n]' -or [IO.Path]::IsPathRooted($relative) -or $relative.Split('/') -contains '..') { throw "Unsupported tracked support-file path: $relative" }
    $sourceFile = Join-Path $source $relative
    if ((Get-Item -LiteralPath $sourceFile).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Distribution support files cannot be symbolic links: $relative" }
    $destination = Join-Path $package $relative
    $null = New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force
    Copy-Item -LiteralPath $sourceFile -Destination $destination
    if ($relative.StartsWith('data/')) {
        foreach ($component in @('agent', 'mcp')) {
            $componentDestination = Join-Path $package ($component + '/' + $relative)
            $null = New-Item -ItemType Directory -Path (Split-Path -Parent $componentDestination) -Force
            Copy-Item -LiteralPath $sourceFile -Destination $componentDestination
        }
    }
}
Copy-Item -LiteralPath (Join-Path $source 'docs/WINDOWS_QUICKSTART.md') -Destination (Join-Path $package 'START-HERE.md')

# Exercise the actual self-contained CLI without starting the GUI or a game.
$smoke = Join-Path $output 'agent-smoke'; $null = New-Item -ItemType Directory -Path $smoke
$agent = Join-Path $package 'agent/sed-agent.exe'
$null = Invoke-AgentSmoke @('schema') 'schema.json'
$smokeInput = Join-Path $smoke 'room.jkl'
$null = Invoke-AgentSmoke @('new', $smokeInput, 'mots') 'new.json'
$inspection = Invoke-AgentSmoke @('inspect', $smokeInput) 'inspect.json'
$hash = ($inspection | ConvertFrom-Json).inputSha256
$patch = Join-Path $smoke 'patch.json'
@{ schemaVersion=1; inputSha256=$hash; operations=@(@{op='setGravity';value=6}) } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $patch -Encoding utf8
$edited = Join-Path $smoke 'edited.jkl'
$null = Invoke-AgentSmoke @('apply', $smokeInput, $patch, $edited) 'apply.json'
$null = Invoke-AgentSmoke @('validate', $edited) 'validate.json'
$null = Invoke-AgentSmoke @('apply', $smokeInput, $patch, $edited) 'no-overwrite.json' -ExpectedExit 4
if ((Get-FileHash -LiteralPath $smokeInput -Algorithm SHA256).Hash.ToLowerInvariant() -ne $hash) { throw 'Packaged agent changed the source.' }

& (Join-Path $sourceTools 'Test-AgentWorkflow.ps1') -AgentPath $agent -OutputDirectory (Join-Path $output 'agent-workflow')
& (Join-Path $sourceTools 'Test-AgentSourceLights.ps1') -AgentPath $agent -OutputDirectory (Join-Path $output 'agent-source-lights')
& (Join-Path $sourceTools 'Test-AgentEditing.ps1') -AgentPath $agent -OutputDirectory (Join-Path $output 'agent-editing')
& (Join-Path $sourceTools 'Test-AgentDesign.ps1') -AgentPath $agent -OutputDirectory (Join-Path $output 'agent-design') -SkipSnapshot:(!$RunAgentSnapshots)
& (Join-Path $sourceTools 'Test-AgentSpatial.ps1') -AgentPath $agent -OutputDirectory (Join-Path $output 'agent-spatial') -SkipSnapshot:(!$RunAgentSnapshots)
if ($RunAgentSnapshots) {
    & (Join-Path $sourceTools 'Test-AgentLitSnapshot.ps1') -AgentPath $agent -OutputDirectory (Join-Path $output 'agent-lit-snapshot')
}

# Exercise the packaged stdio host and its packaged worker, not development bin/.
$mcp = Join-Path $package 'mcp/sed-mcp.exe'
$mcpEvidence = Join-Path $output 'mcp-workflow'
$mcpStart = [Diagnostics.ProcessStartInfo]::new()
$mcpStart.FileName = $pythonExe; $mcpStart.WorkingDirectory = $package
$mcpArguments = @('-B', (Join-Path $package 'tools/test_mcp_workflow.py'), '--server', $mcp, '--agent', $agent, '--output', $mcpEvidence)
if ($RunAgentSnapshots) { $mcpArguments += '--snapshots' }
foreach ($argument in $mcpArguments) { $mcpStart.ArgumentList.Add($argument) }
$mcpStart.UseShellExecute = $false; $mcpStart.CreateNoWindow = $true
$mcpStart.RedirectStandardOutput = $true; $mcpStart.RedirectStandardError = $true
$mcpProcess = [Diagnostics.Process]::Start($mcpStart)
try {
    $mcpStdout = $mcpProcess.StandardOutput.ReadToEndAsync(); $mcpStderr = $mcpProcess.StandardError.ReadToEndAsync()
    $completed = Wait-OwnedProcess $mcpProcess
    [IO.File]::WriteAllText((Join-Path $output 'mcp-workflow.log'), $mcpStdout.GetAwaiter().GetResult() + $mcpStderr.GetAwaiter().GetResult())
    if (!$completed) { throw 'Packaged MCP stdio workflow timed out; see mcp-workflow.log.' }
    if ($mcpProcess.ExitCode -ne 0) { throw 'Packaged MCP stdio workflow failed; see mcp-workflow.log.' }
} finally { $mcpProcess.Dispose() }
$mcpReceipt = Get-Content -LiteralPath (Join-Path $mcpEvidence 'verification.json') -Raw | ConvertFrom-Json
if ($mcpReceipt.status -cne 'passed' -or $mcpReceipt.snapshotsRequested -ne [bool]$RunAgentSnapshots -or
    $mcpReceipt.serverSha256 -cne (Get-FileHash -LiteralPath $mcp -Algorithm SHA256).Hash.ToLowerInvariant() -or
    $mcpReceipt.managedSha256 -cne (Get-FileHash -LiteralPath (Join-Path $package 'mcp/sed-mcp.dll') -Algorithm SHA256).Hash.ToLowerInvariant()) {
    throw 'MCP workflow receipt does not confirm the packaged executable and managed companion.'
}

# Author the original playable exercise with the packaged tools; engine execution
# is a separate opt-in test requiring the user's explicit game-resource manifest.
$playEvidence = Join-Path $output 'play-authoring'
$playStart = [Diagnostics.ProcessStartInfo]::new()
$playStart.FileName = $pythonExe; $playStart.WorkingDirectory = $package
foreach ($argument in @('-B', (Join-Path $package 'tools/new_playable_fixture.py'), '--server', $mcp, '--agent', $agent, '--output', $playEvidence)) {
    $playStart.ArgumentList.Add($argument)
}
$playStart.UseShellExecute = $false; $playStart.CreateNoWindow = $true
$playStart.RedirectStandardOutput = $true; $playStart.RedirectStandardError = $true
$playProcess = [Diagnostics.Process]::Start($playStart)
try {
    $playStdout = $playProcess.StandardOutput.ReadToEndAsync(); $playStderr = $playProcess.StandardError.ReadToEndAsync()
    $completed = Wait-OwnedProcess $playProcess
    [IO.File]::WriteAllText((Join-Path $output 'play-authoring.log'), $playStdout.GetAwaiter().GetResult() + $playStderr.GetAwaiter().GetResult())
    if (!$completed) { throw 'Packaged PLAY authoring timed out; see play-authoring.log.' }
    if ($playProcess.ExitCode -ne 0) { throw 'Packaged PLAY authoring failed; see play-authoring.log.' }
} finally { $playProcess.Dispose() }
$playReceipt = Get-Content -LiteralPath (Join-Path $playEvidence 'authoring-receipt.json') -Raw | ConvertFrom-Json
if ($playReceipt.status -cne 'authored' -or $playReceipt.gameLaunched -ne $false -or
    $playReceipt.serverSha256 -cne (Get-FileHash -LiteralPath $mcp -Algorithm SHA256).Hash.ToLowerInvariant() -or
    $playReceipt.agentSha256 -cne (Get-FileHash -LiteralPath $agent -Algorithm SHA256).Hash.ToLowerInvariant()) {
    throw 'PLAY authoring receipt does not confirm the packaged tools.'
}
foreach ($assembly in $playReceipt.managedAssemblies) {
    if ($assembly.sha256 -cne (Get-FileHash -LiteralPath $assembly.path -Algorithm SHA256).Hash.ToLowerInvariant()) {
        throw 'A managed tool assembly changed after PLAY authoring.'
    }
}
if ($playReceipt.managedAssemblies.Count -ne 6 -or $playReceipt.variants.Count -ne 4 -or
    @($playReceipt.variants | Where-Object { $_.reopenedTopologyMatches -ne $true }).Count -ne 0) {
    throw 'PLAY authoring receipt lacks complete managed identities or reopened topology evidence.'
}

# Exercise selection, batch UVs, arrays and refusal controls with the packaged
# CLI and original synthetic fixtures. This is authoring/serialization evidence;
# it does not start the editor or a game, or qualify runtime/retail behavior.
$workbenchScript = Join-Path $package 'tools/test_tool_workbench.py'
$workbenchEvidence = Join-Path $output 'agent-tool-workbench'
$workbenchStart = [Diagnostics.ProcessStartInfo]::new()
$workbenchStart.FileName = $pythonExe; $workbenchStart.WorkingDirectory = $package
foreach ($argument in @('-B', $workbenchScript, '--agent', $agent, '--output-dir', $workbenchEvidence)) {
    $workbenchStart.ArgumentList.Add($argument)
}
$workbenchStart.UseShellExecute = $false; $workbenchStart.CreateNoWindow = $true
$workbenchStart.RedirectStandardOutput = $true; $workbenchStart.RedirectStandardError = $true
$workbenchProcess = [Diagnostics.Process]::Start($workbenchStart)
try {
    $workbenchStdout = $workbenchProcess.StandardOutput.ReadToEndAsync(); $workbenchStderr = $workbenchProcess.StandardError.ReadToEndAsync()
    $completed = Wait-OwnedProcess $workbenchProcess
    [IO.File]::WriteAllText((Join-Path $output 'agent-tool-workbench.log'), $workbenchStdout.GetAwaiter().GetResult() + $workbenchStderr.GetAwaiter().GetResult())
    if (!$completed) { throw 'Packaged TOOL workbench timed out; see agent-tool-workbench.log.' }
    if ($workbenchProcess.ExitCode -ne 0) { throw 'Packaged TOOL-01 CLI workflow failed; see agent-tool-workbench.log.' }
} finally { $workbenchProcess.Dispose() }
$workbenchReceipt = Get-Content -LiteralPath (Join-Path $workbenchEvidence 'receipt.json') -Raw | ConvertFrom-Json
if ($workbenchReceipt.status -cne 'passed' -or
    $workbenchReceipt.gameLaunched -isnot [bool] -or $workbenchReceipt.gameLaunched -ne $false -or
    $workbenchReceipt.guiLaunched -isnot [bool] -or $workbenchReceipt.guiLaunched -ne $false -or
    [IO.Path]::GetFullPath($workbenchReceipt.agent) -ine [IO.Path]::GetFullPath($agent) -or
    $workbenchReceipt.scriptSha256 -cne (Get-FileHash -LiteralPath $workbenchScript -Algorithm SHA256).Hash.ToLowerInvariant()) {
    throw 'TOOL-01 receipt does not confirm the packaged CLI workflow and script identity.'
}
$workbenchRuntimeNames = @('sed-agent.exe', 'sed-agent.dll', 'sed-agent.deps.json', 'sed-agent.runtimeconfig.json',
    'Sed.Core.dll', 'Sed.Formats.dll', 'Sed.Rendering.dll', 'Sed.Rendering.Vulkan.dll')
if (@($workbenchReceipt.runtimeHashes).Count -ne $workbenchRuntimeNames.Count) {
    throw 'TOOL-01 receipt lacks the complete packaged executable and managed runtime identities.'
}
foreach ($runtimeName in $workbenchRuntimeNames) {
    $runtimePath = [IO.Path]::GetFullPath((Join-Path (Split-Path -Parent $agent) $runtimeName))
    $runtimeRecords = @($workbenchReceipt.runtimeHashes | Where-Object { [IO.Path]::GetFullPath($_.path) -ieq $runtimePath })
    if ($runtimeRecords.Count -ne 1 -or
        $runtimeRecords[0].sha256 -cne (Get-FileHash -LiteralPath $runtimePath -Algorithm SHA256).Hash.ToLowerInvariant()) {
        throw "TOOL-01 receipt does not bind the exact packaged runtime: $runtimeName"
    }
}
if ($workbenchReceipt.callCount -lt 1 -or $workbenchReceipt.callCount -ne @($workbenchReceipt.calls).Count -or
    $workbenchReceipt.checkCount -lt 1 -or $workbenchReceipt.checkCount -ne @($workbenchReceipt.checks).Count -or
    @($workbenchReceipt.checks | Where-Object { $_.passed -isnot [bool] -or $_.passed -ne $true }).Count -ne 0 -or
    @($workbenchReceipt.calls | Where-Object { $_.timedOut -eq $true -or $_.exitCode -ne $_.expectedExit }).Count -ne 0) {
    throw 'TOOL-01 receipt lacks complete passing CLI calls and authoring checks.'
}

# The dependency-closed distribution operation is shared with Linux. Exercise
# its CLI and MCP paths against the actual Windows binaries from an unrelated
# working directory with spaces, without an installed SDK or graphics driver.
$dependencyEvidence = Join-Path $output 'dependency-package-workflow'
$callerDirectory = Join-Path $output 'caller project with spaces'
$null = New-Item -ItemType Directory -Path $callerDirectory
$dependencyStart = [Diagnostics.ProcessStartInfo]::new()
$dependencyStart.FileName = $pythonExe; $dependencyStart.WorkingDirectory = $callerDirectory
foreach ($argument in @('-B', (Join-Path $package 'tools/test_package_workflow.py'), '--agent', $agent, '--mcp', $mcp, '--work', $dependencyEvidence)) {
    $dependencyStart.ArgumentList.Add($argument)
}
$dependencyStart.UseShellExecute = $false; $dependencyStart.CreateNoWindow = $true
$dependencyStart.RedirectStandardOutput = $true; $dependencyStart.RedirectStandardError = $true
$dependencyStart.Environment['DOTNET_ROOT'] = Join-Path $output 'no-sdk'
$dependencyStart.Environment['DOTNET_ROOT_X64'] = Join-Path $output 'no-sdk'
$dependencyStart.Environment['PYTHONDONTWRITEBYTECODE'] = '1'
$dependencyStart.Environment['VK_ICD_FILENAMES'] = Join-Path $output 'no-driver.json'
$dependencyStart.Environment['VK_DRIVER_FILES'] = Join-Path $output 'no-driver.json'
$dependencyProcess = [Diagnostics.Process]::Start($dependencyStart)
try {
    $dependencyStdout = $dependencyProcess.StandardOutput.ReadToEndAsync(); $dependencyStderr = $dependencyProcess.StandardError.ReadToEndAsync()
    $completed = Wait-OwnedProcess $dependencyProcess
    [IO.File]::WriteAllText((Join-Path $output 'dependency-package-workflow.log'), $dependencyStdout.GetAwaiter().GetResult() + $dependencyStderr.GetAwaiter().GetResult())
    if (!$completed) { throw 'Packaged dependency workflow timed out; see dependency-package-workflow.log.' }
    if ($dependencyProcess.ExitCode -ne 0) { throw 'Packaged dependency workflow failed; see dependency-package-workflow.log.' }
} finally { $dependencyProcess.Dispose() }
$dependencyReceiptPath = Join-Path $dependencyEvidence 'verification.json'
$dependencyReceipt = Get-Content -LiteralPath $dependencyReceiptPath -Raw | ConvertFrom-Json
if ($dependencyReceipt.status -cne 'passed' -or $dependencyReceipt.callCount -lt 1 -or
    $dependencyReceipt.callCount -ne @($dependencyReceipt.calls).Count -or
    [IO.Path]::GetFullPath($dependencyReceipt.agent.executable) -ine [IO.Path]::GetFullPath($agent) -or
    [IO.Path]::GetFullPath($dependencyReceipt.mcp.executable) -ine [IO.Path]::GetFullPath($mcp)) {
    throw 'Dependency workflow receipt does not confirm the packaged Windows tools and calls.'
}
foreach ($identity in @($dependencyReceipt.agent, $dependencyReceipt.mcp)) {
    if (@($identity.files).Count -lt 2) { throw 'Dependency workflow lacks executable and managed identities.' }
    foreach ($file in $identity.files) {
        if ($file.sha256 -cne (Get-FileHash -LiteralPath $file.path -Algorithm SHA256).Hash.ToLowerInvariant()) {
            throw 'A packaged runtime changed after dependency workflow acceptance.'
        }
    }
}

Assert-SourceUnchanged
Assert-SourceSnapshot $source $sourceFiles
$provenance = [ordered]@{ schemaVersion=2; product='SED MotS .NET development build'; version=$Version; commit=$head;
    basedOn='https://github.com/davidagnome/SED/tree/908d53d70d6b95784a92e9f1746ab5b13afcfdf0';
    sourceArchive='SED-MotS-source.zip'; target='win-x64'; selfContained=$true;
    compiledFrom='verified-commit-snapshot'; sourceGitBlobs=$sourceFiles.Count; sourceArchiveSha256=$snapshotReceipt.archiveSha256;
    guiLaunched=$false; gameLaunched=$false; packagedAgentSmoke='passed'; packagedAgentWorkflow='passed';
    packagedAgentDesign='passed'; packagedAgentSourceLights='passed'; packagedAgentSpatial='passed'; packagedAgentEditing='passed'; packagedAgentSnapshots=[bool]$RunAgentSnapshots;
    packagedMcpWorkflow='passed'; packagedMcpSnapshots=[bool]$RunAgentSnapshots; packagedMcpToolCalls=$mcpReceipt.toolCalls;
    packagedPlayAuthoring='passed'; packagedPlayToolCalls=$playReceipt.calls.Count;
    packagedToolWorkbench='passed'; packagedToolWorkbenchCalls=$workbenchReceipt.callCount;
    supportProfile='cross-platform-tools-v1'; packagedDependencyWorkflow='passed'; packagedDependencyWorkflowCalls=$dependencyReceipt.callCount;
    packagedDependencyWorkflowSha256=(Get-FileHash -LiteralPath $dependencyReceiptPath -Algorithm SHA256).Hash.ToLowerInvariant();
    headlessUiProbe='passed'; pythonToolSuites=@('tools/tests', 'tools/blender/tests');
    createdUtc=[DateTime]::UtcNow.ToString('o'); dotnetSha256=(Get-FileHash -LiteralPath $dotnetExe -Algorithm SHA256).Hash.ToLowerInvariant() }
$provenance | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $package 'PROVENANCE.json') -Encoding utf8
$manifest = Get-ChildItem -LiteralPath $package -Recurse -File | Sort-Object FullName | ForEach-Object {
    $relative=[IO.Path]::GetRelativePath($package,$_.FullName).Replace('\','/')
    '{0}  {1}' -f (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant(),$relative
}
$manifest | Set-Content -LiteralPath (Join-Path $package 'SHA256SUMS.txt') -Encoding utf8
Compress-Archive -LiteralPath $package -DestinationPath (Join-Path $output "$name.zip") -CompressionLevel Optimal
Invoke-Git @('bundle', 'create', (Join-Path $output 'SED-MotS-history.bundle'), 'HEAD') 120
$bundleHeads = @(Invoke-Git @('bundle', 'list-heads', (Join-Path $output 'SED-MotS-history.bundle')))
if ($bundleHeads.Count -ne 1 -or $bundleHeads[0] -cne "$head HEAD") { throw 'History bundle does not match the packaged HEAD commit.' }
Get-ChildItem -LiteralPath $output -File | Where-Object { $_.Extension -in '.zip','.bundle' } | Sort-Object Name | ForEach-Object {
    '{0}  {1}' -f (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant(), $_.Name
} | Set-Content -LiteralPath (Join-Path $output 'SHA256SUMS.txt') -Encoding utf8
Assert-SourceUnchanged
& (Join-Path $PSScriptRoot 'Verify-Distribution.ps1') -PackageDirectory $output
Assert-SourceUnchanged
Write-Output "Verified self-contained agent smoke, artifact manifests and matching source/history commit; Windows/source handoff: $output"
