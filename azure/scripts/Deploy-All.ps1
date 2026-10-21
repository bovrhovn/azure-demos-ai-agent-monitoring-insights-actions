#Requires -Version 7.0
<#
.SYNOPSIS
Builds in ACR Tasks and deploys Agent Observatory using Bicep.
.DESCRIPTION
Creates a registry when AcrName is omitted; otherwise reuses the supplied registry.
Provisions infrastructure and a configurable Foundry model,
builds/pushes the image, deploys the app, and waits for readiness. No local Docker is needed.
Use -WhatIf for a local plan without Azure changes.
.PARAMETER TemplateParameters
Additional Bicep parameters, for example scheduleCron, observedInsightsResourceId,
foundryModelCapacity, foundryModelVersion or deployFoundryModel. Workflow-owned
parameters (identity, registry, image and deployApp) cannot be overridden here.
#>
[CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'Medium')]
param(
    [Parameter(Mandatory)][string]$ResourceGroup,
    [Parameter(Mandatory)][guid]$EntraClientId,
    [string]$Location = 'swedencentral',
    [string]$FoundryLocation = '',
    [ValidatePattern('^[a-z][a-z0-9]{2,11}$')][string]$Prefix = 'agentops',
    [string]$SubscriptionId = '',
    [ValidatePattern('^$|^[a-zA-Z0-9]{5,50}$')][string]$AcrName = '',
    [string]$AcrResourceGroup = '',
    [string]$AcrSubscriptionId = '',
    [string]$CosmosAccountName = '',
    [string]$CosmosResourceGroup = '',
    [string]$CosmosSubscriptionId = '',
    [ValidatePattern('^[a-z0-9]+(?:[._/-][a-z0-9]+)*$')][string]$ImageRepository = 'agent-observatory',
    [ValidatePattern('^$|^[a-zA-Z0-9_][a-zA-Z0-9_.-]{0,127}$')][string]$ImageTag = '',
    [ValidatePattern('^$|^[a-zA-Z0-9._-]+$')][string]$ModelName = '',
    [ValidatePattern('^$|^[a-zA-Z0-9._-]+$')][string]$ModelDeploymentName = '',
    [securestring]$EntraClientSecret,
    [switch]$ReuseEntraClientSecret,
    [securestring]$WebhookApiKey,
    [hashtable]$TemplateParameters = @{},
    [switch]$SkipBuild,
    [ValidateRange(1, 3600)][int]$ReadinessTimeoutSeconds = 300,
    [ValidateRange(1, 60)][int]$PollSeconds = 5
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$azureRoot = Join-Path $root 'azure'
$template = Join-Path $azureRoot 'main.bicep'

function Invoke-Azure([string[]]$Arguments) {
    $previousEncoding = $env:PYTHONIOENCODING
    try {
        $env:PYTHONIOENCODING = 'utf-8'
        $result = & az @Arguments
        if ($LASTEXITCODE -ne 0) { throw "Azure CLI failed: az $($Arguments -join ' ')" }
        return $result
    } finally {
        $env:PYTHONIOENCODING = $previousEncoding
    }
}
function Write-Step([int]$Number, [string]$Message) {
    Write-Host "`n[$Number/6] $Message" -ForegroundColor Cyan
}

Get-Command az -ErrorAction Stop | Out-Null
if ($SkipBuild -and -not $ImageTag) { throw 'Supply ImageTag when using SkipBuild.' }
if (-not $ImageTag) { $ImageTag = Get-Date -AsUTC -Format 'yyyyMMddHHmmss' }
if (-not $FoundryLocation) { $FoundryLocation = $Location }
Write-Step 1 'Compiling Bicep and checking deployment settings'
$compiled = (Invoke-Azure @('bicep', 'build', '--file', $template, '--stdout')) | ConvertFrom-Json -AsHashtable
$accountArgs = @('account', 'show', '--output', 'json')
if ($SubscriptionId) { $accountArgs += @('--subscription', $SubscriptionId) }
$account = (Invoke-Azure $accountArgs) | ConvertFrom-Json
$SubscriptionId = $account.id
if (-not $AcrResourceGroup) { $AcrResourceGroup = $ResourceGroup }
if (-not $AcrSubscriptionId) { $AcrSubscriptionId = $SubscriptionId }
if (-not $CosmosResourceGroup) { $CosmosResourceGroup = $ResourceGroup }
if (-not $CosmosSubscriptionId) { $CosmosSubscriptionId = $SubscriptionId }
$parameters = @{
    prefix = $Prefix; location = $Location; foundryLocation = $FoundryLocation
    tenantId = $account.tenantId; entraClientId = $EntraClientId.ToString()
    existingRegistryName = $AcrName; existingRegistryResourceGroup = $AcrResourceGroup
    existingRegistrySubscriptionId = $AcrSubscriptionId
    existingCosmosAccountName = $CosmosAccountName; existingCosmosResourceGroup = $CosmosResourceGroup
    existingCosmosSubscriptionId = $CosmosSubscriptionId
    imageRepository = $ImageRepository; imageTag = $ImageTag; imageDigest = ''; deployApp = $false
    deployEventSubscription = $false
}
foreach ($key in $TemplateParameters.Keys) {
    if (-not $compiled.parameters.ContainsKey($key)) { throw "Unknown Bicep parameter: $key" }
    if ($parameters.ContainsKey($key) -or $key -in @('entraClientSecret', 'webhookApiKey')) { throw "Use the dedicated script parameter for $key." }
    $parameters[$key] = $TemplateParameters[$key]
}
if ($ModelName) { $parameters.foundryModelName = $ModelName }
if ($ModelDeploymentName) { $parameters.foundryModelDeployment = $ModelDeploymentName }
function Get-ParameterValue([string]$Name) {
    if ($parameters.ContainsKey($Name)) { return $parameters[$Name] }
    return $compiled.parameters[$Name].defaultValue
}
$model = Get-ParameterValue 'foundryModelName'
$deployment = Get-ParameterValue 'foundryModelDeployment'
if (-not $deployment) { $deployment = $model }
$modelEnabled = Get-ParameterValue 'deployFoundryModel'
Write-Host "Registry: $(if ($AcrName) { "reuse $AcrName in $AcrResourceGroup" } else { 'create a deterministic registry in the app resource group' })"
Write-Host "Image: ${ImageRepository}:$ImageTag; Foundry: $FoundryLocation, model=$model, deployment=$deployment, enabled=$modelEnabled"
if (-not $PSCmdlet.ShouldProcess("$SubscriptionId/$ResourceGroup",
        'Provision infrastructure/model, build and push image, deploy Container App')) { return }

if ($AcrName) {
    Invoke-Azure @('acr', 'show', '--name', $AcrName, '--resource-group', $AcrResourceGroup,
        '--subscription', $AcrSubscriptionId, '--output', 'none') | Out-Null
}
if ($CosmosAccountName) {
    $cosmos = (Invoke-Azure @('cosmosdb', 'show', '--name', $CosmosAccountName,
        '--resource-group', $CosmosResourceGroup, '--subscription', $CosmosSubscriptionId, '--output', 'json')) | ConvertFrom-Json
    if ($cosmos.kind -ne 'GlobalDocumentDB') { throw 'The existing Cosmos account must support the NoSQL API.' }
    if ($cosmos.publicNetworkAccess -eq 'Disabled' -and $TemplateParameters.ContainsKey('usePrivateCosmos') -and -not $TemplateParameters.usePrivateCosmos) {
        throw 'The existing Cosmos account requires private connectivity.'
    }
}
if ($modelEnabled) {
    Write-Host 'Checking the regional model catalogue (quota/access are still enforced by Azure at deployment).'
    $catalogue = @(Invoke-Azure @('cognitiveservices', 'model', 'list', '--location', $FoundryLocation,
        '--subscription', $SubscriptionId, '--output', 'json') | ConvertFrom-Json)
    $version = Get-ParameterValue 'foundryModelVersion'
    $format = Get-ParameterValue 'foundryModelFormat'
    $sku = Get-ParameterValue 'foundryModelSku'
    $available = @($catalogue | Where-Object {
        $skus = if ($_.model.PSObject.Properties['skus']) { $_.model.skus } else { $_.skus }
        $_.model.name -eq $model -and $_.model.format -eq $format -and
        (-not $version -or $_.model.version -eq $version) -and
        ($skus.name -contains $sku)
    })
    if ($available.Count -eq 0) {
        throw "Model '$model' version '$version' with SKU '$sku' is not listed in '$FoundryLocation'. Select a supported model/location; no fallback was deployed."
    }
}
if ($ReuseEntraClientSecret -and $EntraClientSecret) { throw 'Do not supply EntraClientSecret with ReuseEntraClientSecret.' }
if (-not $ReuseEntraClientSecret -and (-not $EntraClientSecret -or $EntraClientSecret.Length -eq 0)) {
    if ($env:AZURE_CLIENT_SECRET) {
        $EntraClientSecret = ConvertTo-SecureString $env:AZURE_CLIENT_SECRET -AsPlainText -Force
    } else {
        $EntraClientSecret = Read-Host 'Entra confidential web client secret' -AsSecureString
    }
}
if (-not $ReuseEntraClientSecret -and (-not $EntraClientSecret -or $EntraClientSecret.Length -eq 0)) { throw 'An Entra web client secret is required.' }
if (-not $WebhookApiKey -or $WebhookApiKey.Length -eq 0) {
    if ($env:AGENTOPS_WEBHOOK_API_KEY) {
        $WebhookApiKey = ConvertTo-SecureString $env:AGENTOPS_WEBHOOK_API_KEY -AsPlainText -Force
    } else {
        $WebhookApiKey = Read-Host 'Event Grid webhook API key' -AsSecureString
    }
}
if (-not $WebhookApiKey -or $WebhookApiKey.Length -eq 0) { throw 'A webhook API key is required.' }

$previousParameters = $env:AGENTOPS_DEPLOY_PARAMETERS
$previousSecret = $env:AGENTOPS_DEPLOY_ENTRA_CLIENT_SECRET
$previousWebhookSecret = $env:AGENTOPS_DEPLOY_WEBHOOK_API_KEY
$parameterFile = Join-Path $azureRoot ".deploy.$([guid]::NewGuid().ToString('N')).bicepparam"
try {
    $env:AGENTOPS_DEPLOY_ENTRA_CLIENT_SECRET = if ($ReuseEntraClientSecret) { '' } else { [System.Net.NetworkCredential]::new('', $EntraClientSecret).Password }
    $env:AGENTOPS_DEPLOY_WEBHOOK_API_KEY = [System.Net.NetworkCredential]::new('', $WebhookApiKey).Password
    $lines = @("using './main.bicep'", "param webhookApiKey = readEnvironmentVariable('AGENTOPS_DEPLOY_WEBHOOK_API_KEY')")
    if (-not $ReuseEntraClientSecret) { $lines += "param entraClientSecret = readEnvironmentVariable('AGENTOPS_DEPLOY_ENTRA_CLIENT_SECRET')" }
    $lines += foreach ($key in ($parameters.Keys | Sort-Object)) {
        "param $key = json(readEnvironmentVariable('AGENTOPS_DEPLOY_PARAMETERS')).$key"
    }
    Set-Content -LiteralPath $parameterFile -Value $lines -Encoding utf8NoBOM
    $env:AGENTOPS_DEPLOY_PARAMETERS = $parameters | ConvertTo-Json -Depth 20 -Compress
    # Validate parameter expressions without writing a compiled credential to disk or output.
    Invoke-Azure @('bicep', 'build-params', '--file', $parameterFile, '--stdout') | Out-Null
    $exists = Invoke-Azure @('group', 'exists', '--name', $ResourceGroup, '--subscription', $SubscriptionId)
    if ($exists.Trim() -eq 'false') {
        Invoke-Azure @('group', 'create', '--name', $ResourceGroup, '--location', $Location,
            '--subscription', $SubscriptionId, '--output', 'none') | Out-Null
    } elseif ($exists.Trim() -ne 'true') {
        throw "Unexpected response checking resource group: $exists"
    }
    Write-Step 2 'Provisioning ACR/access, Cosmos containers, telemetry, Foundry project and model'
    $deployArgs = @('deployment', 'group', 'create', '--name', "$Prefix-infrastructure",
        '--resource-group', $ResourceGroup, '--subscription', $SubscriptionId,
        '--parameters', $parameterFile, '--query', 'properties.outputs', '--output', 'json')
    $outputs = (Invoke-Azure $deployArgs) | ConvertFrom-Json
    $registry = $outputs.registryName.value
    $registryGroup = $outputs.registryResourceGroup.value
    $registrySubscription = $outputs.registrySubscriptionId.value
    Write-Host "Registry ready: $($outputs.registryLoginServer.value)"
    Write-Host "Foundry project: $($outputs.foundryProjectEndpoint.value); deployment: $($outputs.foundryDeploymentName.value)"

    Write-Step 3 'Building and pushing the Linux image through ACR Tasks'
    if ($SkipBuild) {
        Write-Host 'Build skipped; verifying the explicitly selected image already exists.'
    } else {
        $registryInfo = (Invoke-Azure @('acr', 'show', '--name', $registry, '--resource-group', $registryGroup,
            '--subscription', $registrySubscription, '--output', 'json')) | ConvertFrom-Json -AsHashtable
        $buildArgs = @('acr', 'run', '--registry', $registry, '--resource-group', $registryGroup,
            '--subscription', $registrySubscription, '--file', 'azure/acr-task.yaml', '--platform', 'linux',
            '--set', "imageRepository=$ImageRepository", '--set', "imageTag=$ImageTag")
        if ($registryInfo['roleAssignmentMode'] -eq 'AbacRepositoryPermissions') {
            $buildArgs += @('--source-acr-auth-id', '[caller]')
        }
        $buildArgs += $root
        Invoke-Azure $buildArgs | Out-Host
    }
    $digest = Invoke-Azure @('acr', 'repository', 'show', '--name', $registry,
        '--subscription', $registrySubscription, '--image', "${ImageRepository}:$ImageTag", '--query', 'digest', '--output', 'tsv')
    if ($digest -notmatch '^sha256:[a-fA-F0-9]{64}$') { throw 'The registry did not return a valid image digest.' }
    Write-Host "Image verified: $($outputs.containerImage.value) ($digest)" -ForegroundColor Green

    Write-Step 4 'Deploying the Container App with the verified image and managed identity'
    $parameters.deployApp = $true
    $parameters.imageDigest = $digest.Trim()
    $env:AGENTOPS_DEPLOY_PARAMETERS = $parameters | ConvertTo-Json -Depth 20 -Compress
    $deployArgs[4] = "$Prefix-application"
    $outputs = (Invoke-Azure $deployArgs) | ConvertFrom-Json
    $url = $outputs.containerAppUrl.value
    if (-not [uri]::IsWellFormedUriString($url, [UriKind]::Absolute) -or ([uri]$url).Scheme -ne 'https') {
        throw 'The deployment did not return an HTTPS Container App URL.'
    }
    Write-Step 5 "Waiting for persistence and application readiness at $url"
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($ReadinessTimeoutSeconds)
    $ready = $false
    do {
        try {
            $response = Invoke-WebRequest "$url/health/ready" -SkipHttpErrorCheck -TimeoutSec 10 -MaximumRedirection 0
            if ($response.StatusCode -eq 200) { $ready = $true; break }
            Write-Host "Readiness returned HTTP $($response.StatusCode); waiting for startup/RBAC propagation."
        } catch {
            Write-Warning "Readiness request failed: $($_.Exception.Message)"
        }
        Start-Sleep -Seconds $PollSeconds
    } while ([DateTimeOffset]::UtcNow -lt $deadline)
    if (-not $ready) { throw "Readiness timed out after $ReadinessTimeoutSeconds seconds. Inspect Container Apps logs and database/RBAC configuration." }
    Write-Step 6 'Registering Event Grid webhook and completing the authenticated validation handshake'
    $parameters.deployEventSubscription = $true
    $env:AGENTOPS_DEPLOY_PARAMETERS = $parameters | ConvertTo-Json -Depth 20 -Compress
    $deployArgs[4] = "$Prefix-eventgrid"
    $outputs = (Invoke-Azure $deployArgs) | ConvertFrom-Json
    $subscriptionState = Invoke-Azure @('eventgrid', 'event-subscription', 'show',
        '--name', $outputs.eventGridSubscriptionName.value,
        '--source-resource-id', "/subscriptions/$SubscriptionId/resourceGroups/$ResourceGroup/providers/Microsoft.EventGrid/topics/$($outputs.eventGridTopicName.value)",
        '--query', 'provisioningState', '--output', 'tsv')
    if ($subscriptionState.Trim() -ne 'Succeeded') { throw "Event Grid webhook validation did not succeed: $subscriptionState" }
    Write-Host "Application: $url" -ForegroundColor Green
    Write-Host "Register Entra callbacks: $url/signin-oidc and $url/signout-callback-oidc"
    Write-Host 'Assign reader/operator roles and complete a real sign-in. Agent behavior remains NotImplemented.'
    return $outputs
} finally {
    $env:AGENTOPS_DEPLOY_PARAMETERS = $previousParameters
    $env:AGENTOPS_DEPLOY_ENTRA_CLIENT_SECRET = $previousSecret
    $env:AGENTOPS_DEPLOY_WEBHOOK_API_KEY = $previousWebhookSecret
    if (Test-Path -LiteralPath $parameterFile) { Remove-Item -LiteralPath $parameterFile -Force }
}
