[CmdletBinding()]
param(
    [ValidateSet('Release', 'Debug')][string]$Configuration = 'Release',
    [string]$DotNet = 'dotnet'
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
if (Test-Path -LiteralPath $DotNet -PathType Leaf) {
    $dotnetExe = (Resolve-Path -LiteralPath $DotNet).Path
}
else {
    $command = Get-Command $DotNet -CommandType Application -ErrorAction SilentlyContinue
    if (-not $command) { throw 'Install a .NET 10 SDK or supply -DotNet with the full path to a portable SDK dotnet.exe.' }
    $dotnetExe = $command.Source
}
$attemptRoot = Join-Path $repoRoot ('build\dotnet\' + $Configuration + '-' + [Guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $attemptRoot

function Invoke-DotNet([string[]]$Arguments, [string]$LogName) {
    $start = New-Object Diagnostics.ProcessStartInfo
    $start.FileName = $dotnetExe
    $start.WorkingDirectory = $repoRoot
    # All arguments are passed directly to dotnet, without a command shell.
    # The paths below never end in a backslash; reject embedded quotes/newlines.
    foreach ($argument in $Arguments) {
        if ($argument -match '["\r\n]') { throw 'Build arguments cannot contain quotes or line breaks.' }
    }
    $start.Arguments = ($Arguments | ForEach-Object { '"' + $_ + '"' }) -join ' '
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.EnvironmentVariables['DOTNET_ROOT'] = Split-Path -Parent $dotnetExe
    $start.EnvironmentVariables['DOTNET_CLI_TELEMETRY_OPTOUT'] = '1'
    $start.EnvironmentVariables['DOTNET_SKIP_FIRST_TIME_EXPERIENCE'] = '1'
    $start.EnvironmentVariables['DOTNET_GENERATE_ASPNET_CERTIFICATE'] = 'false'
    $start.EnvironmentVariables['MSBUILDDISABLENODEREUSE'] = '1'
    $process = New-Object Diagnostics.Process
    $process.StartInfo = $start
    try {
        $null = $process.Start()
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        $process.WaitForExit()
        $outputText = $stdout.GetAwaiter().GetResult() + $stderr.GetAwaiter().GetResult()
        $logPath = Join-Path $attemptRoot $LogName
        $outputText | Set-Content -LiteralPath $logPath -Encoding UTF8
        Write-Host $outputText
        if ($process.ExitCode -ne 0) { throw "dotnet returned $($process.ExitCode). See $logPath" }
    }
    finally { $process.Dispose() }
}

$receipt = [ordered]@{
    startedUtc = [DateTime]::UtcNow.ToString('o')
    configuration = $Configuration
    dotnetPath = $dotnetExe
    dotnetSha256 = (Get-FileHash -LiteralPath $dotnetExe -Algorithm SHA256).Hash.ToLowerInvariant()
    status = 'running'
    guiLaunched = $false
}
try {
    Invoke-DotNet @('--info') 'sdk-info.log'
    Invoke-DotNet @('build', 'dotnet/Sed.slnx', '--configuration', $Configuration, '--nologo', '--disable-build-servers') 'build.log'
    Invoke-DotNet @('test', 'dotnet/tests/Sed.Core.Tests/Sed.Core.Tests.csproj', '--configuration', $Configuration, '--no-build', '--no-restore', '--nologo', '--logger', 'trx;LogFileName=tests.trx', '--results-directory', $attemptRoot) 'tests.log'
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
