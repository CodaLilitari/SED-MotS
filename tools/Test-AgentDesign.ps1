#requires -Version 7.2
<#
.SYNOPSIS
Exercises a reviewed JK/MotS room-design workflow using only the agent process.
.DESCRIPTION
Creates synthetic levels in a new directory and records exact requests, replies
and checks. Fictional MAT/CMP names are metadata examples, not supplied assets.
Snapshots are flat diagnostic geometry, not textured/gameplay evidence. No GUI,
shell child, game, native picker or network service is started by this script.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$AgentPath,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [switch]$SkipSnapshot
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$utf8 = [Text.UTF8Encoding]::new($false, $true)
$agent = (Resolve-Path -LiteralPath $AgentPath).ProviderPath
if (-not (Test-Path -LiteralPath $agent -PathType Leaf)) { throw 'AgentPath must name an executable file.' }
$scratch = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputDirectory)
if (Test-Path -LiteralPath $scratch) { throw 'OutputDirectory must be new; existing paths are never reused.' }
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
    gameLaunched = $false
    processTimeoutSeconds = 30
    snapshot = $(if ($SkipSnapshot) { 'skipped by request' } else { 'pending' })
    imageVisuallyInspected = $false
    limitations = @(
        'Fictional material and colormap registration is metadata only; no resource files are supplied or loaded.',
        'A player template, if available, is placed without verifying its game resources or scripts.',
        'Snapshots show flat diagnostic polygon geometry, not materials, game lighting or thing models.',
        'Structural and image checks do not establish engine loading or gameplay.'
    )
    steps = [Collections.Generic.List[object]]::new()
    checks = [Collections.Generic.List[object]]::new()
    outputs = [Collections.Generic.List[object]]::new()
}

function Get-Hash([string]$Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() }
function Convert-Request($Value) { ConvertTo-Json -InputObject $Value -Depth 50 -Compress }
function Confirm-Check([string]$Name, [bool]$Condition) {
    $receipt.checks.Add([ordered]@{ name = $Name; passed = $Condition })
    if (-not $Condition) { throw "Design assertion failed: $Name" }
}
function Invoke-Agent {
    param([string]$Name, [string[]]$Arguments, [AllowEmptyString()][string]$InputText = '', [int]$ExpectedExit = 0)
    $stem = '{0:D2}-{1}' -f ($receipt.steps.Count + 1), $Name
    $inputBytes = $utf8.GetBytes($InputText)
    $step = [ordered]@{
        name = $Name; executable = $agent; arguments = $Arguments
        stdinFile = $(if ($InputText.Length) { "$stem.request.json" } else { "$stem.stdin.txt" })
        stdinBytes = $inputBytes.Length
        stdinSha256 = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($inputBytes)).ToLowerInvariant()
        expectedExitCode = $ExpectedExit; exitCode = $null; timedOut = $false
        stdoutFile = "$stem.stdout.json"; stderrFile = "$stem.stderr.txt"
        stdoutJson = $null; passed = $false
    }
    [IO.File]::WriteAllBytes((Join-Path $scratch $step.stdinFile), $inputBytes)
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
            $remaining = [Math]::Max(0, 30000 - [int]$clock.ElapsedMilliseconds)
            if (-not $writeTask.Wait($remaining)) { $step.timedOut = $true; throw 'Agent stdin write timed out.' }
            $null = $writeTask.GetAwaiter().GetResult()
        }
        catch {
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
        if ($started -and -not $process.HasExited) {
            $process.Kill($true)
            $null = $process.WaitForExit(5000)
        }
        # Retain available host diagnostics even after a timeout or parsing failure.
        if ($null -ne $stdoutTask -and $stdoutTask.IsCompletedSuccessfully) {
            [IO.File]::WriteAllText((Join-Path $scratch $step.stdoutFile), $stdoutTask.GetAwaiter().GetResult(), $utf8)
        }
        if ($null -ne $stderrTask -and $stderrTask.IsCompletedSuccessfully) {
            [IO.File]::WriteAllText((Join-Path $scratch $step.stderrFile), $stderrTask.GetAwaiter().GetResult(), $utf8)
        }
        $step.elapsedMilliseconds = $clock.ElapsedMilliseconds
        $process.Dispose()
    }
}
function Read-Collection {
    param([string]$Name, [string]$LevelPath, [string]$Collection, [int]$Sector = -1, [string]$Search = '')
    $items = [Collections.Generic.List[object]]::new()
    $offset = 0
    $revision = $null
    $inputHash = $null
    do {
        if ($offset -gt 20000) { throw 'Synthetic design query exceeded the harness paging bound.' }
        $request = @{ schemaVersion = 1; collection = $Collection; offset = $offset; limit = 200 }
        if ($Sector -ge 0) { $request.sector = $Sector }
        if ($Search.Length) { $request.search = $Search }
        $page = Invoke-Agent "$Name-page-$offset" @('query', $LevelPath, '-') (Convert-Request $request)
        if ($null -eq $revision) { $revision = $page.sourceRevision; $inputHash = $page.inputSha256 }
        elseif ($revision -ne $page.sourceRevision -or $inputHash -ne $page.inputSha256) { throw 'Source changed between design query pages.' }
        foreach ($item in $page.items) { $items.Add($item) }
        if ($null -ne $page.nextOffset -and $page.nextOffset -le $offset) { throw 'Query continuation did not advance.' }
        $offset = $page.nextOffset
    } while ($null -ne $offset)
    return @{ items = $items.ToArray(); inputSha256 = $inputHash; sourceRevision = $revision }
}
function Commit-Design {
    param([string]$Name, [string]$InputPath, [object[]]$Operations)
    $source = Read-Collection "$Name-source" $InputPath 'summary'
    $patch = Convert-Request @{ schemaVersion = 1; inputSha256 = $source.inputSha256; sourceRevision = $source.sourceRevision; operations = $Operations }
    $plan = Invoke-Agent "$Name-plan" @('plan', $InputPath, '-') $patch
    Confirm-Check "$Name plan retains the source bytes" ((Get-Hash $InputPath) -eq $source.inputSha256)
    $output = Join-Path $scratch "$Name.jkl"
    $applied = Invoke-Agent "$Name-apply" @('apply', $InputPath, '-', $output, $plan.outputSha256) $patch
    Confirm-Check "$Name apply retains the source bytes" ((Get-Hash $InputPath) -eq $source.inputSha256)
    $hash = Get-Hash $output
    Confirm-Check "$Name output matches reviewed hash" ($hash -eq $plan.outputSha256 -and $hash -eq $applied.plan.outputSha256)
    $valid = Invoke-Agent "$Name-validate" @('validate', $output)
    Confirm-Check "$Name passes structural validation" ([bool]$valid.valid -and $valid.inputSha256 -eq $hash)
    $receipt.outputs.Add(@{ path = $output; sha256 = $hash; kind = 'synthetic JKL'; structurallyValid = $true })
    return @{ path = $output; hash = $hash; plan = $plan }
}
function Find-OpenFace([object[]]$Surfaces, [int]$Axis, [int]$Sign) {
    $candidates = @($Surfaces | Where-Object { $_.adjoinSector -lt 0 -and ([double]$_.normal[$Axis] * $Sign) -gt 0.999999 })
    if ($candidates.Count -ne 1) { throw "Expected one open axis-aligned face for axis $Axis sign $Sign; got $($candidates.Count)." }
    return $candidates[0]
}
function Get-Center($Bounds) {
    return ,@(
        (([double]$Bounds.min[0] + [double]$Bounds.max[0]) / 2),
        (([double]$Bounds.min[1] + [double]$Bounds.max[1]) / 2),
        (([double]$Bounds.min[2] + [double]$Bounds.max[2]) / 2)
    )
}
function Test-VectorEqual([object[]]$Actual, [object[]]$Expected) {
    if ($Actual.Count -ne $Expected.Count) { return $false }
    for ($coordinate = 0; $coordinate -lt $Actual.Count; $coordinate++) {
        if (-not [double]::IsFinite([double]$Actual[$coordinate]) -or
            [Math]::Abs([double]$Actual[$coordinate] - [double]$Expected[$coordinate]) -gt 0.000000001) { return $false }
    }
    return $true
}
function Get-CreatedSector($Plan) {
    $effects = @($Plan.changes | Where-Object operation -EQ 'extrudeSurface')
    if ($effects.Count -ne 1 -or $null -eq $effects[0].serializedFinal.target.createdSector) { throw 'Missing extrusion target receipt.' }
    return [int]$effects[0].serializedFinal.target.createdSector
}

try {
    $version = Invoke-Agent 'version' @('--version')
    Confirm-Check 'agent provides stdin and reviewed-output capabilities' ($version.name -eq 'sed-agent' -and 'stdin-json' -in $version.capabilities -and 'reviewed-output-hash' -in $version.capabilities)
    $receipt.agentVersion = $version.version
    $null = Invoke-Agent 'command-schema' @('schema', 'commands')
    $null = Invoke-Agent 'query-schema' @('schema', 'query')
    foreach ($operation in @('registerMaterial', 'registerColormap', 'setSectorColormap', 'setSurfaceMaterial', 'fitSurfaceTexture', 'setSurfaceUv', 'setSurfaceRenderModes', 'extrudeSurface', 'adjoinSurfaces', 'addThing', 'addLight', 'setLight', 'addBox')) {
        $schema = Invoke-Agent "schema-$operation" @('schema', $operation)
        Confirm-Check "$operation schema is available" ($schema.type -eq 'object')
    }
    if (-not $SkipSnapshot) { $null = Invoke-Agent 'snapshot-schema' @('schema', 'snapshot') }

    $original = Join-Path $scratch 'mots-original.jkl'
    $created = Invoke-Agent 'new-mots' @('new', $original, 'mots')
    $originalHash = Get-Hash $original
    Confirm-Check 'MotS scaffold hash matches' ($originalHash -eq $created.inputSha256)
    $originalValid = Invoke-Agent 'validate-mots-original' @('validate', $original)
    Confirm-Check 'MotS scaffold validates' ([bool]$originalValid.valid)
    $receipt.outputs.Add(@{ path = $original; sha256 = $originalHash; kind = 'synthetic JKL'; structurallyValid = $true })

    $materialName = 'agent_demo_panel.mat'
    $colormapName = 'agent_demo_palette.cmp'
    $registered = Commit-Design 'mots-registered' $original @(
        @{ op = 'registerMaterial'; name = $materialName },
        @{ op = 'registerColormap'; name = $colormapName },
        @{ op = 'addLight'; position = @(0, 0, 0); rgb = @(1, 0.5, 0.25); intensity = 0.375; range = 4 },
        @{ op = 'addLight'; position = @(0.25, 0, 0); rgb = @(0.25, 1, 0.5); intensity = 0.625; rgbIntensity = 0; range = 5; legacyRgbRange = 99 },
        @{ op = 'addLight'; position = @(-0.25, 0, 0); rgb = @(0.5, 0.25, 1); intensity = 0.875; rgbIntensity = 0.875; range = 6; legacyRgbRange = 0 }
    )
    $sourceLights = Read-Collection 'source-lights-before-geometry' $registered.path 'lights'
    Confirm-Check 'source light fixture retains inherited zero and explicit-equal RGB' (
        $sourceLights.items.Count -eq 3 -and $sourceLights.items[0].rgbIntensityInherited -and
        $null -eq $sourceLights.items[0].rgbIntensity -and $null -eq $sourceLights.items[0].legacyRgbRange -and
        -not $sourceLights.items[1].rgbIntensityInherited -and $sourceLights.items[1].rgbIntensity -eq 0 -and
        $sourceLights.items[1].legacyRgbRange -eq 99 -and -not $sourceLights.items[2].rgbIntensityInherited -and
        $sourceLights.items[2].rgbIntensity -eq $sourceLights.items[2].intensity -and $sourceLights.items[2].legacyRgbRange -eq 0)
    $sourceLightState = Convert-Request $sourceLights.items
    Confirm-Check 'source lights use a validated authoring metadata extension' ($registered.plan.outputSourceLightMetadata.state -eq 'validated')
    $materials = Read-Collection 'registered-materials' $registered.path 'materials'
    $colormaps = Read-Collection 'registered-colormaps' $registered.path 'colormaps'
    $material = @($materials.items | Where-Object name -EQ $materialName)
    $colormap = @($colormaps.items | Where-Object name -EQ $colormapName)
    Confirm-Check 'fictional resource metadata appears once' ($material.Count -eq 1 -and $colormap.Count -eq 1)
    $materialIndex = [int]$material[0].index
    $colormapIndex = [int]$colormap[0].index
    Confirm-Check 'registration did not create resource payloads' (-not (Test-Path -LiteralPath (Join-Path $scratch $materialName)) -and -not (Test-Path -LiteralPath (Join-Path $scratch $colormapName)))

    $initialSurfaces = Read-Collection 'initial-surfaces' $registered.path 'surfaces' 0
    $eastFace = Find-OpenFace $initialSurfaces.items 0 -1
    $first = Commit-Design 'mots-east-room' $registered.path @(
        @{ op = 'setSectorColormap'; sector = 0; colormapIndex = $colormapIndex },
        @{ op = 'setSurfaceMaterial'; sector = 0; surface = [int]$eastFace.index; materialIndex = $materialIndex },
        @{ op = 'fitSurfaceTexture'; sector = 0; surface = [int]$eastFace.index; width = 64; height = 64 },
        @{ op = 'extrudeSurface'; sector = 0; surface = [int]$eastFace.index; distance = 2 }
    )
    $firstSector = Get-CreatedSector $first.plan
    $firstSurfaces = Read-Collection 'east-room-surfaces' $first.path 'surfaces' $firstSector
    $northFace = Find-OpenFace $firstSurfaces.items 1 -1
    $dot = 0.0
    for ($axis = 0; $axis -lt 3; $axis++) { $dot += [double]$eastFace.normal[$axis] * [double]$northFace.normal[$axis] }
    Confirm-Check 'second extrusion is perpendicular to the first' ([Math]::Abs($dot) -lt 0.000001)
    $second = Commit-Design 'mots-north-room' $first.path @(
        @{ op = 'extrudeSurface'; sector = $firstSector; surface = [int]$northFace.index; distance = 2 }
    )
    $secondSector = Get-CreatedSector $second.plan
    Confirm-Check 'extrusions report distinct new sectors' ($firstSector -ne 0 -and $secondSector -ne 0 -and $firstSector -ne $secondSector)

    $sectorsBeforeTexture = Read-Collection 'rooms-before-texture' $second.path 'sectors'
    $textureOperations = [Collections.Generic.List[object]]::new()
    $manualUvSurface = $null
    $manualUv = @(@(0, 0), @(128, 0), @(128, 64), @(0, 64))
    foreach ($sector in $sectorsBeforeTexture.items) {
        $textureOperations.Add(@{ op = 'setSectorColormap'; sector = [int]$sector.index; colormapIndex = $colormapIndex })
        $surfaces = Read-Collection "texture-candidates-$($sector.index)" $second.path 'surfaces' ([int]$sector.index)
        foreach ($surface in $surfaces.items) {
            if ($surface.adjoinSector -ge 0) { continue }
            $textureOperations.Add(@{ op = 'setSurfaceMaterial'; sector = [int]$sector.index; surface = [int]$surface.index; materialIndex = $materialIndex })
            $textureOperations.Add(@{ op = 'setSurfaceRenderModes'; sector = [int]$sector.index; surface = [int]$surface.index; geometryMode = 4; lightMode = 3; textureMode = 3 })
            $textureOperations.Add(@{ op = 'fitSurfaceTexture'; sector = [int]$sector.index; surface = [int]$surface.index; width = 64; height = 64 })
            if ($sector.index -eq 0 -and [double]$surface.normal[2] -gt 0.999999 -and @($surface.corners).Count -eq $manualUv.Count) {
                $manualUvSurface = [int]$surface.index
                $textureOperations.Add(@{ op = 'setSurfaceUv'; sector = 0; surface = $manualUvSurface; uv = $manualUv })
            }
        }
    }
    Confirm-Check 'original room floor selected for explicit UVs' ($null -ne $manualUvSurface)
    $templates = Read-Collection 'player-templates' $second.path 'templates' -1 'walkplayer'
    $playerTemplate = @($templates.items | Where-Object { $_.name -eq 'walkplayer' -and $_.parameters.type -eq 'player' })
    $originalSector = @($sectorsBeforeTexture.items | Where-Object index -EQ 0)[0]
    $originCenter = Get-Center $originalSector.geometryBounds
    if ($playerTemplate.Count -eq 1) {
        $textureOperations.Add(@{ op = 'addThing'; sector = 0; template = $playerTemplate[0].name; name = 'agent_start'; position = $originCenter })
        $receipt.player = @{ status = 'placed template metadata'; template = $playerTemplate[0].name; gameResourcesVerified = $false }
    }
    else { $receipt.player = @{ status = 'skipped'; reason = 'One exact walkplayer template with type=player was not available.' } }
    $finished = Commit-Design 'mots-design' $second.path $textureOperations.ToArray()
    $finishedLights = Read-Collection 'source-lights-after-geometry' $finished.path 'lights'
    Confirm-Check 'all source light fields survive geometry and material edits' ((Convert-Request $finishedLights.items) -ceq $sourceLightState)
    Confirm-Check 'final design retains validated source light metadata' ($finished.plan.outputSourceLightMetadata.state -eq 'validated')

    $sectors = Read-Collection 'final-sectors' $finished.path 'sectors'
    $surfaceMap = @{}
    foreach ($sector in $sectors.items) {
        Confirm-Check "sector $($sector.index) retains selected colormap" ($sector.colorMap -eq $colormapName)
        $surfaces = Read-Collection "final-surfaces-$($sector.index)" $finished.path 'surfaces' ([int]$sector.index)
        $vertices = Read-Collection "final-vertices-$($sector.index)" $finished.path 'vertices' ([int]$sector.index)
        Confirm-Check "sector $($sector.index) vertex readback matches declared count" (@($vertices.items).Count -eq $sector.vertices)
        $surfaceMap[[int]$sector.index] = $surfaces.items
        $center = Get-Center $sector.geometryBounds
        foreach ($surface in $surfaces.items) {
            $inward = 0.0
            for ($axis = 0; $axis -lt 3; $axis++) { $inward += [double]$surface.normal[$axis] * ($center[$axis] - [double]$surface.centroid[$axis]) }
            Confirm-Check "sector $($sector.index) surface $($surface.index) faces inward" ($inward -gt 0.000001)
            if ($surface.adjoinSector -ge 0) { Confirm-Check "portal $($sector.index)/$($surface.index) remains open" ($surface.geo -eq 0) }
            else {
                Confirm-Check "solid $($sector.index)/$($surface.index) retains material and render modes" ($surface.material -eq $materialName -and $surface.materialIndex -eq $materialIndex -and $surface.geo -eq 4 -and $surface.light -eq 3 -and $surface.tex -eq 3)
                $u = @($surface.corners | ForEach-Object { [double]$_.uv[0] })
                $v = @($surface.corners | ForEach-Object { [double]$_.uv[1] })
                Confirm-Check "solid $($sector.index)/$($surface.index) has finite varying UVs" (@($u + $v | Where-Object { -not [double]::IsFinite($_) }).Count -eq 0 -and ($u | Measure-Object -Maximum).Maximum -gt ($u | Measure-Object -Minimum).Minimum -and ($v | Measure-Object -Maximum).Maximum -gt ($v | Measure-Object -Minimum).Minimum)
            }
        }
    }
    foreach ($sector in $sectors.items) {
        foreach ($surface in $surfaceMap[[int]$sector.index]) {
            if ($surface.adjoinSector -lt 0) { continue }
            $other = @($surfaceMap[[int]$surface.adjoinSector] | Where-Object index -EQ $surface.adjoinSurface)
            Confirm-Check "portal $($sector.index)/$($surface.index) has reciprocal readback" ($other.Count -eq 1 -and $other[0].adjoinSector -eq $sector.index -and $other[0].adjoinSurface -eq $surface.index)
        }
    }
    $sourceRoom = @($sectors.items | Where-Object index -EQ 0)[0]
    $eastRoom = @($sectors.items | Where-Object index -EQ $firstSector)[0]
    $northRoom = @($sectors.items | Where-Object index -EQ $secondSector)[0]
    Confirm-Check 'MotS design has exactly three rooms' (@($sectors.items).Count -eq 3)
    Confirm-Check 'source room retains its original bounds' ((Test-VectorEqual $sourceRoom.geometryBounds.min @(-1, -1, -1)) -and (Test-VectorEqual $sourceRoom.geometryBounds.max @(1, 1, 1)))
    Confirm-Check 'east room extends exactly two units outward' ((Test-VectorEqual $eastRoom.geometryBounds.min @(1, -1, -1)) -and (Test-VectorEqual $eastRoom.geometryBounds.max @(3, 1, 1)))
    Confirm-Check 'north room extends exactly two units outward' ((Test-VectorEqual $northRoom.geometryBounds.min @(1, 1, -1)) -and (Test-VectorEqual $northRoom.geometryBounds.max @(3, 3, 1)))
    Confirm-Check 'neighbors describe exactly the intended L-shaped chain' ((Test-VectorEqual @($sourceRoom.neighbors | Sort-Object) @($firstSector)) -and (Test-VectorEqual @($eastRoom.neighbors | Sort-Object) @(0, $secondSector)) -and (Test-VectorEqual @($northRoom.neighbors | Sort-Object) @($firstSector)))
    $floor = @($surfaceMap[0] | Where-Object index -EQ $manualUvSurface)[0]
    $uvMatches = @($floor.corners).Count -eq $manualUv.Count
    for ($corner = 0; $uvMatches -and $corner -lt $manualUv.Count; $corner++) {
        $uvMatches = Test-VectorEqual $floor.corners[$corner].uv $manualUv[$corner]
    }
    Confirm-Check 'explicit floor UVs survive serialization in corner order' $uvMatches
    $things = Read-Collection 'final-things' $finished.path 'things'
    if ($receipt.player.status -eq 'placed template metadata') {
        $player = @($things.items | Where-Object name -EQ 'agent_start')
        Confirm-Check 'player metadata reads back in original sector' ($player.Count -eq 1 -and $player[0].template -eq 'walkplayer' -and $player[0].sector -eq 0)
    }
    $occupiedPatch = Convert-Request @{ schemaVersion = 1; inputSha256 = $finished.hash; operations = @(@{ op = 'extrudeSurface'; sector = 0; surface = [int]$eastFace.index; distance = 1 }) }
    $occupied = Invoke-Agent 'occupied-portal-refusal' @('plan', $finished.path, '-') $occupiedPatch 2
    Confirm-Check 'occupied portal refuses a second extrusion without mutation' ($occupied.error -eq 'invalid_request' -and $occupied.message -match 'already adjoined' -and (Get-Hash $finished.path) -eq $finished.hash)

    $lateOutput = Join-Path $scratch 'late-failure-refused.jkl'
    $latePatch = Convert-Request @{ schemaVersion = 1; inputSha256 = $finished.hash; operations = @(
        @{ op = 'setLight'; light = [int]$finishedLights.items[0].index; intensity = 9 },
        @{ op = 'extrudeSurface'; sector = 0; surface = [int]$eastFace.index; distance = 1 }) }
    $late = Invoke-Agent 'late-geometry-failure' @('apply', $finished.path, '-', $lateOutput) $latePatch 2
    Confirm-Check 'late geometry failure does not persist the earlier light edit' ($late.error -eq 'invalid_request' -and $late.details.operationIndex -eq 1 -and -not (Test-Path -LiteralPath $lateOutput) -and (Get-Hash $finished.path) -eq $finished.hash)

    # Deliberately opaque synthetic negative fixture, not an authored design step.
    $opaque = Join-Path $scratch 'opaque-reference-input.jkl'
    [IO.File]::WriteAllText($opaque, [IO.File]::ReadAllText($original) + "`nSECTION: AGENT_UNKNOWN_REFERENCES`n0: 0`nend`n", $utf8)
    $opaqueHash = Get-Hash $opaque
    $opaqueOutput = Join-Path $scratch 'opaque-box-refused.jkl'
    $opaquePatch = Convert-Request @{ schemaVersion = 1; inputSha256 = $opaqueHash; operations = @(@{ op = 'addBox'; center = @(10, 0, 0); halfSize = 1 }) }
    $opaqueResult = Invoke-Agent 'opaque-box-refusal' @('apply', $opaque, '-', $opaqueOutput) $opaquePatch 2
    Confirm-Check 'box append refuses unknown reference sections without output' ($opaqueResult.error -eq 'invalid_request' -and $opaqueResult.message -match 'AGENT_UNKNOWN_REFERENCES' -and -not (Test-Path -LiteralPath $opaqueOutput) -and (Get-Hash $opaque) -eq $opaqueHash)

    $jkOriginal = Join-Path $scratch 'jk-original.jkl'
    $jkCreated = Invoke-Agent 'new-jk' @('new', $jkOriginal, 'jk')
    $jkValid = Invoke-Agent 'validate-jk-original' @('validate', $jkOriginal)
    Confirm-Check 'JK scaffold validates and matches reported bytes' ([bool]$jkValid.valid -and (Get-Hash $jkOriginal) -eq $jkCreated.inputSha256)
    $receipt.outputs.Add(@{ path = $jkOriginal; sha256 = (Get-Hash $jkOriginal); kind = 'synthetic JKL'; structurallyValid = $true })
    $jkSurfaces = Read-Collection 'jk-source-surfaces' $jkOriginal 'surfaces' 0
    $jkFace = Find-OpenFace $jkSurfaces.items 0 -1
    $jkDesign = Commit-Design 'jk-connected-rooms' $jkOriginal @(@{ op = 'extrudeSurface'; sector = 0; surface = [int]$jkFace.index; distance = 2 })
    $jkAppended = Get-CreatedSector $jkDesign.plan
    $jkSectors = Read-Collection 'jk-final-sectors' $jkDesign.path 'sectors'
    $jkRoot = @($jkSectors.items | Where-Object index -EQ 0)[0]
    $jkNeighbor = @($jkSectors.items | Where-Object index -EQ $jkAppended)[0]
    Confirm-Check 'JK design has exactly two rooms' (@($jkSectors.items).Count -eq 2)
    Confirm-Check 'JK original room retains its bounds' ((Test-VectorEqual $jkRoot.geometryBounds.min @(-1, -1, -1)) -and (Test-VectorEqual $jkRoot.geometryBounds.max @(1, 1, 1)))
    Confirm-Check 'JK new room extends exactly two units outward' ((Test-VectorEqual $jkNeighbor.geometryBounds.min @(1, -1, -1)) -and (Test-VectorEqual $jkNeighbor.geometryBounds.max @(3, 1, 1)))
    Confirm-Check 'JK connected rooms report exact reciprocal neighbors' ((Test-VectorEqual @($jkRoot.neighbors) @($jkAppended)) -and (Test-VectorEqual @($jkNeighbor.neighbors) @(0)))

    if (-not $SkipSnapshot) {
        $eastCenter = Get-Center $eastRoom.geometryBounds
        $northCenter = Get-Center $northRoom.geometryBounds
        $cameras = @(
            @{ name = 'mots-east-interior'; position = $originCenter; target = $eastCenter },
            @{ name = 'mots-north-interior'; position = $eastCenter; target = $northCenter }
        )
        $firstImage = $null
        $firstImageHash = $null
        $firstRequest = $null
        foreach ($camera in $cameras) {
            $request = @{ schemaVersion = 1; inputSha256 = $finished.hash; sourceRevision = $finished.hash; position = $camera.position; target = $camera.target; fieldOfViewDegrees = 90; width = 640; height = 480; nearPlane = 0.01; farPlane = 20 }
            $requestText = Convert-Request $request
            $png = Join-Path $scratch "$($camera.name).png"
            $snapshot = Invoke-Agent $camera.name @('snapshot', $finished.path, '-', $png) $requestText
            $pngHash = Get-Hash $png
            $bytes = [IO.File]::ReadAllBytes($png)
            Confirm-Check "$($camera.name) writes a PNG with reported hash" ($bytes.Length -ge 24 -and [BitConverter]::ToString($bytes, 0, 8) -eq '89-50-4E-47-0D-0A-1A-0A' -and $pngHash -eq $snapshot.outputSha256)
            $width = [Net.IPAddress]::NetworkToHostOrder([BitConverter]::ToInt32($bytes, 16))
            $height = [Net.IPAddress]::NetworkToHostOrder([BitConverter]::ToInt32($bytes, 20))
            Confirm-Check "$($camera.name) respects camera and image bounds" ($width -eq 640 -and $height -eq 480 -and $snapshot.width -eq $width -and $snapshot.height -eq $height -and $snapshot.camera.nearPlane -eq 0.01 -and $snapshot.camera.farPlane -eq 20 -and (Test-VectorEqual $snapshot.camera.position $camera.position) -and (Test-VectorEqual $snapshot.camera.target $camera.target))
            Confirm-Check "$($camera.name) reports visible geometry and omitted open portals" ($snapshot.nonBackgroundPixels -gt 0 -and $snapshot.counts.omittedPortalSurfaces -gt 0 -and $snapshot.inputSha256 -eq $finished.hash -and -not $snapshot.guiLaunched)
            $receipt.outputs.Add(@{ path = $png; sha256 = $pngHash; kind = 'diagnostic geometry PNG'; camera = $snapshot.camera; visuallyInspected = $false })
            if ($null -eq $firstImage) { $firstImage = $png; $firstImageHash = $pngHash; $firstRequest = $requestText }
        }
        $overwrite = Invoke-Agent 'snapshot-no-overwrite' @('snapshot', $finished.path, '-', $firstImage) $firstRequest 4
        Confirm-Check 'existing PNG is refused and unchanged' ($overwrite.error -eq 'conflict' -and (Get-Hash $firstImage) -eq $firstImageHash)
        $staleRequest = ConvertFrom-Json -InputObject $firstRequest -AsHashtable
        $staleRequest.inputSha256 = $originalHash
        $staleOutput = Join-Path $scratch 'stale-snapshot-refused.png'
        $stale = Invoke-Agent 'snapshot-stale-source' @('snapshot', $finished.path, '-', $staleOutput) (Convert-Request $staleRequest) 4
        Confirm-Check 'stale snapshot request is refused without output' ($stale.error -eq 'conflict' -and -not (Test-Path -LiteralPath $staleOutput))
        $receipt.snapshot = 'passed; image content still needs visual inspection'
    }
    Confirm-Check 'original MotS input and final design remain unchanged by checks' ((Get-Hash $original) -eq $originalHash -and (Get-Hash $finished.path) -eq $finished.hash)
    $receipt.design = @{ mots = $finished.path; jk = $jkDesign.path; firstExtrusionSector = $firstSector; secondExtrusionSector = $secondSector; materialName = $materialName; colormapName = $colormapName; resourcePayloadsIncluded = $false }
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
    Write-Host "Agent design $($receipt.status): $(Join-Path $scratch 'receipt.json')"
}
