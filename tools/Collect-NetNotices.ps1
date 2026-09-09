[CmdletBinding()]
param([Parameter(Mandatory)][string]$DotNet)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$assetsPath = Join-Path $repo 'dotnet/src/Sed.App/obj/project.assets.json'
if (!(Test-Path -LiteralPath $assetsPath)) { throw 'Build/restore Sed.App before collecting notices.' }
$assets = Get-Content -LiteralPath $assetsPath -Raw | ConvertFrom-Json
$packageRoot = $assets.packageFolders.PSObject.Properties.Name | Select-Object -First 1
$noticeRoot = Join-Path $repo 'licenses/net'
$null = New-Item -ItemType Directory -Path $noticeRoot -Force
$records = [Collections.Generic.List[object]]::new()
$headers = @{ 'User-Agent' = 'SED-MotS-license-inventory'; 'Accept' = 'application/vnd.github+json' }
$repoCache = @{}
foreach ($entry in $assets.libraries.PSObject.Properties | Sort-Object Name) {
    $library = $entry.Value
    if ($library.type -ne 'package') { continue }
    $packageDir = Join-Path $packageRoot $library.path
    $nuspecPath = Get-ChildItem -LiteralPath $packageDir -Filter '*.nuspec' -File | Select-Object -First 1
    [xml]$nuspec = Get-Content -LiteralPath $nuspecPath.FullName -Raw
    $metadata = $nuspec.SelectSingleNode('//*[local-name()="metadata"]')
    $license = $metadata.SelectSingleNode('*[local-name()="license"]')
    $copyright = $metadata.SelectSingleNode('*[local-name()="copyright"]')
    $repository = $metadata.SelectSingleNode('*[local-name()="repository"]')
    $destinationName = $entry.Name.Replace('/', '-')
    $destination = Join-Path $noticeRoot $destinationName
    $null = New-Item -ItemType Directory -Path $destination -Force
    Copy-Item -LiteralPath $nuspecPath.FullName -Destination (Join-Path $destination 'package.nuspec')
    $saved = [Collections.Generic.List[object]]::new()
    foreach ($file in $library.files) {
        if ([IO.Path]::GetFileName($file) -notmatch '(?i)licen[sc]e|notice') { continue }
        $source = Join-Path $packageDir $file
        $name = $file.Replace('/', '_').Replace('\', '_')
        Copy-Item -LiteralPath $source -Destination (Join-Path $destination $name)
        $saved.Add([ordered]@{ file = "$destinationName/$name"; source = "nuget:$($entry.Name)/$file" })
    }
    if ($saved.Count -eq 0) {
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
                Invoke-WebRequest -Uri $url -OutFile (Join-Path $destination $name)
                $saved.Add([ordered]@{ file = "$destinationName/$name"; source = $url })
            }
        }
        else { throw "No bundled license or pinned GitHub source for $($entry.Name)" }
    }
    foreach ($file in $saved) { $file.sha256 = (Get-FileHash -LiteralPath (Join-Path $noticeRoot $file.file) -Algorithm SHA256).Hash.ToLowerInvariant() }
    $records.Add([ordered]@{ package = $entry.Name; license = [string]$license.InnerText;
        copyright = [string]$copyright.InnerText; repository = [string]$repository.url;
        repositoryCommit = [string]$repository.commit; files = @($saved) })
}
$fontUrl = 'https://raw.githubusercontent.com/phosphor-icons/homepage/master/LICENSE'
Invoke-WebRequest -Uri $fontUrl -OutFile (Join-Path $noticeRoot 'Phosphor-LICENSE.txt')
$sdkRoot = Split-Path -Parent (Resolve-Path -LiteralPath $DotNet).Path
foreach ($name in @('LICENSE.txt', 'ThirdPartyNotices.txt')) {
    Copy-Item -LiteralPath (Join-Path $sdkRoot $name) -Destination (Join-Path $noticeRoot "dotnet-$name")
}
$inventory = [ordered]@{ capturedUtc = [DateTime]::UtcNow.ToString('o');
    scope = 'All Sed.App restore packages, including build and non-Windows dependencies; this is a superset of the Windows runtime.';
    packages = @($records); font = [ordered]@{ source = $fontUrl;
        binarySha256 = (Get-FileHash -LiteralPath (Join-Path $repo 'dotnet/assets/Phosphor.ttf') -Algorithm SHA256).Hash.ToLowerInvariant();
        licenseSha256 = (Get-FileHash -LiteralPath (Join-Path $noticeRoot 'Phosphor-LICENSE.txt') -Algorithm SHA256).Hash.ToLowerInvariant() } }
$inventory | ConvertTo-Json -Depth 9 | Set-Content -LiteralPath (Join-Path $noticeRoot 'inventory.json') -Encoding utf8
Write-Output "Collected notices for $($records.Count) NuGet packages, Phosphor and the .NET distribution."
