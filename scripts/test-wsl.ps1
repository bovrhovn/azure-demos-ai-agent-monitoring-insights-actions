#Requires -Version 7.0
<#
.SYNOPSIS
Builds and runs the same .NET tests under WSL with isolated Linux artifacts.
#>
param(
    [string]$Distribution = 'FedoraLinux-42',
    [string]$NuGetSource = '',
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release'
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$restore = @('restore', 'AgentOps.slnx', '--artifacts-path', '../.artifacts-wsl', '--verbosity', 'quiet')
if ($NuGetSource) { $restore += @('--source', $NuGetSource) }
& wsl --cd (Join-Path $root 'src') -d $Distribution --exec dotnet @restore
if ($LASTEXITCODE) { throw 'WSL restore failed.' }
& wsl --cd (Join-Path $root 'src') -d $Distribution --exec dotnet test AgentOps.slnx --artifacts-path ../.artifacts-wsl --configuration $Configuration --no-restore --verbosity minimal
if ($LASTEXITCODE) { throw 'WSL tests failed.' }
