# Original, offline fixtures. Invoked by test_collect_net_notices.py with a
# 180-second child deadline; this script itself launches no child processes.
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$CollectorPath,
    [Parameter(Mandatory)][string]$ScratchPath
)
$ErrorActionPreference = 'Stop'
$source = [IO.Path]::GetFullPath($CollectorPath)
$root = [IO.Path]::GetFullPath($ScratchPath)
$tokens = $null; $parseErrors = $null
$null = [Management.Automation.Language.Parser]::ParseFile($source, [ref]$tokens, [ref]$parseErrors)
if ($parseErrors) { throw 'Collector has syntax errors; refusal cases must not pass from parser failures.' }
if (Test-Path -LiteralPath $root) { throw 'Use a new fixture scratch directory.' }
for ($ancestor = $root; $ancestor; $ancestor = Split-Path -Parent $ancestor) {
    if (Test-Path -LiteralPath (Join-Path $ancestor '.git')) { throw 'Fixture scratch must be outside every Git checkout.' }
    if ((Test-Path -LiteralPath $ancestor) -and ((Get-Item -LiteralPath $ancestor).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw 'Fixture scratch must not pass through a reparse point.'
    }
}
$null = New-Item -ItemType Directory -Path $root
$contentHash = [Convert]::ToBase64String([byte[]](1..64))
$primarySource = 'https://packages.example.invalid/primary/index.json'
$fallbackSource = 'https://packages.example.invalid/fallback/index.json'
function Put([string]$Path, [string]$Text) {
    $null = New-Item -ItemType Directory -Path (Split-Path -Parent $Path) -Force
    [IO.File]::WriteAllText($Path, $Text, [Text.UTF8Encoding]::new($false))
}
function Make-Package([string]$Directory, [string]$Id, [string]$Version, [string]$CandidateSource, [string]$LicenseMember = 'LICENSE.txt') {
    $null = New-Item -ItemType Directory -Path $Directory -Force
    $name = $Id.ToLowerInvariant()
    $payload = [ordered]@{}
    $payload["$name.nuspec"] = "<package><metadata><id>$Id</id><version>$Version</version><license type='expression'>MIT</license></metadata></package>"
    $payload[$LicenseMember] = 'Original synthetic license or notice text'
    $payload['runtimes/win-x64/lib/net10.0/fixture.dll'] = 'Original non-executable fixture'
    foreach ($entry in $payload.GetEnumerator()) { Put (Join-Path $Directory $entry.Key) $entry.Value }
    $zipPath = Join-Path $Directory "$name.$Version.nupkg"
    $zip = [IO.Compression.ZipFile]::Open($zipPath, [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($entry in $payload.GetEnumerator()) {
            $member = $zip.CreateEntry($entry.Key)
            $writer = [IO.StreamWriter]::new($member.Open())
            try { $writer.Write($entry.Value) } finally { $writer.Dispose() }
        }
        # NuGet intentionally does not extract these packaging-only members.
        foreach ($name in @('_rels/.rels', '[Content_Types].xml', 'package/services/metadata/core-properties/fixture.psmdcp')) {
            $member = $zip.CreateEntry($name)
            $writer = [IO.StreamWriter]::new($member.Open())
            try { $writer.Write('Original package metadata') } finally { $writer.Dispose() }
        }
    } finally { $zip.Dispose() }
    $stream = [IO.File]::OpenRead($zipPath)
    try { $archiveHash = [Convert]::ToBase64String([Security.Cryptography.SHA512]::HashData($stream)) }
    finally { $stream.Dispose() }
    $name = $Id.ToLowerInvariant()
    Put (Join-Path $Directory "$name.$Version.nupkg.sha512") $archiveHash
    Put (Join-Path $Directory '.nupkg.metadata') (@{version=2; contentHash=$contentHash; source=$CandidateSource} | ConvertTo-Json)
    return @{files=@($payload.Keys) + @('.nupkg.metadata', "$name.$Version.nupkg.sha512"); archiveHash=$archiveHash}
}
# Stub only the external HTTP boundary; selection, copying, parsing and inventory
# writing use the real collector. No HTTP is performed, including on refusals.
function Invoke-WebRequest {
    param($Uri, $OutFile)
    if ($Uri -ceq 'https://raw.githubusercontent.com/phosphor-icons/homepage/master/LICENSE') {
        Put $OutFile 'Original synthetic font license'
    } elseif ($fixtureGithubFile -and $Uri -ceq "https://raw.githubusercontent.com/fixture/original-package/0123456789012345678901234567890123456789/$fixtureGithubFile") {
        Put $OutFile 'Original synthetic repository license or notice'
    } else { throw 'Unexpected fixture network request' }
}
function Invoke-RestMethod {
    param($Uri, $Headers)
    if (!$fixtureGithubFile -or $Uri -cne 'https://api.github.com/repos/fixture/original-package/git/trees/0123456789012345678901234567890123456789') {
        throw 'Unexpected fixture network request'
    }
    return @{tree=@(@{type='blob'; path=$fixtureGithubFile})}
}
$cases = @(
    @{name='ordinary-partial-primary-fallback'; runtime=$false; change='directory-only'; fallback=$true; success=$true},
    @{name='runtime-partial-primary-fallback'; runtime=$true; change='directory-only'; fallback=$true; success=$true},
    @{name='signed-content-hash-differs-from-archive'; runtime=$false; change='none'; fallback=$false; success=$true},
    @{name='missing-marker-fallback'; runtime=$false; change='marker-missing'; fallback=$true; success=$true},
    @{name='corrupt-marker-fallback'; runtime=$true; change='marker-corrupt'; fallback=$true; success=$true},
    @{name='wrong-content-hash-fallback'; runtime=$false; change='content-hash'; fallback=$true; success=$true},
    @{name='missing-ordinary-file-fallback'; runtime=$false; change='payload-missing'; fallback=$true; success=$true},
    @{name='missing-runtime-file-fallback'; runtime=$true; change='payload-missing'; fallback=$true; success=$true},
    @{name='wrong-nuspec-id-fallback'; runtime=$false; change='nuspec-id'; fallback=$true; success=$true},
    @{name='wrong-nuspec-version-fallback'; runtime=$true; change='nuspec-version'; fallback=$true; success=$true},
    @{name='all-incomplete-refused'; runtime=$true; change='directory-only'; fallback=$false; success=$false; error='Missing completion input'},
    @{name='missing-marker-refused'; runtime=$true; change='marker-missing'; fallback=$false; success=$false; error='Missing completion input: .nupkg.metadata'},
    @{name='corrupt-marker-refused'; runtime=$false; change='marker-corrupt'; fallback=$false; success=$false; error='No complete matching restored package'},
    @{name='wrong-content-hash-refused'; runtime=$false; change='content-hash'; fallback=$false; success=$false; error='Restored content hash differs'},
    @{name='missing-sidecar-refused'; runtime=$true; change='sidecar-missing'; fallback=$false; success=$false; error='Missing completion input'},
    @{name='bad-sidecar-refused'; runtime=$false; change='sidecar-corrupt'; fallback=$false; success=$false; error='No complete matching restored package'},
    @{name='wrong-nuspec-refused'; runtime=$false; change='nuspec-id'; fallback=$false; success=$false; error='Nuspec identity differs'},
    @{name='missing-runtime-payload-refused'; runtime=$true; change='payload-missing'; fallback=$false; success=$false; error='Missing or truncated restored runtime file'},
    @{name='nuspec-only-no-license-refused'; runtime=$false; change='no-license'; fallback=$false; success=$false; error='No bundled license or pinned GitHub source'},
    @{name='pinned-notice-only-refused'; runtime=$false; change='pinned-notice'; fallback=$false; success=$false; error='No package license text collected'},
    @{name='pinned-license-accepted'; runtime=$false; change='pinned-license'; fallback=$false; success=$true},
    @{name='nested-notice-only-refused'; runtime=$false; change='nested-notice'; fallback=$false; success=$false; error='No bundled license or pinned GitHub source'},
    @{name='nested-license-accepted'; runtime=$false; change='nested-license'; fallback=$false; success=$true},
    @{name='external-output-source-preserved'; runtime=$true; change='none'; output='external'; fallback=$false; success=$true},
    @{name='external-output-incomplete-refused'; runtime=$true; change='marker-missing'; output='external'; fallback=$false; success=$false; error='Missing completion input: .nupkg.metadata'},
    @{name='external-output-source-tree-refused'; runtime=$false; change='none'; output='source'; fallback=$false; success=$false; error='outside the source tree'},
    @{name='external-output-git-tree-refused'; runtime=$false; change='none'; output='git'; fallback=$false; success=$false; error='outside every Git worktree'},
    @{name='external-output-existing-refused'; runtime=$false; change='none'; output='existing'; fallback=$false; success=$false; error='new notice output directory'},
    @{name='external-output-junction-refused'; runtime=$false; change='none'; output='junction'; fallback=$false; success=$false; error='links or reparse points'}
)
$records = [Collections.Generic.List[object]]::new()
foreach ($case in $cases) {
    $fixtureGithubFile = $null
    $caseRoot = Join-Path $root $case.name
    $fixtureRepo = Join-Path $caseRoot 'repo'
    $artifacts = Join-Path $caseRoot 'artifacts'
    $null = New-Item -ItemType Directory -Path (Join-Path $fixtureRepo 'tools') -Force
    Copy-Item -LiteralPath $source -Destination (Join-Path $fixtureRepo 'tools/Collect-NetNotices.ps1')
    Put (Join-Path $fixtureRepo 'dotnet/assets/Phosphor.ttf') 'Original synthetic font'
    foreach ($file in @('dotnet.exe', 'LICENSE.txt', 'ThirdPartyNotices.txt')) { Put (Join-Path $caseRoot "sdk/$file") 'Original SDK fixture' }
    $id = if ($case.runtime) { 'Microsoft.AspNetCore.App.Runtime.win-x64' } else { 'Original.Package' }
    $version = '1.2.3'; $relative = "$($id.ToLowerInvariant())/$version"
    $primary = Join-Path $caseRoot 'primary'; $fallback = Join-Path $caseRoot 'fallback'
    $primaryDir = Join-Path $primary $relative
    $licenseMember = switch ($case.change) {
        'nested-notice' { 'licenses/NOTICE.txt' }
        'nested-license' { 'notices/LICENSE.txt' }
        default { 'LICENSE.txt' }
    }
    $package = Make-Package $primaryDir $id $version $primarySource $licenseMember
    $selectedPackage = $package; $expectedSource = $primarySource
    if ($case.fallback) {
        $selectedPackage = Make-Package (Join-Path $fallback $relative) $id $version $fallbackSource
        $expectedSource = $fallbackSource
    }
    switch ($case.change) {
        'directory-only' {
            foreach ($file in @("$($id.ToLowerInvariant()).$version.nupkg.sha512", '.nupkg.metadata')) { [IO.File]::Delete((Join-Path $primaryDir $file)) }
            if (!$case.runtime) { [IO.File]::Delete((Join-Path $primaryDir "$($id.ToLowerInvariant()).nuspec")) }
        }
        'marker-missing' { [IO.File]::Delete((Join-Path $primaryDir '.nupkg.metadata')) }
        'marker-corrupt' { Put (Join-Path $primaryDir '.nupkg.metadata') '{not-json' }
        'content-hash' { Put (Join-Path $primaryDir '.nupkg.metadata') (@{version=2; contentHash=[Convert]::ToBase64String([byte[]](2..65)); source=$primarySource} | ConvertTo-Json) }
        'payload-missing' { [IO.File]::Delete((Join-Path $primaryDir 'runtimes/win-x64/lib/net10.0/fixture.dll')) }
        'sidecar-missing' { [IO.File]::Delete((Join-Path $primaryDir "$($id.ToLowerInvariant()).$version.nupkg.sha512")) }
        'sidecar-corrupt' { Put (Join-Path $primaryDir "$($id.ToLowerInvariant()).$version.nupkg.sha512") 'not-a-sha512' }
        'nuspec-id' { Put (Join-Path $primaryDir "$($id.ToLowerInvariant()).nuspec") '<package><metadata><id>Wrong.Package</id><version>1.2.3</version></metadata></package>' }
        'nuspec-version' { Put (Join-Path $primaryDir "$($id.ToLowerInvariant()).nuspec") "<package><metadata><id>$id</id><version>9.9.9</version></metadata></package>" }
    }
    if ($case.change -in @('no-license', 'pinned-notice', 'pinned-license')) {
        [IO.File]::Delete((Join-Path $primaryDir 'LICENSE.txt'))
        $package.files = @($package.files | Where-Object { $_ -cne 'LICENSE.txt' })
        if ($case.change -ne 'no-license') {
            $fixtureGithubFile = if ($case.change -eq 'pinned-notice') { 'NOTICE' } else { 'LICENSE' }
            Put (Join-Path $primaryDir "$($id.ToLowerInvariant()).nuspec") "<package><metadata><id>$id</id><version>$version</version><license type='expression'>MIT</license><repository url='https://github.com/fixture/original-package' commit='0123456789012345678901234567890123456789'/></metadata></package>"
        }
    }
    $folders = [ordered]@{}; $folders[$primary] = @{}
    if ($case.fallback) { $folders[$fallback] = @{} }
    $libs = [ordered]@{}; $downloads = @()
    if ($case.runtime) { $downloads = @(@{name=$id; version="[$version]"}) }
    else { $libs["$id/$version"] = @{type='package'; path=$relative; sha512=$contentHash; files=$package.files} }
    $assets = @{libraries=$libs; packageFolders=$folders; project=@{frameworks=@{net10=@{downloadDependencies=$downloads}}}} | ConvertTo-Json -Depth 8
    foreach ($project in @('Sed.App', 'Sed.Agent', 'Sed.Mcp')) { Put (Join-Path $artifacts "obj/$project/project.assets.json") $assets }
    $noticeOutput = Join-Path $fixtureRepo 'licenses/net'
    $extraArguments = @{}
    if ($case.output) {
        Put (Join-Path $noticeOutput 'retained.txt') 'Existing source notice - preserve exactly'
        $noticeOutput = Join-Path $caseRoot 'external-notices'
        switch ($case.output) {
            'source' { $noticeOutput = Join-Path $fixtureRepo 'other-notices' }
            'git' {
                $noticeOutput = Join-Path $caseRoot 'other-repo/notices'
                Put (Join-Path $caseRoot 'other-repo/.git') 'gitdir: original-fixture'
            }
            'existing' { Put (Join-Path $noticeOutput 'keep.txt') 'Existing external notice - preserve exactly' }
            'junction' {
                $actual = Join-Path $caseRoot 'actual-notices'
                $null = New-Item -ItemType Directory -Path $actual
                $linkType = if ($IsWindows) { 'Junction' } else { 'SymbolicLink' }
                $null = New-Item -ItemType $linkType -Path (Join-Path $caseRoot 'linked-parent') -Target $actual
                $noticeOutput = Join-Path $caseRoot 'linked-parent/new-notices'
            }
        }
        $extraArguments.OutputDirectory = $noticeOutput
    }
    $errorText = $null
    try { & (Join-Path $fixtureRepo 'tools/Collect-NetNotices.ps1') -DotNet (Join-Path $caseRoot 'sdk/dotnet.exe') -ArtifactsPath $artifacts @extraArguments | Out-Null }
    catch { $errorText = $_.Exception.Message }
    $passed = (($null -eq $errorText) -eq $case.success)
    if (!$case.success -and $passed) { $passed = $errorText.Contains($case.error) }
    if ($passed -and $case.success) {
        $inventory = Get-Content -LiteralPath (Join-Path $noticeOutput 'inventory.json') -Raw | ConvertFrom-Json
        $entry = @($inventory.packages)[0]
        $passed = ($entry.package -ceq "$id/$version" -and @($entry.files).Count -ge 1 -and @($entry.sourceProjects).Count -eq 3 -and
            $entry.contentSha512 -ceq $contentHash -and $entry.archiveSha512 -cne $contentHash -and
            $entry.archiveSha512 -ceq $selectedPackage.archiveHash -and $entry.packageSource -ceq $expectedSource)
        $passed = $passed -and @($entry.files | Where-Object { $_.isPackageLicense }).Count -ge 1
        if ($case.change -eq 'nested-license') {
            $licenseRecord = @($entry.files | Where-Object { $_.isPackageLicense })
            $passed = $passed -and $licenseRecord.Count -eq 1 -and
                $licenseRecord[0].source -ceq "nuget:$id/$version/notices/LICENSE.txt" -and
                $licenseRecord[0].file -ceq "$id-$version/notices_LICENSE.txt"
        }
    }
    if ($case.output) {
        $sourceNotices = Join-Path $fixtureRepo 'licenses/net'
        $passed = $passed -and @((Get-ChildItem -LiteralPath $sourceNotices -Recurse -File)).Count -eq 1 -and
            [IO.File]::ReadAllText((Join-Path $sourceNotices 'retained.txt')) -ceq 'Existing source notice - preserve exactly'
        if (!$case.success) { $passed = $passed -and !(Test-Path -LiteralPath (Join-Path $noticeOutput 'inventory.json')) }
        if ($case.output -eq 'existing') {
            $passed = $passed -and @((Get-ChildItem -LiteralPath $noticeOutput)).Count -eq 1 -and
                [IO.File]::ReadAllText((Join-Path $noticeOutput 'keep.txt')) -ceq 'Existing external notice - preserve exactly'
        }
        if ($case.output -eq 'junction') { $passed = $passed -and @((Get-ChildItem -LiteralPath $actual)).Count -eq 0 }
    }
    $records.Add([ordered]@{case=$case.name; passed=$passed; expectedSuccess=$case.success; error=$errorText})
    Write-Output "$($case.name): $passed$(if ($errorText) { ' (' + $errorText + ')' })"
}
$receipt = [ordered]@{sourceSha256=(Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash.ToLowerInvariant(); cases=$records;
    passed=@($records | Where-Object { $_.passed }).Count; failed=@($records | Where-Object { !$_.passed }).Count;
    networkPerformed=$false; buildOrRestorePerformed=$false}
$receipt | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $root 'receipt.json') -Encoding UTF8
if ($receipt.failed) { exit 1 }
