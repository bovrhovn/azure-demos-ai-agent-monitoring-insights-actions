#Requires -Version 7.0
<#
.SYNOPSIS
Configures secretless Entra login and managed-identity Cosmos access.
.DESCRIPTION
Uses an existing user-assigned managed identity and pre-provisioned Cosmos NoSQL
container. Creates an Entra registration, or updates one explicitly selected by
ClientId, preserving existing roles, scopes and callbacks. No credentials are created.
Attach the identity to the Azure host and apply the printed environment settings.
Requires Azure CLI sign-in, application-management permissions in Entra, and
Microsoft.DocumentDB/databaseAccounts/sqlRoleAssignments/write on the Cosmos account.
.PARAMETER ClientId
Existing login application ID. Omit only on the first run; reuse the printed ID on reruns.
.PARAMETER AppUrl
Public HTTPS application base URL, including any externally hosted path base.
#>
[CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'Medium')]
param(
    [Parameter(Mandatory)][ValidateNotNullOrEmpty()][string]$DisplayName,
    [Parameter(Mandatory)][uri]$AppUrl,
    [Parameter(Mandatory)][ValidatePattern('^/subscriptions/[^/]+/resourceGroups/[^/]+/providers/Microsoft\.ManagedIdentity/userAssignedIdentities/[^/]+$')]
    [string]$ManagedIdentityResourceId,
    [Parameter(Mandatory)][ValidateNotNullOrEmpty()][string]$CosmosAccountName,
    [Parameter(Mandatory)][ValidateNotNullOrEmpty()][string]$ResourceGroup,
    [string]$SubscriptionId = '',
    [guid]$ClientId,
    [ValidatePattern('^[^/?#\\]+$')][string]$Database = 'agentops',
    [ValidatePattern('^[^/?#\\]+$')][string]$Container = 'executions',
    [ValidateNotNullOrEmpty()][string]$ReaderRole = 'AgentOps.Reader',
    [ValidateNotNullOrEmpty()][string]$OperatorRole = 'AgentOps.Operator',
    [ValidateNotNullOrEmpty()][string]$RunScope = 'Agent.Run'
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Invoke-Azure([string[]]$Arguments) {
    $previousEncoding = $env:PYTHONIOENCODING
    try {
        $env:PYTHONIOENCODING = 'utf-8'
        $result = & az @Arguments --only-show-errors --output json
        if ($LASTEXITCODE -ne 0) { throw "Azure CLI failed: az $($Arguments -join ' ')" }
        if ($result) { return ($result -join "`n") | ConvertFrom-Json -AsHashtable }
    } finally {
        $env:PYTHONIOENCODING = $previousEncoding
    }
}

function Invoke-AzureJson([string[]]$Arguments, [hashtable]$Body) {
    $file = Join-Path ([IO.Path]::GetTempPath()) "agentops-identity-$([guid]::NewGuid().ToString('N')).json"
    try {
        $Body | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath $file -Encoding utf8NoBOM
        Invoke-Azure ($Arguments + @("@$file"))
    } finally {
        if (Test-Path -LiteralPath $file) { Remove-Item -LiteralPath $file -Force }
    }
}

if (-not $AppUrl.IsAbsoluteUri -or $AppUrl.Scheme -ne 'https' -or $AppUrl.UserInfo -or $AppUrl.Query -or $AppUrl.Fragment) {
    throw 'AppUrl must be an absolute HTTPS base URL without credentials, query or fragment.'
}
if ($ReaderRole -eq $OperatorRole) { throw 'ReaderRole and OperatorRole must differ.' }
if ($PSBoundParameters.ContainsKey('ClientId') -and $ClientId -eq [guid]::Empty) { throw 'ClientId must not be empty.' }
if (-not $PSCmdlet.ShouldProcess("$DisplayName; $ManagedIdentityResourceId; $CosmosAccountName/$Database/$Container",
        'Configure Entra login federation and Cosmos data-plane access')) { return }

Get-Command az -ErrorAction Stop | Out-Null
$cloud = Invoke-Azure @('cloud', 'show')
if ($cloud.name -ne 'AzureCloud') { throw 'This script currently supports Azure public cloud only.' }
$accountArgs = @('account', 'show')
if ($SubscriptionId) { $accountArgs += @('--subscription', $SubscriptionId) }
$account = Invoke-Azure $accountArgs
$SubscriptionId = $account.id
$tenantId = $account.tenantId
$identity = Invoke-Azure @('identity', 'show', '--ids', $ManagedIdentityResourceId)
if ($identity.tenantId -ne $tenantId) { throw 'The managed identity must belong to the signed-in subscription tenant.' }
foreach ($name in @('clientId', 'principalId')) {
    if (-not $identity[$name] -or -not [guid]::TryParse($identity[$name], [ref]([guid]::Empty))) {
        throw "The managed identity did not return a valid $name."
    }
}
$cosmosArgs = @('--account-name', $CosmosAccountName, '--resource-group', $ResourceGroup, '--subscription', $SubscriptionId)
$cosmos = Invoke-Azure @('cosmosdb', 'show', '--name', $CosmosAccountName, '--resource-group', $ResourceGroup, '--subscription', $SubscriptionId)
if ($cosmos.kind -ne 'GlobalDocumentDB') { throw 'The Cosmos account must support the NoSQL API.' }
$endpoint = [uri]$cosmos.documentEndpoint
if (-not $endpoint.IsAbsoluteUri -or $endpoint.Scheme -ne 'https') { throw 'Cosmos did not return a trusted HTTPS endpoint.' }
$existingContainer = Invoke-Azure (@('cosmosdb', 'sql', 'container', 'show') + $cosmosArgs +
    @('--database-name', $Database, '--name', $Container))
if (@($existingContainer.resource.partitionKey.paths).Count -ne 1 -or $existingContainer.resource.partitionKey.paths[0] -cne '/trigger') {
    throw 'The execution container must be pre-provisioned with the /trigger partition key.'
}

if ($ClientId) {
    $app = Invoke-Azure @('ad', 'app', 'show', '--id', $ClientId.ToString())
    if ($app.signInAudience -ne 'AzureADMyOrg') { throw 'The selected app must be single-tenant (AzureADMyOrg).' }
} else {
    $matches = @(Invoke-Azure @('ad', 'app', 'list', '--display-name', $DisplayName))
    if ($matches.Count) { throw 'An application with this display name already exists. Select it explicitly with ClientId.' }
    $app = Invoke-Azure @('ad', 'app', 'create', '--display-name', $DisplayName, '--sign-in-audience', 'AzureADMyOrg')
}
$ClientId = [guid]$app.appId
Write-Host "Login application ClientId: $ClientId (reuse with -ClientId on subsequent runs)"
$baseUrl = $AppUrl.AbsoluteUri.TrimEnd('/')
$roles = @($app.appRoles)
foreach ($role in @(
    @{ value = $ReaderRole; displayName = 'Execution reader'; description = 'Read Agent Observatory'; allowedMemberTypes = @('User') },
    @{ value = $OperatorRole; displayName = 'Execution operator'; description = 'Operate Agent Observatory'; allowedMemberTypes = @('User', 'Application') }
)) {
    $existing = @($roles | Where-Object value -CEQ $role.value)
    if ($existing.Count) {
        if (-not $existing[0].isEnabled -or @($role.allowedMemberTypes | Where-Object { $_ -notin $existing[0].allowedMemberTypes }).Count) {
            throw "Existing role '$($role.value)' is disabled or has incompatible member types."
        }
    } else {
        $roles += $role + @{ id = [guid]::NewGuid().ToString(); isEnabled = $true }
    }
}
$api = if ($app.api) { $app.api } else { @{} }
$scopes = @($api.oauth2PermissionScopes | Where-Object { $null -ne $_ })
$existingScope = @($scopes | Where-Object value -CEQ $RunScope)
if ($existingScope.Count -and -not $existingScope[0].isEnabled) { throw "Existing scope '$RunScope' is disabled." }
if (-not $existingScope.Count) {
    $scopes += @{
        id = [guid]::NewGuid().ToString(); value = $RunScope; type = 'Admin'; isEnabled = $true
        adminConsentDisplayName = 'Run the agent'; adminConsentDescription = 'Run Agent Observatory executions'
    }
}
$api.oauth2PermissionScopes = $scopes
$api.requestedAccessTokenVersion = 2
$web = if ($app.web) { $app.web } else { @{} }
$web.redirectUris = @(@($web.redirectUris) + @("$baseUrl/signin-oidc", "$baseUrl/signout-callback-oidc") |
    Where-Object { $_ } | Select-Object -Unique)
if (-not $web['implicitGrantSettings']) { $web.implicitGrantSettings = @{ enableAccessTokenIssuance = $false } }
$web.implicitGrantSettings.enableIdTokenIssuance = $true
$body = @{
    appRoles = $roles; api = $api; web = $web
    identifierUris = @(@($app.identifierUris) + "api://$ClientId" | Where-Object { $_ } | Select-Object -Unique)
}
Invoke-AzureJson @('rest', '--method', 'PATCH', '--uri', "https://graph.microsoft.com/v1.0/applications/$($app.id)", '--body') $body | Out-Null
$servicePrincipals = @(Invoke-Azure @('ad', 'sp', 'list', '--filter', "appId eq '$ClientId'"))
if (-not $servicePrincipals.Count) { Invoke-Azure @('ad', 'sp', 'create', '--id', $ClientId.ToString()) | Out-Null }

$federation = @{
    name = "agentops-$($identity.principalId)"
    issuer = "https://login.microsoftonline.com/$tenantId/v2.0"
    subject = $identity.principalId
    audiences = @('api://AzureADTokenExchange')
}
$credentials = @(Invoke-Azure @('ad', 'app', 'federated-credential', 'list', '--id', $app.id))
$trusted = @($credentials | Where-Object {
    $_.issuer -ceq $federation.issuer -and $_.subject -ceq $federation.subject -and
    @($_.audiences).Count -eq 1 -and $_.audiences[0] -ceq $federation.audiences[0]
})
if (-not $trusted.Count) {
    if (@($credentials | Where-Object name -EQ $federation.name).Count) { throw 'Federation name exists with a different trust. Review it before retrying.' }
    Invoke-AzureJson @('ad', 'app', 'federated-credential', 'create', '--id', $app.id, '--parameters') $federation | Out-Null
}

$scope = "$($cosmos.id)/dbs/$Database/colls/$Container"
$roleDefinition = "$($cosmos.id)/sqlRoleDefinitions/00000000-0000-0000-0000-000000000002"
$assignments = @(Invoke-Azure (@('cosmosdb', 'sql', 'role', 'assignment', 'list') + $cosmosArgs))
$assigned = @($assignments | Where-Object {
    $_.principalId -eq $identity.principalId -and $_.scope.TrimEnd('/') -eq $scope -and $_.roleDefinitionId -eq $roleDefinition
})
if (-not $assigned.Count) {
    Invoke-Azure (@('cosmosdb', 'sql', 'role', 'assignment', 'create') + $cosmosArgs +
        @('--principal-id', $identity.principalId, '--role-definition-id', $roleDefinition, '--scope', $scope)) | Out-Null
}

$settings = [ordered]@{
    ASPNETCORE_ENVIRONMENT = 'Production'
    AzureAd__Instance = 'https://login.microsoftonline.com/'
    AzureAd__TenantId = $tenantId
    AzureAd__ClientId = $ClientId.ToString()
    AzureAd__ReaderRole = $ReaderRole
    AzureAd__OperatorRole = $OperatorRole
    AzureAd__RunScope = $RunScope
    AzureAd__CallbackPath = '/signin-oidc'
    AzureAd__SignedOutCallbackPath = '/signout-callback-oidc'
    AzureAd__ClientCredentials__0__SourceType = 'SignedAssertionFromManagedIdentity'
    AzureAd__ClientCredentials__0__ManagedIdentityClientId = $identity.clientId
    AZURE_CLIENT_ID = $identity.clientId
    Cosmos__Endpoint = $endpoint.AbsoluteUri
    Cosmos__Database = $Database
    Cosmos__Container = $Container
    Cosmos__InitializeDatabase = 'false'
    Cosmos__AllowInsecureEmulatorTls = 'false'
}
Write-Host "`nCopy these non-secret environment settings to the Azure host:"
foreach ($key in $settings.Keys) { Write-Output "$key=$($settings[$key])" }
Write-Host "`nAttach managed identity: $ManagedIdentityResourceId"
Write-Host "Assign users/groups the '$ReaderRole' or '$OperatorRole' enterprise application role. Login alone grants no application access."
Write-Host 'Remove AzureAd__ClientSecret, AZURE_CLIENT_SECRET and Cosmos__ConnectionString from the host. Preserve separately required webhook configuration.'
Write-Host 'Allow time for federation/RBAC propagation, then verify real login and Cosmos readiness from the Azure host. No live authentication check was performed.'
