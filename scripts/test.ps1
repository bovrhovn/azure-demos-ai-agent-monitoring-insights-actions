#Requires -Version 7.0
<#
.SYNOPSIS
Builds browser assets and executes the .NET test suite.
#>
param([string]$Configuration = 'Release')
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$web = Join-Path $root 'src\AgentOps.Web'
Push-Location (Join-Path $root 'src')
try {
    $npm = if ($IsWindows) { 'npm.cmd' } else { 'npm' }
    & $npm --prefix $web ci --ignore-scripts --no-audit --no-fund
    if ($LASTEXITCODE) { throw 'npm ci failed.' }
    & $npm --prefix $web run build
    if ($LASTEXITCODE) { throw 'Browser asset build failed.' }
    dotnet test AgentOps.slnx --configuration $Configuration --verbosity minimal
    if ($LASTEXITCODE) { throw 'Tests failed.' }
} finally { Pop-Location }
