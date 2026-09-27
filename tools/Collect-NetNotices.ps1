[CmdletBinding()]
param([Parameter(Mandatory)][string]$DotNet)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$projects = [ordered]@{
    'Sed.App' = 'dotnet/src/Sed.App/obj/project.assets.json'
    'Sed.Agent' = 'dotnet/tools/Sed.Agent/obj/project.assets.json'
    'Sed.Mcp' = 'dotnet/tools/Sed.Mcp/obj/project.assets.json'
}
$libraries = @{}
foreach ($project in $projects.GetEnumerator()) {
    $assetsPath = Join-Path $repo $project.Value
    if (!(Test-Path -LiteralPath $assetsPath)) { throw "Build/restore $($project.Key) before collecting notices." }
    $assets = Get-Content -LiteralPath $assetsPath -Raw | ConvertFrom-Json
    foreach ($entry in $assets.libraries.PSObject.Properties) {
        if ($entry.Value.type -ne 'package') { continue }
        if ($libraries.ContainsKey($entry.Name)) {
            if ($libraries[$entry.Name].library.sha512 -cne $entry.Value.sha512) { throw "Different restored identities for $($entry.Name)." }
            $libraries[$entry.Name].projects.Add($project.Key)
            continue
        }
        $packageDirectories = @($assets.packageFolders.PSObject.Properties.Name | ForEach-Object { Join-Path $_ $entry.Value.path } |
            Where-Object { Test-Path -LiteralPath $_ -PathType Container })
        if ($packageDirectories.Count -eq 0) { throw "Missing restored package: $($entry.Name)" }
        $sourceProjects = [Collections.Generic.List[string]]::new(); $sourceProjects.Add($project.Key)
        $libraries[$entry.Name] = @{ library = $entry.Value; directory = $packageDirectories[0]; projects = $sourceProjects; dependencyType = 'package' }
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
            $runtimeDirectories = @($assets.packageFolders.PSObject.Properties.Name | ForEach-Object { Join-Path $_ $runtimeRelative } |
                Where-Object { Test-Path -LiteralPath $_ -PathType Container })
            if ($runtimeDirectories.Count -eq 0) { throw "Missing restored runtime pack: $runtimeId" }
            $runtimeDirectory = $runtimeDirectories[0]
            $runtimeSources = [Collections.Generic.List[string]]::new(); $runtimeSources.Add($project.Key)
            $runtimeFiles = @(Get-ChildItem -LiteralPath $runtimeDirectory -File | Select-Object -ExpandProperty Name)
            $runtimeHashPath = Join-Path $runtimeDirectory "$($download.name.ToLowerInvariant()).$runtimeVersion.nupkg.sha512"
            $libraries[$runtimeId] = @{ directory = $runtimeDirectory; projects = $runtimeSources; dependencyType = 'runtimepack';
                library = [pscustomobject]@{ files = $runtimeFiles; sha512 = ([IO.File]::ReadAllText($runtimeHashPath)).Trim() } }
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
    $packageDir = $entry.Value.directory
    $nuspecPath = Get-ChildItem -LiteralPath $packageDir -Filter '*.nuspec' -File | Select-Object -First 1
    [xml]$nuspec = Get-Content -LiteralPath $nuspecPath.FullName -Raw
    $metadata = $nuspec.SelectSingleNode('//*[local-name()="metadata"]')
    if ($entry.Value.dependencyType -eq 'runtimepack' -and "$($metadata.id)/$($metadata.version)" -cne $entry.Name) {
        throw "Runtime-pack nuspec identity differs from restored download: $($entry.Name)"
    }
    $license = $metadata.SelectSingleNode('*[local-name()="license"]')
    $copyright = $metadata.SelectSingleNode('*[local-name()="copyright"]')
    $repository = $metadata.SelectSingleNode('*[local-name()="repository"]')
    $destinationName = $entry.Name.Replace('/', '-')
    $destination = Join-Path $noticeRoot $destinationName
    $null = New-Item -ItemType Directory -Path $destination -Force
    Copy-Item -LiteralPath $nuspecPath.FullName -Destination (Join-Path $destination 'package.nuspec')
    $saved = [Collections.Generic.List[object]]::new()
    foreach ($file in $library.files) {
        if ([IO.Path]::GetFileName($file) -notmatch '(?i)licen[sc]e|copying|notice') { continue }
        $source = Join-Path $packageDir $file
        $name = $file.Replace('/', '_').Replace('\', '_')
        Copy-Item -LiteralPath $source -Destination (Join-Path $destination $name)
        $saved.Add([ordered]@{ file = "$destinationName/$name"; source = "nuget:$($entry.Name)/$file" })
    }
    # A third-party notice file alone is not the package's own license text.
    $hasLicense = @($saved | Where-Object { [IO.Path]::GetFileName($_.file) -match '(?i)licen[sc]e|copying' }).Count -gt 0
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
                $saved.Add([ordered]@{ file = "$destinationName/$name"; source = $url })
            }
        }
        else { throw "No bundled license or pinned GitHub source for $($entry.Name)" }
    }
    foreach ($file in $saved) { $file.sha256 = (Get-FileHash -LiteralPath (Join-Path $noticeRoot $file.file) -Algorithm SHA256).Hash.ToLowerInvariant() }
    $records.Add([ordered]@{ package = $entry.Name; dependencyType = $entry.Value.dependencyType;
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
