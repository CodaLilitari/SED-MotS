[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$DotNet,
    # Match the .NET artifacts layout used by Build-Net and Package-Windows.
    # Omit only when collecting an older restore using in-project obj folders.
    [string]$ArtifactsPath
)
$ErrorActionPreference = 'Stop'
function Get-NuGetVersionIdentity([string]$Value) {
    if ($Value -notmatch '^(?<release>\d+(?:\.\d+){0,3})(?<suffix>-[0-9A-Za-z.-]+)?(?:\+[0-9A-Za-z.-]+)?$') { throw 'Invalid NuGet version.' }
    $suffix = $Matches.suffix
    $parts = [Collections.Generic.List[string]]::new()
    foreach ($part in $Matches.release.Split('.')) { $parts.Add(([uint32]$part).ToString()) }
    while ($parts.Count -lt 3) { $parts.Add('0') }
    if ($parts.Count -eq 4 -and $parts[3] -eq '0') { $parts.RemoveAt(3) }
    return (($parts -join '.') + $suffix).ToLowerInvariant()
}
function Assert-NuGetHash([string]$Value) {
    if (!$Value -or [Convert]::FromBase64String($Value).Length -ne 64) { throw 'Missing or invalid NuGet SHA512 identity.' }
}
function Get-PackageFilePath([string]$Directory, [string]$Relative) {
    $relativePath = $Relative.Replace('\', '/')
    if (!$relativePath -or $relativePath.Contains(':') -or @($relativePath.Split('/') | Where-Object { $_ -in @('', '.', '..') }).Count) {
        throw "Invalid restored package member: $Relative"
    }
    return Join-Path $Directory $relativePath
}
function Resolve-CompletePackage([string[]]$PackageFolders, [string]$Identity, [string]$Relative,
                                  [string]$ExpectedContentHash, [string[]]$ExpectedFiles) {
    $id, $version = $Identity.Split('/')
    if (!$id -or !$version -or $id -notmatch '^[A-Za-z0-9_.-]+$') { throw "Invalid restored package identity: $Identity" }
    $normalizedVersion = Get-NuGetVersionIdentity $version
    if ($ExpectedFiles.Count -gt 0 -and !$ExpectedContentHash) { throw 'Restored package is missing its assets content hash.' }
    if ($ExpectedContentHash) { Assert-NuGetHash $ExpectedContentHash }
    $failures = [Collections.Generic.List[string]]::new()
    foreach ($folder in $PackageFolders) {
        $directory = Get-PackageFilePath $folder $Relative
        if (!(Test-Path -LiteralPath $directory -PathType Container)) { continue }
        try {
            $stem = "$($id.ToLowerInvariant()).$($version.ToLowerInvariant())"
            $hashPath = Join-Path $directory "$stem.nupkg.sha512"
            $metadataPath = Join-Path $directory '.nupkg.metadata'
            $nuspecPath = Join-Path $directory "$($id.ToLowerInvariant()).nuspec"
            foreach ($required in @($hashPath, $metadataPath, $nuspecPath)) {
                if (!(Test-Path -LiteralPath $required -PathType Leaf)) { throw "Missing completion input: $([IO.Path]::GetFileName($required))" }
            }
            $archiveHash = ([IO.File]::ReadAllText($hashPath)).Trim()
            Assert-NuGetHash $archiveHash
            $completion = Get-Content -LiteralPath $metadataPath -Raw | ConvertFrom-Json
            if ($completion.version -notin @(1, 2)) { throw 'Unsupported NuGet completion metadata.' }
            $contentHash = [string]$completion.contentHash
            Assert-NuGetHash $contentHash
            # NuGet's signed-package content hash excludes the signature and can
            # differ from the archive SHA512 sidecar. Assets use contentHash.
            if ($ExpectedContentHash -and $contentHash -cne $ExpectedContentHash) { throw 'Restored content hash differs from project.assets.json.' }
            [xml]$nuspec = Get-Content -LiteralPath $nuspecPath -Raw
            $metadata = $nuspec.SelectSingleNode('//*[local-name()="metadata"]')
            if (!$metadata -or [string]$metadata.id -ine $id -or (Get-NuGetVersionIdentity ([string]$metadata.version)) -cne $normalizedVersion) {
                throw 'Nuspec identity differs from the restored package.'
            }
            $files = [Collections.Generic.List[string]]::new()
            if ($ExpectedContentHash) {
                if (!$ExpectedFiles) { throw 'Restored package has no assets file coverage.' }
                foreach ($file in $ExpectedFiles) {
                    if (!(Test-Path -LiteralPath (Get-PackageFilePath $directory $file) -PathType Leaf)) { throw "Missing restored file: $file" }
                    $files.Add($file)
                }
            } else {
                $archivePath = Join-Path $directory "$stem.nupkg"
                $archive = [IO.Compression.ZipFile]::OpenRead($archivePath)
                try {
                    # Match NuGet PackageExtractor.ShouldInclude: OPC metadata,
                    # the specially extracted root nuspec, and generated cache
                    # metadata are intentionally not ordinary extracted files.
                    foreach ($member in $archive.Entries) {
                        $file = $member.FullName
                        if ($file.EndsWith('/')) { continue }
                        $base = [IO.Path]::GetFileName($file)
                        if ($base -in @('.rels', '[Content_Types].xml') -or [IO.Path]::GetExtension($file) -ceq '.psmdcp' -or
                            $file -ieq "$stem.nupkg.sha512" -or $file -ieq '.nupkg.metadata' -or
                            (!$file.Contains('/') -and ($file.EndsWith('.nuspec', [StringComparison]::OrdinalIgnoreCase) -or $file.EndsWith('.nupkg', [StringComparison]::OrdinalIgnoreCase)))) { continue }
                        $path = Get-PackageFilePath $directory $file
                        if (!(Test-Path -LiteralPath $path -PathType Leaf) -or (Get-Item -LiteralPath $path).Length -ne $member.Length) { throw "Missing or truncated restored runtime file: $file" }
                        $files.Add($file)
                    }
                    if ($files.Count -eq 0) { throw 'Runtime package has no extracted file coverage.' }
                } finally { $archive.Dispose() }
            }
            return @{ directory = $directory; nuspecPath = $nuspecPath; metadata = $metadata; files = $files.ToArray();
                contentSha512 = $contentHash; archiveSha512 = $archiveHash; packageSource = [string]$completion.source }
        } catch { $failures.Add("${directory}: $($_.Exception.Message)") }
    }
    throw "No complete matching restored package for $Identity. $($failures -join '; ')"
}
$repo = Split-Path -Parent $PSScriptRoot
$projects = [ordered]@{
    'Sed.App' = 'dotnet/src/Sed.App/obj/project.assets.json'
    'Sed.Agent' = 'dotnet/tools/Sed.Agent/obj/project.assets.json'
    'Sed.Mcp' = 'dotnet/tools/Sed.Mcp/obj/project.assets.json'
}
$libraries = @{}
foreach ($project in $projects.GetEnumerator()) {
    $assetsPath = if ($ArtifactsPath) {
        Join-Path ([IO.Path]::GetFullPath($ArtifactsPath)) "obj/$($project.Key)/project.assets.json"
    } else { Join-Path $repo $project.Value }
    if (!(Test-Path -LiteralPath $assetsPath -PathType Leaf)) {
        throw "Build/restore $($project.Key) before collecting notices. Expected assets: $assetsPath"
    }
    $assets = Get-Content -LiteralPath $assetsPath -Raw | ConvertFrom-Json
    foreach ($entry in $assets.libraries.PSObject.Properties) {
        if ($entry.Value.type -ne 'package') { continue }
        if ($libraries.ContainsKey($entry.Name)) {
            if ($libraries[$entry.Name].library.sha512 -cne $entry.Value.sha512) { throw "Different restored identities for $($entry.Name)." }
            $libraries[$entry.Name].projects.Add($project.Key)
            continue
        }
        $resolved = Resolve-CompletePackage @($assets.packageFolders.PSObject.Properties.Name) $entry.Name $entry.Value.path $entry.Value.sha512 @($entry.Value.files)
        $sourceProjects = [Collections.Generic.List[string]]::new(); $sourceProjects.Add($project.Key)
        $libraries[$entry.Name] = @{ library = $entry.Value; resolved = $resolved; projects = $sourceProjects; dependencyType = 'package' }
    }
    # Self-contained runtime packs are restore downloads, not ordinary entries
    # in assets.libraries. The SDK's license is not their corresponding license.
    foreach ($framework in $assets.project.frameworks.PSObject.Properties) {
        foreach ($download in @($framework.Value.downloadDependencies)) {
            if ($null -eq $download -or $download.name -notmatch '^Microsoft\.(NETCore|AspNetCore|WindowsDesktop)\.App\.Runtime\.[A-Za-z0-9.-]+$') { continue }
            if ($download.version -notmatch '^\[(?<version>[^,\s\[\]]+)(?:,\s*\k<version>)?\]$') {
                throw "Runtime pack $($download.name) lacks an exact restored version: $($download.version)"
            }
            $runtimeVersion = $Matches.version
            if ($runtimeVersion -notmatch '^\d+(?:\.\d+){1,3}(?:-[A-Za-z0-9.-]+)?$') { throw 'Unsupported runtime-pack version spelling.' }
            $runtimeId = "$($download.name)/$runtimeVersion"
            if ($libraries.ContainsKey($runtimeId)) {
                $libraries[$runtimeId].dependencyType = 'runtimepack'
                if (!$libraries[$runtimeId].projects.Contains($project.Key)) { $libraries[$runtimeId].projects.Add($project.Key) }
                continue
            }
            $runtimeRelative = "$($download.name.ToLowerInvariant())/$runtimeVersion"
            $resolved = Resolve-CompletePackage @($assets.packageFolders.PSObject.Properties.Name) $runtimeId $runtimeRelative '' @()
            $runtimeSources = [Collections.Generic.List[string]]::new(); $runtimeSources.Add($project.Key)
            $libraries[$runtimeId] = @{ resolved = $resolved; projects = $runtimeSources; dependencyType = 'runtimepack';
                library = [pscustomobject]@{ files = $resolved.files; sha512 = $resolved.contentSha512 } }
        }
    }
}
$noticeRoot = Join-Path $repo 'licenses/net'
$null = New-Item -ItemType Directory -Path $noticeRoot -Force
$records = [Collections.Generic.List[object]]::new()
$headers = @{ 'User-Agent' = 'SED-MotS-license-inventory'; 'Accept' = 'application/vnd.github+json' }
$repoCache = @{}
foreach ($entry in $libraries.GetEnumerator() | Sort-Object Name) {
    $library = $entry.Value.library
    $resolved = $entry.Value.resolved
    $packageDir = $resolved.directory
    $nuspecPath = $resolved.nuspecPath
    $metadata = $resolved.metadata
    $license = $metadata.SelectSingleNode('*[local-name()="license"]')
    $copyright = $metadata.SelectSingleNode('*[local-name()="copyright"]')
    $repository = $metadata.SelectSingleNode('*[local-name()="repository"]')
    $destinationName = $entry.Name.Replace('/', '-')
    $destination = Join-Path $noticeRoot $destinationName
    $null = New-Item -ItemType Directory -Path $destination -Force
    Copy-Item -LiteralPath $nuspecPath -Destination (Join-Path $destination 'package.nuspec')
    $saved = [Collections.Generic.List[object]]::new()
    foreach ($file in $library.files) {
        if ([IO.Path]::GetFileName($file) -notmatch '(?i)licen[sc]e|copying|notice') { continue }
        $source = Join-Path $packageDir $file
        $name = $file.Replace('/', '_').Replace('\', '_')
        Copy-Item -LiteralPath $source -Destination (Join-Path $destination $name)
        $saved.Add([ordered]@{ file = "$destinationName/$name"; source = "nuget:$($entry.Name)/$file";
            isPackageLicense = ([IO.Path]::GetFileName($file) -match '(?i)licen[sc]e|copying') })
    }
    # A third-party notice file alone is not the package's own license text.
    # Classify original basenames: flattening licenses/NOTICE.txt must not turn
    # a directory name into apparent license text in licenses_NOTICE.txt.
    $hasLicense = @($saved | Where-Object { $_.isPackageLicense }).Count -gt 0
    if (!$hasLicense) {
        if ($repository -and $repository.url -match '^https://github.com/([^/]+)/([^/]+?)(?:\.git)?/?$' -and $repository.commit) {
            $githubRepo = "$($Matches[1])/$($Matches[2])"
            $commit = [string]$repository.commit
            $cacheKey = "$githubRepo/$commit"
            if (!$repoCache.ContainsKey($cacheKey)) {
                $tree = Invoke-RestMethod -Uri "https://api.github.com/repos/$githubRepo/git/trees/$commit" -Headers $headers
                $candidates = @($tree.tree | Where-Object { $_.type -eq 'blob' -and $_.path -match '(?i)^(licen[sc]e|copying|notice)' })
                if ($candidates.Count -eq 0) { throw "No root license found at $cacheKey" }
                $repoCache[$cacheKey] = $candidates
            }
            foreach ($candidate in $repoCache[$cacheKey]) {
                $url = "https://raw.githubusercontent.com/$githubRepo/$commit/$($candidate.path)"
                $name = $candidate.path.Replace('/', '_')
                if (@($saved | Where-Object { $_.file -ceq "$destinationName/$name" }).Count -gt 0) { continue }
                Invoke-WebRequest -Uri $url -OutFile (Join-Path $destination $name)
                $saved.Add([ordered]@{ file = "$destinationName/$name"; source = $url;
                    isPackageLicense = ([IO.Path]::GetFileName($candidate.path) -match '(?i)licen[sc]e|copying') })
            }
        }
        else { throw "No bundled license or pinned GitHub source for $($entry.Name)" }
    }
    # A pinned repository can also contain only third-party notices. Apply the
    # package's own-license requirement to downloaded fallback files as well.
    $hasLicense = @($saved | Where-Object { $_.isPackageLicense }).Count -gt 0
    if (!$hasLicense) { throw "No package license text collected for $($entry.Name)." }
    foreach ($file in $saved) { $file.sha256 = (Get-FileHash -LiteralPath (Join-Path $noticeRoot $file.file) -Algorithm SHA256).Hash.ToLowerInvariant() }
    $records.Add([ordered]@{ package = $entry.Name; dependencyType = $entry.Value.dependencyType;
        contentSha512 = $resolved.contentSha512; archiveSha512 = $resolved.archiveSha512; packageSource = $resolved.packageSource;
        nuspecSha256 = (Get-FileHash -LiteralPath (Join-Path $destination 'package.nuspec') -Algorithm SHA256).Hash.ToLowerInvariant();
        license = [string]$license.InnerText;
        copyright = [string]$copyright.InnerText; repository = [string]$repository.url;
        repositoryCommit = [string]$repository.commit; sourceProjects = @($entry.Value.projects); files = @($saved) })
}
$fontUrl = 'https://raw.githubusercontent.com/phosphor-icons/homepage/master/LICENSE'
Invoke-WebRequest -Uri $fontUrl -OutFile (Join-Path $noticeRoot 'Phosphor-LICENSE.txt')
$sdkRoot = Split-Path -Parent (Resolve-Path -LiteralPath $DotNet).Path
foreach ($name in @('LICENSE.txt', 'ThirdPartyNotices.txt')) {
    Copy-Item -LiteralPath (Join-Path $sdkRoot $name) -Destination (Join-Path $noticeRoot "dotnet-$name")
}
$inventory = [ordered]@{ capturedUtc = [DateTime]::UtcNow.ToString('o');
    scope = 'All Sed.App, Sed.Agent and Sed.Mcp restore packages and exact declared runtime-pack downloads, including build and non-Windows dependencies; this is a superset, not a list of deployed components. Runtime-pack identities in published deps manifests are verified separately. dotnet-LICENSE.txt identifies the SDK distribution; runtime-pack directories preserve their separate licenses.';
    packages = @($records); font = [ordered]@{ source = $fontUrl;
        binarySha256 = (Get-FileHash -LiteralPath (Join-Path $repo 'dotnet/assets/Phosphor.ttf') -Algorithm SHA256).Hash.ToLowerInvariant();
        licenseSha256 = (Get-FileHash -LiteralPath (Join-Path $noticeRoot 'Phosphor-LICENSE.txt') -Algorithm SHA256).Hash.ToLowerInvariant() } }
$inventoryText = ($inventory | ConvertTo-Json -Depth 9).Replace("`r`n", "`n") + "`n"
[IO.File]::WriteAllText((Join-Path $noticeRoot 'inventory.json'), $inventoryText, [Text.UTF8Encoding]::new($false))
Write-Output "Collected notices for $($records.Count) NuGet packages, Phosphor and the .NET distribution."
