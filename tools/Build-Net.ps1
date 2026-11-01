#requires -Version 7.4
[CmdletBinding()]
param(
    [ValidateSet('Release', 'Debug')][string]$Configuration = 'Release',
    [string]$DotNet = 'dotnet',
    [string]$Python = 'python',
    [string]$OutputDirectory,
    [string]$ArtifactsPath
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
function Resolve-Executable([string]$Value) {
    if (Test-Path -LiteralPath $Value -PathType Leaf) { return (Resolve-Path -LiteralPath $Value).Path }
    $command = Get-Command $Value -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if (!$command) { throw "Executable not found: $Value. Supply its absolute path." }
    return $command.Source
}
function Assert-ExternalOutput([string]$Path) {
    $absolute = [IO.Path]::GetFullPath($Path)
    $ancestor = $absolute
    while ($ancestor) {
        if (Test-Path -LiteralPath (Join-Path $ancestor '.git')) { throw "Generated build data must be outside Git worktrees: $absolute" }
        if ((Test-Path -LiteralPath $ancestor) -and ((Get-Item -LiteralPath $ancestor).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw "Generated build paths cannot pass through symbolic links or junctions: $absolute"
        }
        $ancestor = Split-Path -Parent $ancestor
    }
    return $absolute
}
$dotnetExe = Resolve-Executable $DotNet
$pythonExe = Resolve-Executable $Python
if (!$OutputDirectory) { $OutputDirectory = Join-Path ([IO.Path]::GetTempPath()) ('SED-MotS-build-' + [Guid]::NewGuid().ToString('N')) }
$attemptRoot = Assert-ExternalOutput $OutputDirectory
if (Test-Path -LiteralPath $attemptRoot) { throw 'Choose a new build evidence directory; previous attempts are preserved.' }
if (!$ArtifactsPath) { $ArtifactsPath = Join-Path $attemptRoot 'artifacts' }
$artifacts = Assert-ExternalOutput $ArtifactsPath
$null = New-Item -ItemType Directory -Path $attemptRoot

function Invoke-Logged([string]$Executable, [string[]]$Arguments, [string]$LogName, [int]$TimeoutSeconds = 1800, [hashtable]$Environment = @{}) {
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = $Executable
    $start.WorkingDirectory = $repoRoot
    foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.EnvironmentVariables['DOTNET_ROOT'] = Split-Path -Parent $dotnetExe
    $start.EnvironmentVariables['DOTNET_CLI_TELEMETRY_OPTOUT'] = '1'
    $start.EnvironmentVariables['DOTNET_SKIP_FIRST_TIME_EXPERIENCE'] = '1'
    $start.EnvironmentVariables['DOTNET_GENERATE_ASPNET_CERTIFICATE'] = 'false'
    $start.EnvironmentVariables['MSBUILDDISABLENODEREUSE'] = '1'
    $start.Environment['PYTHONDONTWRITEBYTECODE'] = '1'
    $start.Environment['TMP'] = $attemptRoot
    $start.Environment['TEMP'] = $attemptRoot
    foreach ($entry in $Environment.GetEnumerator()) { $start.Environment[$entry.Key] = $entry.Value }
    $process = New-Object Diagnostics.Process
    $process.StartInfo = $start
    try {
        $null = $process.Start()
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        $timedOut = !$process.WaitForExit($TimeoutSeconds * 1000)
        if ($timedOut) { $process.Kill($true); $process.WaitForExit() }
        $outputText = $stdout.GetAwaiter().GetResult() + $stderr.GetAwaiter().GetResult()
        $logPath = Join-Path $attemptRoot $LogName
        $outputText | Set-Content -LiteralPath $logPath -Encoding UTF8
        Write-Host $outputText
        if ($timedOut) { throw "Owned build/test process exceeded $TimeoutSeconds seconds. See $logPath" }
        if ($process.ExitCode -ne 0) { throw "Command returned $($process.ExitCode). See $logPath" }
    }
    finally { $process.Dispose() }
}

$properties = @("-p:ArtifactsPath=$artifacts", '-m:1', '-nodeReuse:false', '-p:BuildInParallel=false', '-p:UseSharedCompilation=false')
$receipt = [ordered]@{
    startedUtc = [DateTime]::UtcNow.ToString('o')
    configuration = $Configuration
    dotnetPath = $dotnetExe
    dotnetSha256 = (Get-FileHash -LiteralPath $dotnetExe -Algorithm SHA256).Hash.ToLowerInvariant()
    artifactsPath = $artifacts
    status = 'running'
    guiLaunched = $false
    gameLaunched = $false
    testProjects = @('Sed.Core.Tests', 'Sed.Mcp.Tests')
    pythonToolSuites = @('tools/tests', 'tools/blender/tests')
    pythonToolSuitesNotRun = @(@{ path='tools/retail-capture/tests'; reason='Linux Wine process-ownership and /proc fixtures; run with tools/linux.py on Linux.' },
        @{ path='tools/test_linux.py'; reason='Linux launchers, symlinks and executable-mode fixtures; run on Linux.' })
    headlessUiProbe = 'pending'
}
try {
    Invoke-Logged $dotnetExe @('--info') 'sdk-info.log' 60
    Invoke-Logged $dotnetExe (@('build', 'dotnet/Sed.slnx', '--configuration', $Configuration, '--nologo', '--disable-build-servers') + $properties) 'build.log'
    foreach ($project in $receipt.testProjects) {
        Invoke-Logged $dotnetExe (@('test', "dotnet/tests/$project/$project.csproj", '--configuration', $Configuration, '--no-build', '--no-restore', '--nologo', '--logger', "trx;LogFileName=$project.trx", '--results-directory', $attemptRoot) + $properties) "$project.log"
    }
    foreach ($suite in $receipt.pythonToolSuites) {
        Invoke-Logged $pythonExe @('-B', '-m', 'unittest', 'discover', '-s', $suite, '-v') ('python-' + $suite.Replace('/', '-') + '.log') 600
    }
    Invoke-Logged $dotnetExe @('run', '--project', 'dotnet/tools/Sed.UiProbe/Sed.UiProbe.csproj', '-c', $Configuration, '--no-build', '--no-restore', "-p:ArtifactsPath=$artifacts") 'ui-probe.log' 180 @{
        VK_ICD_FILENAMES=(Join-Path $attemptRoot 'no-driver.json'); VK_DRIVER_FILES=(Join-Path $attemptRoot 'no-driver.json')
    }
    $receipt.headlessUiProbe = 'passed'
    $receipt.status = 'passed'
}
catch {
    $receipt.status = 'failed'
    $receipt.error = $_.Exception.Message
    throw
}
finally {
    $receipt.finishedUtc = [DateTime]::UtcNow.ToString('o')
    $receipt | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $attemptRoot 'build-receipt.json') -Encoding UTF8
    Write-Host "Build evidence: $attemptRoot"
}
