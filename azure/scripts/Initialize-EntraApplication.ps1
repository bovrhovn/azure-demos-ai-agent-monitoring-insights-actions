#Requires -Version 7.0
<#
.SYNOPSIS
Creates or configures the dedicated Entra web/API registration for Agent Observatory.
.DESCRIPTION
Requires application registration permissions. Credentials are returned only as a
SecureString property; capture the result in a variable and never serialize it.
Use ClientId to update callbacks without issuing another credential.
#>
[CmdletBinding()]
param(
    [string]$DisplayName = 'Agent Observatory',
    [guid]$ClientId = [guid]::Empty,
    [uri]$ApplicationUrl,
    [switch]$CreateClientSecret
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
function Invoke-Azure([string[]]$Arguments) {
    $result = & az @Arguments
    if ($LASTEXITCODE) { throw "Entra registration command failed: az $($Arguments[0..1] -join ' ')" }
    return $result
}
if ($ApplicationUrl -and ($ApplicationUrl.Scheme -ne 'https' -or $ApplicationUrl.AbsolutePath -ne '/')) {
    throw 'ApplicationUrl must be an HTTPS origin without a path.'
}
if ($ClientId -eq [guid]::Empty) {
    $matches = @(Invoke-Azure @('ad', 'app', 'list', '--display-name', $DisplayName, '-o', 'json') | ConvertFrom-Json |
        Where-Object displayName -EQ $DisplayName)
    if ($matches.Count) { throw 'This display name already exists. Supply its ClientId explicitly to avoid modifying another registration.' }
    $app = Invoke-Azure @('ad', 'app', 'create', '--display-name', $DisplayName,
        '--sign-in-audience', 'AzureADMyOrg', '-o', 'json') | ConvertFrom-Json
    $ClientId = [guid]$app.appId
} else {
    $app = Invoke-Azure @('ad', 'app', 'show', '--id', $ClientId.ToString(), '-o', 'json') | ConvertFrom-Json
}
$roles = @($app.appRoles)
foreach ($role in @('AgentOps.Reader', 'AgentOps.Operator')) {
    if (@($roles | ForEach-Object value) -notcontains $role) {
        $roles += @{
            id = [guid]::NewGuid().ToString(); value = $role; displayName = $role
            description = "Agent Observatory $($role.Split('.')[-1]) access"
            allowedMemberTypes = @('User', 'Application'); isEnabled = $true
        }
    }
}
$scopes = @($app.api.oauth2PermissionScopes)
if (@($scopes | ForEach-Object value) -notcontains 'Agent.Run') {
    $scopes += @{
        id = [guid]::NewGuid().ToString(); value = 'Agent.Run'; type = 'Admin'; isEnabled = $true
        adminConsentDisplayName = 'Run Agent Observatory executions'
        adminConsentDescription = 'Start agent executions as the signed-in operator.'
    }
}
$body = @{
    appRoles = $roles
    identifierUris = @(@($app.identifierUris) + "api://$ClientId" | Select-Object -Unique)
    api = @{ requestedAccessTokenVersion = 2; oauth2PermissionScopes = $scopes }
}
if ($ApplicationUrl) {
    $origin = $ApplicationUrl.GetLeftPart([UriPartial]::Authority)
    $body.web = @{
        redirectUris = @(@($app.web.redirectUris) + "$origin/signin-oidc" + "$origin/signout-callback-oidc" |
            Select-Object -Unique)
    }
}
$bodyPath = Join-Path ([System.IO.Path]::GetTempPath()) "agentops-entra-$([guid]::NewGuid().ToString('N')).json"
try {
    $body | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $bodyPath -Encoding utf8NoBOM
    Invoke-Azure @('rest', '--method', 'PATCH', '--url', "https://graph.microsoft.com/v1.0/applications/$($app.id)",
        '--body', "@$bodyPath", '-o', 'none') | Out-Null
} finally {
    if (Test-Path -LiteralPath $bodyPath) { Remove-Item -LiteralPath $bodyPath -Force }
}
$principals = @(Invoke-Azure @('ad', 'sp', 'list', '--filter', "appId eq '$ClientId'", '-o', 'json') | ConvertFrom-Json)
if ($principals.Count -eq 0) {
    Invoke-Azure @('ad', 'sp', 'create', '--id', $ClientId.ToString(), '-o', 'none') | Out-Null
}
$credential = $null
if ($CreateClientSecret) {
    $plain = Invoke-Azure @('ad', 'app', 'credential', 'reset', '--id', $ClientId.ToString(), '--append',
        '--display-name', 'Agent Observatory web sign-in', '--query', 'password', '-o', 'tsv')
    $credential = ConvertTo-SecureString $plain.Trim() -AsPlainText -Force
    $plain = $null
}
[pscustomobject]@{ ClientId = $ClientId; ObjectId = $app.id; ClientSecret = $credential }
