#requires -Version 7.4
[CmdletBinding()]
param([Parameter(Mandatory)][string]$PackageDirectory)
$ErrorActionPreference = 'Stop'

function Assert-SafeZipPath([string]$Path) {
    if ([string]::IsNullOrEmpty($Path) -or $Path -match '[<>:"\\|?*\x00-\x1f]' -or $Path.StartsWith('/')) { throw "Unsafe ZIP path: $Path" }
    foreach ($part in $Path.TrimEnd('/').Split('/')) {
        if (!$part -or $part -in '.', '..' -or $part -match '[ .]$' -or $part -match '^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\.|$)') { throw "Unsafe Windows ZIP path: $Path" }
    }
}

function Assert-ZipMemberNames($Archive) {
    $names = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $fileNames = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($entry in $Archive.Entries) {
        Assert-SafeZipPath $entry.FullName
        if (!$names.Add($entry.FullName.TrimEnd('/'))) { throw "Duplicate or case-colliding ZIP member: $($entry.FullName)" }
        if ($entry.Name.Length -gt 0) { [void]$fileNames.Add($entry.FullName) }
    }
    foreach ($name in $names) {
        $slash = $name.LastIndexOf('/')
        while ($slash -gt 0) {
            if ($fileNames.Contains($name.Substring(0, $slash))) { throw "ZIP file also used as a directory: $name" }
            $slash = $name.LastIndexOf('/', $slash - 1)
        }
    }
}

function Get-GitArchiveCommit([string]$Path) {
    # git archive writes the commit ID into the ZIP end-of-central-directory comment.
    # Inspect the bounded tail and require the EOCD record to end at EOF.
    $stream = [IO.File]::OpenRead($Path)
    try {
        if ($stream.Length -lt 22) { throw 'Source ZIP has no end-of-central-directory record.' }
        $tail = [byte[]]::new([int][Math]::Min(65557L, $stream.Length))
        [void]$stream.Seek(-$tail.Length, [IO.SeekOrigin]::End)
        $stream.ReadExactly($tail)
    } finally { $stream.Dispose() }
    for ($i = $tail.Length - 22; $i -ge 0; $i--) {
        if ($tail[$i] -ne 0x50 -or $tail[$i+1] -ne 0x4b -or $tail[$i+2] -ne 0x05 -or $tail[$i+3] -ne 0x06) { continue }
        $length = [int]$tail[$i+20] + 256 * [int]$tail[$i+21]
        if ($i + 22 + $length -ne $tail.Length) { continue }
        $comment = [Text.Encoding]::ASCII.GetString($tail, $i + 22, $length)
        if ($comment -cnotmatch '^(?:[0-9a-f]{40}|[0-9a-f]{64})$') { throw 'Source ZIP lacks the git archive commit comment.' }
        return $comment
    }
    throw 'Source ZIP has no valid end-of-central-directory record at EOF.'
}

function Assert-CompleteHeadBundle([string]$Path, [string]$Commit) {
    $reader = [IO.StreamReader]::new($Path, [Text.Encoding]::ASCII)
    try {
        if ($reader.ReadLine() -cnotin '# v2 git bundle', '# v3 git bundle') { throw 'Unrecognized Git bundle header.' }
        $heads = [Collections.Generic.List[string]]::new()
        for ($i = 0; $i -lt 64; $i++) {
            $line = $reader.ReadLine()
            if ($null -eq $line -or $line.Length -gt 256) { throw 'Malformed Git bundle header.' }
            if ($line.Length -eq 0) {
                if ($heads.Count -ne 1 -or $heads[0] -cne "$Commit HEAD") { throw 'History bundle must advertise only HEAD at the binary provenance commit.' }
                return
            }
            if ($line.StartsWith('-')) { throw 'History bundle has prerequisites; the handoff must include complete history.' }
            if ($line.StartsWith('@')) {
                if ($line -cnotin '@object-format=sha1', '@object-format=sha256') { throw "Unsupported Git bundle capability: $line" }
            } else { $heads.Add($line) }
        }
        throw 'Git bundle header is too long.'
    } finally { $reader.Dispose() }
}

function Read-ZipJson($Archive, [string]$Path) {
    $entry = $Archive.GetEntry($Path)
    if ($null -eq $entry) { throw "Missing JSON distribution member: $Path" }
    $reader = [IO.StreamReader]::new($entry.Open())
    try { return $reader.ReadToEnd() | ConvertFrom-Json } finally { $reader.Dispose() }
}

function Assert-McpNotices($Archive, [string]$Prefix) {
    $dependencies = Read-ZipJson $Archive ($Prefix + 'mcp/sed-mcp.deps.json')
    $inventory = Read-ZipJson $Archive ($Prefix + 'licenses/net/inventory.json')
    $packages = @($dependencies.libraries.PSObject.Properties | Where-Object { $_.Value.type -in 'package', 'runtimepack' })
    if ($packages.Count -eq 0 -or !($packages.Name -contains 'ModelContextProtocol.Core/1.4.1')) { throw 'MCP dependency inventory lacks the pinned protocol SDK.' }
    # Every self-contained component carries a runtime, even though the runtime
    # is represented as type=runtimepack rather than an ordinary NuGet package.
    foreach ($component in @('Sed.App.deps.json', 'agent/sed-agent.deps.json')) {
        $componentDependencies = Read-ZipJson $Archive ($Prefix + $component)
        $runtimePacks = @($componentDependencies.libraries.PSObject.Properties | Where-Object { $_.Value.type -eq 'runtimepack' })
        if ($runtimePacks.Count -eq 0) { throw "Self-contained component has no runtime-pack identity: $component" }
        $packages += $runtimePacks
    }
    if (@($dependencies.libraries.PSObject.Properties | Where-Object { $_.Value.type -eq 'runtimepack' }).Count -eq 0) {
        throw 'Self-contained MCP component has no runtime-pack identity.'
    }
    $checked = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($dependency in $packages) {
        if (!$checked.Add($dependency.Name)) { continue }
        $packageId = $dependency.Name
        if ($dependency.Value.type -eq 'runtimepack') {
            if (!$packageId.StartsWith('runtimepack.', [StringComparison]::Ordinal)) { throw "Unexpected runtime-pack identity: $packageId" }
            $packageId = $packageId.Substring('runtimepack.'.Length)
        }
        $records = @($inventory.packages | Where-Object { $_.package -ceq $packageId })
        if ($records.Count -ne 1 -or @($records[0].files).Count -eq 0) { throw "Missing/ambiguous notices for packaged MCP dependency $($dependency.Name)." }
        if ($dependency.Value.type -eq 'runtimepack' -and $records[0].dependencyType -cne 'runtimepack') {
            throw "The runtime-pack identity $packageId is not identified as a collected runtime pack."
        }
        if (@($records[0].files | Where-Object { [IO.Path]::GetFileName($_.file) -match '(?i)licen[sc]e|copying' }).Count -eq 0) {
            throw "MCP dependency $($dependency.Name) has notices but no package license text."
        }
        foreach ($file in $records[0].files) {
            Assert-SafeZipPath $file.file
            if ($file.sha256 -cnotmatch '^[0-9a-f]{64}$') { throw "Invalid license hash for $($dependency.Name)." }
            $entry = $Archive.GetEntry($Prefix + 'licenses/net/' + $file.file)
            if ($null -eq $entry) { throw "Missing license file $($file.file)." }
            $stream = $entry.Open()
            try { $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)).ToLowerInvariant() }
            finally { $stream.Dispose() }
            if ($hash -cne $file.sha256) { throw "License content differs from its inventory: $($file.file)." }
        }
    }
    return $checked.Count
}

$root = (Resolve-Path -LiteralPath $PackageDirectory).Path
$checksumPath = Join-Path $root 'SHA256SUMS.txt'
$binaryZip = @(Get-ChildItem -LiteralPath $root -Filter '*-win-x64.zip' -File)
if ($binaryZip.Count -ne 1) { throw 'Expected exactly one Windows distribution ZIP.' }
$binaryZip = $binaryZip[0]
$expectedArtifacts = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
foreach ($name in @($binaryZip.Name,'SED-MotS-source.zip','SED-MotS-history.bundle')) { [void]$expectedArtifacts.Add($name) }
foreach ($artifact in Get-ChildItem -LiteralPath $root -File | Where-Object { $_.Extension -in '.zip', '.bundle' }) {
    if (!$expectedArtifacts.Contains($artifact.Name)) { throw "Unexpected distribution artifact: $($artifact.Name)" }
}
$records = @(Get-Content -LiteralPath $checksumPath | Where-Object { ![string]::IsNullOrWhiteSpace($_) })
if ($records.Count -ne $expectedArtifacts.Count) { throw 'Top-level checksum manifest must cover exactly the binary ZIP, source ZIP and history bundle.' }
foreach ($line in $records) {
    if ($line -notmatch '^[0-9a-fA-F]{64}  .+$') { throw 'Malformed top-level checksum row.' }
    $expected = $line.Substring(0,64).ToLowerInvariant(); $name = $line.Substring(66)
    if (!$expectedArtifacts.Remove($name)) { throw "Duplicate or unexpected top-level checksum member: $name" }
    if ([IO.Path]::GetFileName($name) -ne $name) { throw 'Unexpected path in top-level package checksum list.' }
    $actual = (Get-FileHash -LiteralPath (Join-Path $root $name) -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -ne $expected) { throw "Checksum mismatch: $name" }
}
$archive = [IO.Compression.ZipFile]::OpenRead($binaryZip.FullName)
try {
    Assert-ZipMemberNames $archive
    $files = @($archive.Entries | Where-Object { $_.Name.Length -gt 0 })
    $manifests = @($files | Where-Object { $_.FullName -match '^[^/]+/SHA256SUMS.txt$' })
    if ($manifests.Count -ne 1) { throw 'Missing/ambiguous in-package checksum manifest.' }
    $prefix = $manifests[0].FullName.Substring(0,$manifests[0].FullName.LastIndexOf('/')+1)
    if ($prefix -cne $binaryZip.BaseName + '/') { throw 'The binary ZIP root does not match its distribution name.' }
    $reader = [IO.StreamReader]::new($manifests[0].Open())
    try { $manifestLines = $reader.ReadToEnd() -split '\r?\n' } finally { $reader.Dispose() }
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($line in $manifestLines) {
        if ([string]::IsNullOrWhiteSpace($line)) { continue }
        if ($line -notmatch '^[0-9a-fA-F]{64}  .+$') { throw 'Malformed in-package checksum row.' }
        $expected=$line.Substring(0,64).ToLowerInvariant(); $relative=$line.Substring(66)
        Assert-SafeZipPath $relative
        if ($relative -ceq 'SHA256SUMS.txt') { throw 'The in-package checksum manifest cannot list itself.' }
        $path=$prefix+$relative
        if (!$seen.Add($path)) { throw "Duplicate manifest member: $path" }
        $entries=@($files | Where-Object { $_.FullName -ceq $path })
        if ($entries.Count -ne 1) { throw "Missing/duplicate ZIP member: $path" }
        $stream=$entries[0].Open()
        try { $hash=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)).ToLowerInvariant() }
        finally { $stream.Dispose() }
        if ($hash -ne $expected) { throw "ZIP member checksum mismatch: $path" }
    }
    if ($seen.Count -eq 0) { throw 'The in-package checksum manifest is empty.' }
    foreach ($file in $files) {
        if ($file.FullName -cne $manifests[0].FullName -and !$seen.Contains($file.FullName)) { throw 'The ZIP contains files outside its manifest.' }
    }
    foreach ($required in @('Sed.App.exe','coreclr.dll','agent/sed-agent.exe','agent/coreclr.dll','LICENSE','START-HERE.md','data/mots.tpl','data/master.tpl','agent/data/mots.tpl','agent/data/master.tpl','PROVENANCE.json')) {
        if (!$seen.Contains($prefix + $required)) { throw "The binary ZIP lacks a required distribution member: $required" }
    }
    $provenanceEntry = $archive.GetEntry($prefix + 'PROVENANCE.json')
    foreach ($required in @('AGENTS.md','.agents/skills/sed-mots/SKILL.md','tools/Test-AgentDesign.ps1','tools/Test-AgentLitSnapshot.ps1','docs/AGENT_DESIGN_ROADMAP.md','docs/RENDERER_LIGHTING.md')) {
        if (!$seen.Contains($prefix + $required)) { throw "The binary ZIP lacks an agent design workflow member: $required" }
    }
    if ($null -eq $provenanceEntry) { throw 'The binary ZIP has no source provenance.' }
    $reader = [IO.StreamReader]::new($provenanceEntry.Open())
    try { $provenance = $reader.ReadToEnd() | ConvertFrom-Json } finally { $reader.Dispose() }
    if ($provenance.commit -cnotmatch '^(?:[0-9a-f]{40}|[0-9a-f]{64})$' -or $provenance.sourceArchive -cne 'SED-MotS-source.zip' -or $provenance.target -cne 'win-x64' -or $provenance.selfContained -isnot [bool] -or !$provenance.selfContained) { throw 'Invalid or incomplete binary source provenance.' }
    # Keep the verifier usable for earlier, pre-MCP handoffs. Any new MCP
    # payload or provenance declaration activates the full MCP requirements.
    $hasMcp = $seen.Contains($prefix + 'mcp/sed-mcp.exe') -or $null -ne $provenance.PSObject.Properties['packagedMcpWorkflow']
    $mcpNoticePackages = 0
    if ($hasMcp) {
        foreach ($required in @('mcp/sed-mcp.exe','mcp/sed-mcp.dll','mcp/sed-mcp.deps.json','mcp/sed-mcp.runtimeconfig.json',
            'mcp/coreclr.dll','mcp/ModelContextProtocol.Core.dll','mcp/data/mots.tpl','mcp/data/master.tpl',
            'tools/test_mcp_workflow.py','docs/MCP_INTEGRATION.md','licenses/net/inventory.json')) {
            if (!$seen.Contains($prefix + $required)) { throw "The binary ZIP lacks an MCP distribution member: $required" }
        }
        if ($provenance.packagedMcpWorkflow -cne 'passed' -or $provenance.packagedMcpSnapshots -isnot [bool] -or
            $provenance.packagedMcpToolCalls -lt 1) { throw 'MCP package provenance does not record the actual stdio workflow.' }
        $mcpNoticePackages = Assert-McpNotices $archive $prefix
    }
} finally { $archive.Dispose() }
$sourcePath = Join-Path $root 'SED-MotS-source.zip'
$sourceCommit = Get-GitArchiveCommit $sourcePath
if ($sourceCommit -cne $provenance.commit) { throw 'Source ZIP git archive commit differs from binary provenance.' }
$source = [IO.Compression.ZipFile]::OpenRead($sourcePath)
try {
    Assert-ZipMemberNames $source
    foreach ($required in @('LICENSE','global.json','dotnet/src/Sed.Formats/Automation/AgentWorkspace.cs','tools/Build-Net.ps1','tools/Package-Windows.ps1','licenses/net/inventory.json')) {
        if ($null -eq $source.GetEntry($required)) { throw "Corresponding source archive lacks $required" }
    }
    if ($hasMcp) {
        foreach ($required in @('dotnet/tools/Sed.Mcp/Sed.Mcp.csproj','dotnet/tools/Sed.Mcp/packages.lock.json',
            'dotnet/tools/Sed.Mcp/SedTools.cs','dotnet/tools/Sed.Mcp/Infrastructure/McpFilePolicy.cs',
            'dotnet/tools/Sed.Mcp/Infrastructure/OperationStore.cs','dotnet/tools/Sed.Mcp/Infrastructure/SnapshotWorker.cs',
            'dotnet/tests/Sed.Mcp.Tests/Sed.Mcp.Tests.csproj','tools/test_mcp_workflow.py','docs/MCP_INTEGRATION.md')) {
            if ($null -eq $source.GetEntry($required)) { throw "Corresponding source archive lacks $required" }
        }
    }
} finally { $source.Dispose() }
$bundlePath = Join-Path $root 'SED-MotS-history.bundle'
Assert-CompleteHeadBundle $bundlePath $provenance.commit
& git -C (Split-Path -Parent $PSScriptRoot) bundle verify $bundlePath
if ($LASTEXITCODE -ne 0) { throw 'Git history bundle verification failed.' }
$bundleHeads = @(& git bundle list-heads (Join-Path $root 'SED-MotS-history.bundle'))
if ($LASTEXITCODE -ne 0 -or $bundleHeads.Count -ne 1 -or $bundleHeads[0] -cne "$($provenance.commit) HEAD") { throw 'History bundle HEAD differs from binary provenance.' }
[ordered]@{ verifiedUtc=[DateTime]::UtcNow.ToString('o'); topLevelChecksums=$records.Count;
    windowsZip=$binaryZip.Name; verifiedBinaryMembers=$seen.Count; exactBinaryMemberSet=$true;
    sourceArchiveRequiredFiles=$true; sourceArchiveCommit=$sourceCommit; sourceCommitMatchesBinary=$true;
    gitBundleVerified=$true; completeHistoryHeadMatchesBinary=$true; installersExecuted=$false;
    mcpIncluded=[bool]$hasMcp; mcpRuntimeNoticePackages=$mcpNoticePackages } |
    ConvertTo-Json | Set-Content -LiteralPath (Join-Path $root 'verification.json') -Encoding utf8
Write-Output "Verified $($seen.Count) distribution files, exact ZIP member set, source archive and Git history."
