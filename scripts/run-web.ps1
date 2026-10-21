#Requires -Version 7.0
<#
.SYNOPSIS
Runs the web page directly against the native Windows Cosmos DB emulator.
.DESCRIPTION
Configure AzureAd and Cosmos:ConnectionString with user-secrets or environment
variables first. The shared native emulator is left running after the app stops.
Use SkipEmulatorStart with an already running emulator on any supported OS.
#>
[CmdletBinding()]
param(
    [ValidateRange(1024, 65535)][int]$WebPort = 5080,
    [ValidateRange(1024, 65535)][int]$CosmosPort = 8081,
    [ValidateRange(10, 600)][int]$StartupTimeoutSeconds = 180,
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Debug',
    [string]$TenantId = '',
    [string]$ClientId = '',
    [switch]$NoBrowser,
    [switch]$SkipEmulatorStart,
    [switch]$SmokeTest
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$source = Join-Path $root 'src'
$project = Join-Path $source 'AgentOps.Web\AgentOps.Web.csproj'
$web = Split-Path -Parent $project
$process = $null
Push-Location $source
try {
    Get-Command dotnet, node -ErrorAction Stop | Out-Null
    $npm = if ($IsWindows) { 'npm.cmd' } else { 'npm' }
    Get-Command $npm -ErrorAction Stop | Out-Null
    dotnet --version
    if ($LASTEXITCODE) { throw 'Install the SDK pinned in src\global.json.' }
    dotnet dev-certs https --check --quiet
    if ($LASTEXITCODE) { throw 'Run dotnet dev-certs https --trust first.' }
    $listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, $WebPort)
    try { $listener.Start() }
    catch [System.Net.Sockets.SocketException] { throw "Web port $WebPort is already in use." }
    finally { $listener.Stop() }
    if (-not $SkipEmulatorStart) {
        if (-not $IsWindows) { throw 'Start a Cosmos emulator first, then pass -SkipEmulatorStart.' }
        $emulator = Join-Path $env:ProgramFiles 'Azure Cosmos DB Emulator\Microsoft.Azure.Cosmos.Emulator.exe'
        if (-not (Test-Path $emulator)) { throw 'Install the native Azure Cosmos DB emulator or use -SkipEmulatorStart.' }
        $running = $false
        try {
            $probe = Invoke-WebRequest "https://localhost:$CosmosPort/_explorer/index.html" -TimeoutSec 2 -SkipCertificateCheck
            $running = $probe.StatusCode -eq 200
        } catch { }
        if (-not $running) {
            Write-Host 'Starting native Cosmos emulator (Windows elevation may be required).'
            Start-Process -FilePath $emulator -WorkingDirectory (Split-Path -Parent $emulator) `
                -ArgumentList "/NoUI /NoExplorer /Port=$CosmosPort" | Out-Null
            $deadline = [DateTimeOffset]::UtcNow.AddSeconds($StartupTimeoutSeconds)
            do {
                try {
                    $probe = Invoke-WebRequest "https://localhost:$CosmosPort/_explorer/index.html" -TimeoutSec 2 -SkipCertificateCheck
                    $running = $probe.StatusCode -eq 200
                } catch { }
                if (-not $running) { Start-Sleep -Milliseconds 500 }
            } while (-not $running -and [DateTimeOffset]::UtcNow -lt $deadline)
            if (-not $running) { throw 'The native emulator did not become ready. Start it from the Windows Start menu, then retry.' }
        }
    }
    if (-not (Test-Path (Join-Path $web 'node_modules'))) {
        & $npm --prefix $web ci --ignore-scripts --no-audit --no-fund
        if ($LASTEXITCODE) { throw 'npm ci failed.' }
    }
    & $npm --prefix $web run build
    if ($LASTEXITCODE) { throw 'Browser asset build failed.' }
    dotnet build $project --configuration $Configuration --verbosity quiet
    if ($LASTEXITCODE) { throw 'Application build failed.' }

    $url = "https://localhost:$WebPort"
    $start = [System.Diagnostics.ProcessStartInfo]::new('dotnet')
    $start.UseShellExecute = $false
    $start.WorkingDirectory = $web
    $start.ArgumentList.Add((Join-Path $web "bin\$Configuration\net10.0\AgentOps.Web.dll"))
    $start.Environment['ASPNETCORE_ENVIRONMENT'] = 'Development'
    $start.Environment['ASPNETCORE_URLS'] = $url
    if ($TenantId) { $start.Environment['AzureAd__TenantId'] = $TenantId }
    if ($ClientId) { $start.Environment['AzureAd__ClientId'] = $ClientId }
    $process = [System.Diagnostics.Process]::Start($start)
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($StartupTimeoutSeconds)
    $ready = $false
    do {
        if ($process.HasExited) { throw "Application exited with code $($process.ExitCode). Check startup errors." }
        try {
            $response = Invoke-WebRequest "$url/health/ready" -TimeoutSec 3 -SkipCertificateCheck -SkipHttpErrorCheck
            $ready = $response.StatusCode -eq 200 -and $response.Content -eq 'Healthy'
        } catch { }
        if (-not $ready) { Start-Sleep -Milliseconds 500 }
    } while (-not $ready -and [DateTimeOffset]::UtcNow -lt $deadline)
    if (-not $ready) { throw 'Application readiness timed out; check the Cosmos execution-history container and configuration.' }
    Write-Host "Web app and Cosmos are ready: $url (PID $($process.Id))"
    if ($SmokeTest) {
        $response = Invoke-WebRequest "$url/api/executions" -Method Post -SkipCertificateCheck -SkipHttpErrorCheck
        if ($response.StatusCode -ne 401) { throw "Anonymous execution returned $($response.StatusCode), expected 401." }
        foreach ($asset in @('/css/site.css', '/js/app.js')) {
            $response = Invoke-WebRequest "$url$asset" -SkipCertificateCheck -SkipHttpErrorCheck
            if ($response.StatusCode -ne 200 -or $response.RawContentLength -eq 0) { throw "Static asset failed: $asset" }
        }
        Write-Host 'Direct web runtime smoke test passed.'
    } else {
        if (-not $NoBrowser) { Start-Process $url | Out-Null }
        Write-Host 'Press Ctrl+C to stop the web app. The shared emulator is left running.'
        while (-not $process.HasExited) { Start-Sleep -Milliseconds 500 }
        if ($process.ExitCode) { throw "Application exited with code $($process.ExitCode)." }
    }
} finally {
    if ($null -ne $process -and -not $process.HasExited) {
        Stop-Process -Id $process.Id -ErrorAction SilentlyContinue
        $process.WaitForExit()
    }
    Pop-Location
}
