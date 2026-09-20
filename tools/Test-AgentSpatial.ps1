#requires -Version 7.2
<#
.SYNOPSIS
Exercises the original eight-sector Relay Outpost through revision-bound spatial selection.
.DESCRIPTION
Creates an original MotS outpost in a new directory and records exact requests, replies
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
        'No player, game scripts or gameplay resources are added or verified.',
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
    if (-not $Condition) { throw "Spatial assertion failed: $Name" }
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
        if ($offset -gt 20000) { throw 'Synthetic spatial query exceeded the harness paging bound.' }
        $request = @{ schemaVersion = 1; collection = $Collection; offset = $offset; limit = 200 }
        if ($Sector -ge 0) { $request.sector = $Sector }
        if ($Search.Length) { $request.search = $Search }
        $page = Invoke-Agent "$Name-page-$offset" @('query', $LevelPath, '-') (Convert-Request $request)
        if ($null -eq $revision) { $revision = $page.sourceRevision; $inputHash = $page.inputSha256 }
        elseif ($revision -ne $page.sourceRevision -or $inputHash -ne $page.inputSha256) { throw 'Source changed between spatial query pages.' }
        foreach ($item in $page.items) { $items.Add($item) }
        if ($null -ne $page.nextOffset -and $page.nextOffset -le $offset) { throw 'Query continuation did not advance.' }
        $offset = $page.nextOffset
    } while ($null -ne $offset)
    return @{ items = $items.ToArray(); inputSha256 = $inputHash; sourceRevision = $revision }
}

function Test-VectorEqual([object[]]$Actual, [object[]]$Expected) {
    if ($Actual.Count -ne $Expected.Count) { return $false }
    for ($coordinate = 0; $coordinate -lt $Actual.Count; $coordinate++) {
        if (-not [double]::IsFinite([double]$Actual[$coordinate]) -or
            [Math]::Abs([double]$Actual[$coordinate] - [double]$Expected[$coordinate]) -gt 0.000000001) { return $false }
    }
    return $true
}
function Select-Spatial {
    param([string]$Name, $Source, [string]$Kind, [double[]]$Point,
        [double[]]$Outward = @(), [string]$Status = 'unique')
    $request = @{ schemaVersion = 1; kind = $Kind; sectorPoint = $Point }
    if ($Kind -eq 'surface') { $request.outward = $Outward }
    $reply = Invoke-Agent $Name @('select', $Source.path, '-') (Convert-Request $request)
    Confirm-Check "$Name returns the expected cardinality status" ($reply.status -ceq $Status)
    Confirm-Check "$Name is bound to the actual input bytes" ($reply.inputSha256 -ceq $Source.hash -and $reply.sourceRevision -ceq $Source.hash)
    if ($Status -eq 'unique') {
        Confirm-Check "$Name returns exactly one revision-bound geometry token" ($reply.total -eq 1 -and $null -ne $reply.selection -and
            $reply.selection.kind -ceq $Kind -and $reply.selection.sourceRevision -ceq $Source.hash -and
            $reply.selection.geometrySha256 -cmatch '^[0-9a-f]{64}$')
    }
    else {
        Confirm-Check "$Name does not issue a usable token" ($null -eq $reply.selection -and
            (($Status -eq 'none' -and $reply.total -eq 0) -or ($Status -eq 'ambiguous' -and $reply.total -gt 1)))
    }
    return $reply
}
function New-SpatialPatch($Source, [object[]]$Operations) {
    # These are generated standalone JKLs without native companion dependencies.
    return Convert-Request @{ schemaVersion = 1; inputSha256 = $Source.hash; sourceRevision = $Source.hash; operations = $Operations }
}
function Commit-Spatial {
    param([string]$Name, $Source, [object[]]$Operations)
    $patch = New-SpatialPatch $Source $Operations
    $plan = Invoke-Agent "$Name-plan" @('plan', $Source.path, '-') $patch
    Confirm-Check "$Name planning preserves the source" ((Get-Hash $Source.path) -ceq $Source.hash)
    $output = Join-Path $scratch "$Name.jkl"
    $applied = Invoke-Agent "$Name-apply" @('apply', $Source.path, '-', $output, $plan.outputSha256) $patch
    $hash = Get-Hash $output
    Confirm-Check "$Name output matches the independently recomputed reviewed plan" ($hash -ceq $plan.outputSha256 -and $hash -ceq $applied.plan.outputSha256)
    Confirm-Check "$Name applying preserves the source" ((Get-Hash $Source.path) -ceq $Source.hash)
    $receipt.outputs.Add(@{ path = $output; sha256 = $hash; kind = 'original synthetic JKL' })
    return @{ path = $output; hash = $hash; plan = $plan }
}
function Expect-PatchRefusal {
    param([string]$Name, $Source, [object[]]$Operations, [int]$ExitCode,
        [string]$ErrorType, [int]$OperationIndex, [string]$MessagePattern)
    $output = Join-Path $scratch "$Name-refused.jkl"
    $reply = Invoke-Agent $Name @('apply', $Source.path, '-', $output) (New-SpatialPatch $Source $Operations) $ExitCode
    Confirm-Check "$Name identifies the intended refusal" ($reply.error -ceq $ErrorType -and
        $reply.details.operationIndex -eq $OperationIndex -and $reply.message -match $MessagePattern)
    Confirm-Check "$Name leaves input and destination unchanged" ((Get-Hash $Source.path) -ceq $Source.hash -and -not (Test-Path -LiteralPath $output))
    return $reply
}

# Independent output witnesses read serialized tables directly. Their row
# numbers are used only for verification, never to construct an edit request.
function Read-Section([string[]]$Lines, [string]$Name) {
    $body = [Collections.Generic.List[string]]::new()
    $inside = $false
    foreach ($raw in $Lines) {
        $line = $raw.Split('#')[0].Trim()
        if ($line -match '^SECTION:') {
            if ($inside) { break }
            $inside = $line -ieq "SECTION: $Name"
            continue
        }
        if ($inside) {
            if ($line -ieq 'END') { break }
            if ($line.Length) { $body.Add($line) }
        }
    }
    if (-not $inside) { throw "Missing output section $Name." }
    return ,$body.ToArray()
}
function Read-IndexedTable([string[]]$Lines, [string]$Name) {
    $pattern = '^' + (($Name.Split(' ') | ForEach-Object { [Regex]::Escape($_) }) -join '\s+') + '\s+(\d+)$'
    $start = -1
    $count = 0
    for ($lineIndex = 0; $lineIndex -lt $Lines.Count; $lineIndex++) {
        $match = [Regex]::Match($Lines[$lineIndex], $pattern, [Text.RegularExpressions.RegexOptions]::IgnoreCase)
        if ($match.Success) { $start = $lineIndex + 1; $count = [int]$match.Groups[1].Value; break }
    }
    if ($start -lt 0 -or $start + $count -gt $Lines.Count) { throw "Missing or truncated output table $Name." }
    $rows = [Collections.Generic.List[object]]::new()
    for ($row = 0; $row -lt $count; $row++) {
        $fields = $Lines[$start + $row] -split '\s+'
        if ($fields[0] -cne "${row}:") { throw "Unexpected serialized $Name row identity at $row." }
        $rows.Add($fields)
    }
    return @{ rows = $rows.ToArray(); next = $start + $count }
}
function Read-Number([string]$Text) {
    $value = [double]::Parse($Text, [Globalization.CultureInfo]::InvariantCulture)
    if (-not [double]::IsFinite($value)) { throw 'Output contains a non-finite geometry component.' }
    return $value
}
function Read-Outpost([string]$Path) {
    $lines = [IO.File]::ReadAllLines($Path)
    $geo = Read-Section $lines 'GEORESOURCE'
    $vertices = (Read-IndexedTable $geo 'World vertices').rows
    $uv = (Read-IndexedTable $geo 'World texture vertices').rows
    $adjoins = (Read-IndexedTable $geo 'World adjoins').rows
    $surfaceTable = Read-IndexedTable $geo 'World surfaces'
    $normals = [Collections.Generic.List[object]]::new()
    for ($index = 0; $index -lt $surfaceTable.rows.Count; $index++) {
        if ($surfaceTable.next + $index -ge $geo.Count) { throw 'Missing serialized surface normal.' }
        $fields = $geo[$surfaceTable.next + $index] -split '\s+'
        if ($fields.Count -ne 4 -or $fields[0] -cne "${index}:") { throw 'Invalid serialized surface normal row.' }
        $normals.Add(@((Read-Number $fields[1]), (Read-Number $fields[2]), (Read-Number $fields[3])))
    }
    $sectorLines = Read-Section $lines 'SECTORS'
    $header = [Regex]::Match($sectorLines[0], '^World\s+sectors\s+(\d+)$', [Text.RegularExpressions.RegexOptions]::IgnoreCase)
    if (-not $header.Success) { throw 'Missing output sector count.' }
    $sectors = [Collections.Generic.List[object]]::new()
    $sectorMatches = [Regex]::Matches(($sectorLines -join "`n"), '(?im)^SECTOR\s+(\d+)\s*$')
    foreach ($sectorMatch in $sectorMatches) {
        $sectorIndex = [int]$sectorMatch.Groups[1].Value
        $begin = 0
        while ($begin -lt $sectorLines.Count -and $sectorLines[$begin] -cnotmatch "^SECTOR\s+$sectorIndex$") { $begin++ }
        $end = $begin + 1
        while ($end -lt $sectorLines.Count -and $sectorLines[$end] -notmatch '^SECTOR\s+\d+$') { $end++ }
        $block = @($sectorLines[($begin + 1)..($end - 1)])
        $refs = (Read-IndexedTable $block 'VERTICES').rows
        $positions = @($refs | ForEach-Object {
            $row = $vertices[[int]$_[1]]
            ,@((Read-Number $row[1]), (Read-Number $row[2]), (Read-Number $row[3]))
        })
        $range = @($block | Where-Object { $_ -match '^SURFACES\s+' })
        if ($range.Count -ne 1) { throw 'Missing or repeated sector surface range.' }
        $rangeFields = $range[0] -split '\s+'
        $ambient = @($block | Where-Object { $_ -match '^AMBIENT\s+LIGHT\s+' })
        $colormap = @($block | Where-Object { $_ -match '^COLORMAP\s+' })
        if ($ambient.Count -ne 1 -or $colormap.Count -ne 1) { throw 'Missing sector authoring metadata.' }
        $sectors.Add(@{ index = $sectorIndex; positions = $positions; firstSurface = [int]$rangeFields[1];
            surfaceCount = [int]$rangeFields[2]; ambient = Read-Number (($ambient[0] -split '\s+')[-1]); colormap = [int](($colormap[0] -split '\s+')[-1]) })
    }
    if ($sectors.Count -ne [int]$header.Groups[1].Value) { throw 'Sector count does not match actual serialized blocks.' }
    return @{ sectors = $sectors.ToArray(); vertices = $vertices; uv = $uv; surfaces = $surfaceTable.rows; normals = $normals.ToArray(); adjoins = $adjoins }
}
function Point-Key([object[]]$Point) {
    return ($Point | ForEach-Object { ([double]$_).ToString('R', [Globalization.CultureInfo]::InvariantCulture) }) -join ','
}
function Confirm-Outpost($Model, [object[]]$Rooms, [int]$MaterialIndex, [int]$ColormapIndex, [object[]]$FloorUv) {
    Confirm-Check 'serialized outpost has exactly eight sectors and 48 surfaces' ($Model.sectors.Count -eq 8 -and $Model.surfaces.Count -eq 48)
    $owners = @{}
    $byName = @{}
    foreach ($room in $Rooms) {
        $expected = @(
            foreach ($x in @(($room.center[0] - 1), ($room.center[0] + 1))) {
                foreach ($y in @(($room.center[1] - 1), ($room.center[1] + 1))) {
                    foreach ($z in @(($room.center[2] - 1), ($room.center[2] + 1))) { Point-Key @($x, $y, $z) }
                }
            }
        ) | Sort-Object
        $matching = @($Model.sectors | Where-Object {
            $actual = @($_.positions | ForEach-Object { Point-Key $_ }) | Sort-Object
            $actual.Count -eq 8 -and ($actual -join '|') -ceq ($expected -join '|')
        })
        Confirm-Check "$($room.name) has its exact independently specified eight vertices" ($matching.Count -eq 1)
        $sector = $matching[0]
        Confirm-Check "$($room.name) has exactly six surfaces" ($sector.surfaceCount -eq 6)
        $byName[$room.name] = $sector
        $planes = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        for ($local = 0; $local -lt $sector.surfaceCount; $local++) {
            $global = $sector.firstSurface + $local
            if ($owners.ContainsKey($global)) { throw 'A serialized surface belongs to multiple rooms.' }
            $owners[$global] = $room.name
            $row = $Model.surfaces[$global]
            Confirm-Check "$($room.name) face $local is an inward quad" ([int]$row[9] -eq 4)
            $points = @(
                foreach ($corner in 0..3) {
                    $pair = $row[10 + $corner].Split(',')
                    $vertex = $Model.vertices[[int]$pair[0]]
                    ,@((Read-Number $vertex[1]), (Read-Number $vertex[2]), (Read-Number $vertex[3]))
                }
            )
            $planeAxes = @(foreach ($axis in 0..2) {
                $values = @($points | ForEach-Object { [double]$_[$axis] } | Sort-Object -Unique)
                if ($values.Count -eq 1) { $axis }
            })
            Confirm-Check "$($room.name) face $local lies on exactly one room boundary plane" ($planeAxes.Count -eq 1)
            $axis = $planeAxes[0]
            $expectedNormal = @(0.0, 0.0, 0.0)
            $expectedNormal[$axis] = [double]$room.center[$axis] - [double]$points[0][$axis]
            Confirm-Check "$($room.name) face $local serialized normal points inward" ((Test-VectorEqual $Model.normals[$global] $expectedNormal) -and [Math]::Abs($expectedNormal[$axis]) -eq 1)
            $null = $planes.Add("${axis}:$($points[0][$axis])")
            $edgeA = @(foreach ($component in 0..2) { [double]$points[1][$component] - [double]$points[0][$component] })
            $edgeB = @(foreach ($component in 0..2) { [double]$points[2][$component] - [double]$points[1][$component] })
            $cross = @(
                ($edgeA[1] * $edgeB[2] - $edgeA[2] * $edgeB[1]),
                ($edgeA[2] * $edgeB[0] - $edgeA[0] * $edgeB[2]),
                ($edgeA[0] * $edgeB[1] - $edgeA[1] * $edgeB[0])
            )
            $dot = 0.0
            foreach ($component in 0..2) { $dot += $cross[$component] * $expectedNormal[$component] }
            Confirm-Check "$($room.name) face $local actual vertex winding points inward" ($dot -gt 0)
            $faceKeys = @($points | ForEach-Object { Point-Key $_ } | Sort-Object -Unique)
            Confirm-Check "$($room.name) face $local uses four distinct vertices from its own room" ($faceKeys.Count -eq 4 -and @($faceKeys | Where-Object { $_ -cnotin $expected }).Count -eq 0)
        }
        Confirm-Check "$($room.name) has six distinct boundary planes" ($planes.Count -eq 6)
    }
    Confirm-Check 'all serialized surfaces have exactly one geometric room owner' ($owners.Count -eq $Model.surfaces.Count)
    $neighborSets = @{}
    foreach ($room in $Rooms) { $neighborSets[$room.name] = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal) }
    $adjoinOwners = @{}
    for ($global = 0; $global -lt $Model.surfaces.Count; $global++) {
        $row = $Model.surfaces[$global]
        $adjoin = [int]$row[7]
        if ($adjoin -lt 0) { continue }
        if ($adjoinOwners.ContainsKey($adjoin)) { throw 'An adjoin slot has more than one surface owner.' }
        $adjoinOwners[$adjoin] = $global
        Confirm-Check "portal surface $global remains open" ([int]$row[4] -eq 0)
    }
    Confirm-Check 'nine portal pairs are represented by exactly 18 owned adjoin slots' ($Model.adjoins.Count -eq 18 -and $adjoinOwners.Count -eq 18)
    foreach ($adjoin in $adjoinOwners.Keys) {
        $mirror = [int]$Model.adjoins[$adjoin][2]
        Confirm-Check "adjoin $adjoin has a reciprocal owned mirror" ($adjoinOwners.ContainsKey($mirror) -and [int]$Model.adjoins[$mirror][2] -eq $adjoin -and $mirror -ne $adjoin)
        $owner = $owners[$adjoinOwners[$adjoin]]
        $partner = $owners[$adjoinOwners[$mirror]]
        $null = $neighborSets[$owner].Add($partner)
        $a = $Model.surfaces[$adjoinOwners[$adjoin]]
        $b = $Model.surfaces[$adjoinOwners[$mirror]]
        $aKeys = @(foreach ($corner in 0..3) {
            $pair = $a[10 + $corner].Split(',')
            $vertex = $Model.vertices[[int]$pair[0]]
            Point-Key @($vertex[1..3])
        }) | Sort-Object
        $bKeys = @(foreach ($corner in 0..3) {
            $pair = $b[10 + $corner].Split(',')
            $vertex = $Model.vertices[[int]$pair[0]]
            Point-Key @($vertex[1..3])
        }) | Sort-Object
        Confirm-Check "adjoin $adjoin joins the same complete geometric boundary" (($aKeys -join '|') -ceq ($bKeys -join '|'))
    }
    foreach ($room in $Rooms) {
        $actual = @($neighborSets[$room.name] | Sort-Object)
        $expected = @($room.neighbors | Sort-Object)
        Confirm-Check "$($room.name) has exactly its intended neighbors" (($actual -join '|') -ceq ($expected -join '|'))
    }
    Confirm-Check 'revisited checkpoint retains selected ambient light and colormap' ($byName.Checkpoint.ambient -eq 0.25 -and $byName.Checkpoint.colormap -eq $ColormapIndex)
    $entry = $byName.Arrival
    $floors = @(for ($i = 0; $i -lt $entry.surfaceCount; $i++) {
        $global = $entry.firstSurface + $i
        if (Test-VectorEqual $Model.normals[$global] @(0, 0, 1)) { $global }
    })
    Confirm-Check 'revisited arrival has one geometric floor' ($floors.Count -eq 1)
    $floor = $Model.surfaces[$floors[0]]
    Confirm-Check 'arrival floor retains the selected material slot' ([int]$floor[1] -eq $MaterialIndex)
    for ($corner = 0; $corner -lt 4; $corner++) {
        $pair = $floor[10 + $corner].Split(',')
        $texture = $Model.uv[[int]$pair[1]]
        Confirm-Check "arrival floor corner $corner retains explicit UVs" (Test-VectorEqual @((Read-Number $texture[1]), (Read-Number $texture[2])) $FloorUv[$corner])
    }
    return $byName
}

try {
    $version = Invoke-Agent 'version' @('--version')
    Confirm-Check 'the selected executable identifies itself as sed-agent' ($version.name -ceq 'sed-agent')
    $receipt.agentVersion = $version.version
    $null = Invoke-Agent 'spatial-selection-schema' @('schema', 'select')

    # Semantic names are acceptance labels, not invented persistent sector IDs.
    # Every edit discovers its target by an interior point and outward direction.
    $rooms = @(
        @{ name = 'Arrival'; center = @(0, 0, 0); neighbors = @('Checkpoint', 'Hangar') },
        @{ name = 'Checkpoint'; center = @(2, 0, 0); neighbors = @('Arrival', 'Comms', 'NorthHub', 'Security') },
        @{ name = 'Comms'; center = @(4, 0, 0); neighbors = @('Checkpoint', 'Generator') },
        @{ name = 'Generator'; center = @(4, 2, 0); neighbors = @('Comms', 'NorthHub', 'Observation') },
        @{ name = 'NorthHub'; center = @(2, 2, 0); neighbors = @('Generator', 'Hangar', 'Checkpoint') },
        @{ name = 'Hangar'; center = @(0, 2, 0); neighbors = @('NorthHub', 'Arrival') },
        @{ name = 'Security'; center = @(2, -2, 0); neighbors = @('Checkpoint') },
        @{ name = 'Observation'; center = @(4, 2, 2); neighbors = @('Generator') }
    )
    $receipt.task = @{ name = 'Relay Outpost'; originalSyntheticFixture = $true; rooms = $rooms;
        authoringTargets = 'Only select-issued sourceRevision and geometrySha256 tokens; no authored geometry indices.' }

    $original = Join-Path $scratch 'relay-original.jkl'
    $created = Invoke-Agent 'new-relay-mots' @('new', $original, 'mots')
    $source = @{ path = $original; hash = Get-Hash $original }
    Confirm-Check 'original scaffold matches its reported hash' ($source.hash -ceq $created.inputSha256)
    $receipt.outputs.Add(@{ path = $source.path; sha256 = $source.hash; kind = 'original synthetic JKL' })
    $materialName = 'relay_floor.mat'
    $colormapName = 'relay_palette.cmp'
    $source = Commit-Spatial 'relay-authoring-metadata' $source @(
        @{ op = 'registerMaterial'; name = $materialName },
        @{ op = 'registerColormap'; name = $colormapName },
        @{ op = 'addLight'; position = @(0, 0, 0); rgb = @(1, 0.5, 0.25); intensity = 0.375; range = 4 },
        @{ op = 'addLight'; position = @(2, 0, 0); rgb = @(0.25, 1, 0.5); intensity = 0.625; rgbIntensity = 0; range = 5; legacyRgbRange = 99 },
        @{ op = 'addLight'; position = @(4, 2, 0); rgb = @(0.5, 0.25, 1); intensity = 0.875; rgbIntensity = 0.875; range = 6; legacyRgbRange = 0 }
    )
    $materials = Read-Collection 'registered-materials' $source.path 'materials'
    $colormaps = Read-Collection 'registered-colormaps' $source.path 'colormaps'
    $material = @($materials.items | Where-Object name -CEQ $materialName)
    $colormap = @($colormaps.items | Where-Object name -CEQ $colormapName)
    Confirm-Check 'the requested resource metadata is registered exactly once' ($material.Count -eq 1 -and $colormap.Count -eq 1)
    $materialIndex = [int]$material[0].index
    $colormapIndex = [int]$colormap[0].index
    $lights = Read-Collection 'original-source-lights' $source.path 'lights'
    $originalLights = Convert-Request $lights.items
    Confirm-Check 'light metadata distinguishes inherited zero and explicit-equal RGB' ($lights.items.Count -eq 3 -and
        $null -eq $lights.items[0].rgbIntensity -and $lights.items[0].rgbIntensityInherited -and
        $lights.items[1].rgbIntensity -eq 0 -and -not $lights.items[1].rgbIntensityInherited -and $lights.items[1].legacyRgbRange -eq 99 -and
        $lights.items[2].rgbIntensity -eq $lights.items[2].intensity -and -not $lights.items[2].rgbIntensityInherited -and $lights.items[2].legacyRgbRange -eq 0)
    $arrivalLight = @($lights.items | Where-Object { Test-VectorEqual $_.position @(0, 0, 0) })
    Confirm-Check 'negative-control light was discovered by its returned position' ($arrivalLight.Count -eq 1)

    $extensions = @(
        @{ name = 'checkpoint'; from = @(0, 0, 0); outward = @(1, 0, 0) },
        @{ name = 'comms'; from = @(2, 0, 0); outward = @(1, 0, 0) },
        @{ name = 'generator'; from = @(4, 0, 0); outward = @(0, 1, 0) },
        @{ name = 'north-hub'; from = @(4, 2, 0); outward = @(-1, 0, 0) },
        @{ name = 'hangar'; from = @(2, 2, 0); outward = @(-1, 0, 0) },
        @{ name = 'security'; from = @(2, 0, 0); outward = @(0, -1, 0) },
        @{ name = 'observation'; from = @(4, 2, 0); outward = @(0, 0, 1) }
    )
    foreach ($extension in $extensions) {
        $selected = Select-Spatial "select-$($extension.name)-boundary" $source 'surface' $extension.from $extension.outward
        $source = Commit-Spatial "relay-$($extension.name)" $source @(@{ op = 'extrudeSurface'; target = $selected.selection; distance = 2 })
    }

    $hangar = Select-Spatial 'select-hangar-south' $source 'surface' @(0, 2, 0) @(0, -1, 0)
    $arrival = Select-Spatial 'select-arrival-north' $source 'surface' @(0, 0, 0) @(0, 1, 0)
    $source = Commit-Spatial 'relay-close-loop' $source @(@{ op = 'adjoinSurfaces'; target = $hangar.selection; otherTarget = $arrival.selection })

    $null = Select-Spatial 'no-match-outside-outpost' $source 'sector' @(99, 99, 99) -Status 'none'
    $boundary = Select-Spatial 'ambiguous-shared-boundary' $source 'sector' @(1, 0, 0) -Status 'ambiguous'
    Confirm-Check 'the boundary negative is sector ambiguity with exactly two candidates' ($boundary.stage -ceq 'sector' -and $boundary.total -eq 2)

    # Revisit two earlier rooms. The tempting extrusion overlaps the existing
    # north room, so it must refuse before the exact-boundary adjoin succeeds.
    $checkpoint = Select-Spatial 'select-checkpoint-north' $source 'surface' @(2, 0, 0) @(0, 1, 0)
    $northHub = Select-Spatial 'select-north-hub-south' $source 'surface' @(2, 2, 0) @(0, -1, 0)
    $null = Expect-PatchRefusal 'occupied-room-extrusion' $source @(@{ op = 'extrudeSurface'; target = $checkpoint.selection; distance = 2 }) 2 'invalid_request' 0 'overlap'
    $source = Commit-Spatial 'relay-checkpoint-shortcut' $source @(@{ op = 'adjoinSurfaces'; target = $checkpoint.selection; otherTarget = $northHub.selection })

    $floor = Select-Spatial 'select-original-arrival-floor' $source 'surface' @(0, 0, 0) @(0, 0, -1)
    $checkpointSector = Select-Spatial 'select-earlier-checkpoint' $source 'sector' @(2, 0, 0)
    $beforeMetadata = $source
    $floorUv = @(@(0, 0), @(128, 0), @(128, 64), @(0, 64))
    $source = Commit-Spatial 'relay-final' $source @(
        @{ op = 'setSurfaceMaterial'; target = $floor.selection; materialIndex = $materialIndex },
        @{ op = 'setSurfaceUv'; target = $floor.selection; uv = $floorUv },
        @{ op = 'setSectorAmbient'; target = $checkpointSector.selection; value = 0.25 },
        @{ op = 'setSectorColormap'; target = $checkpointSector.selection; colormapIndex = $colormapIndex }
    )
    Confirm-Check 'multiple metadata operations can reuse unchanged geometry tokens within a batch' ($source.hash -cne $beforeMetadata.hash)
    $freshFloor = Select-Spatial 'select-revised-arrival-floor' $source 'surface' @(0, 0, 0) @(0, 0, -1)
    Confirm-Check 'metadata-only revision changes source identity while preserving target geometry identity' ($freshFloor.selection.geometrySha256 -ceq $floor.selection.geometrySha256 -and
        $freshFloor.selection.sourceRevision -cne $floor.selection.sourceRevision)
    $null = Expect-PatchRefusal 'stale-selection-revision' $source @(@{ op = 'setSurfaceMaterial'; target = $floor.selection; materialIndex = $materialIndex }) 4 'conflict' 0 'revision'

    $west = Select-Spatial 'select-arrival-west-for-rollback-control' $source 'surface' @(0, 0, 0) @(-1, 0, 0)
    $null = Expect-PatchRefusal 'within-batch-stale-geometry' $source @(
        @{ op = 'setLight'; light = [int]$arrivalLight[0].index; intensity = 9 },
        @{ op = 'extrudeSurface'; target = $west.selection; distance = 2 },
        @{ op = 'extrudeSurface'; target = $west.selection; distance = 2 }
    ) 4 'conflict' 2 'geometry'

    $finalLights = Read-Collection 'final-source-lights' $source.path 'lights'
    Confirm-Check 'all seeded source-light fields survive authoring and the failed batch' ((Convert-Request $finalLights.items) -ceq $originalLights)
    Confirm-Check 'final output retains validated independent source-light metadata' ($source.plan.outputSourceLightMetadata.state -ceq 'validated')
    $validation = Invoke-Agent 'validate-final-outpost' @('validate', $source.path)
    Confirm-Check 'final outpost passes structural validation for the exact bytes' ([bool]$validation.valid -and $validation.inputSha256 -ceq $source.hash)
    $model = Read-Outpost $source.path
    $null = Confirm-Outpost $model $rooms $materialIndex $colormapIndex $floorUv
    Confirm-Check 'fictional resource registration never created asset payloads' (-not (Test-Path -LiteralPath (Join-Path $scratch $materialName)) -and -not (Test-Path -LiteralPath (Join-Path $scratch $colormapName)))

    if (-not $SkipSnapshot) {
        # The second view is tilted slightly from vertical because the existing
        # snapshot camera uses Z-up and correctly refuses a parallel view axis.
        foreach ($camera in @(
            @{ name = 'relay-ground-loop'; position = @(-0.5, 0, 0); target = @(4, 0, 0) },
            @{ name = 'relay-observation-downward'; position = @(4, 2, 2); target = @(4.5, 2, 0) }
        )) {
            $request = @{ schemaVersion = 1; inputSha256 = $source.hash; sourceRevision = $source.hash;
                position = $camera.position; target = $camera.target; fieldOfViewDegrees = 90;
                width = 640; height = 480; nearPlane = 0.01; farPlane = 20 }
            $png = Join-Path $scratch "$($camera.name).png"
            $snapshot = Invoke-Agent $camera.name @('snapshot', $source.path, '-', $png) (Convert-Request $request)
            $bytes = [IO.File]::ReadAllBytes($png)
            $hash = Get-Hash $png
            Confirm-Check "$($camera.name) produces a hashed PNG" ($bytes.Length -ge 24 -and [BitConverter]::ToString($bytes, 0, 8) -ceq '89-50-4E-47-0D-0A-1A-0A' -and $hash -ceq $snapshot.outputSha256)
            $width = [Net.IPAddress]::NetworkToHostOrder([BitConverter]::ToInt32($bytes, 16))
            $height = [Net.IPAddress]::NetworkToHostOrder([BitConverter]::ToInt32($bytes, 20))
            Confirm-Check "$($camera.name) honors the requested dimensions and pose" ($width -eq 640 -and $height -eq 480 -and
                $snapshot.width -eq $width -and $snapshot.height -eq $height -and
                (Test-VectorEqual $snapshot.camera.position $camera.position) -and (Test-VectorEqual $snapshot.camera.target $camera.target))
            Confirm-Check "$($camera.name) reports visible geometry without launching a GUI" ($snapshot.nonBackgroundPixels -gt 0 -and
                $snapshot.counts.omittedPortalSurfaces -gt 0 -and $snapshot.inputSha256 -ceq $source.hash -and -not $snapshot.guiLaunched)
            $receipt.outputs.Add(@{ path = $png; sha256 = $hash; kind = 'diagnostic geometry PNG'; camera = $snapshot.camera; visuallyInspected = $false })
        }
        $receipt.snapshot = 'passed; image content still needs visual inspection'
    }
    foreach ($output in $receipt.outputs) {
        Confirm-Check "retained artifact is unchanged: $([IO.Path]::GetFileName($output.path))" ((Get-Hash $output.path) -ceq $output.sha256)
    }
    $receipt.design = @{ path = $source.path; sha256 = $source.hash; sectors = 8; portalPairs = 9;
        materialName = $materialName; colormapName = $colormapName; resourcePayloadsIncluded = $false;
        sourceLightStatePreserved = $true; noGeometryIndicesAuthored = $true }
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
    Write-Host "Agent spatial $($receipt.status): $(Join-Path $scratch 'receipt.json')"
}
