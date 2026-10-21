targetScope = 'resourceGroup'

@description('Short lowercase prefix. Names also include a deterministic unique suffix.')
@minLength(3)
@maxLength(12)
param prefix string = 'agentops'
param location string = resourceGroup().location
@description('Location supporting Microsoft Foundry accounts and projects.')
param foundryLocation string = location
param tenantId string = tenant().tenantId
@description('Existing Entra web/API application registration client ID.')
param entraClientId string
@secure()
@description('OIDC confidential web client credential, stored in Key Vault. Not a Cosmos/Foundry API key.')
param entraClientSecret string = ''
@secure()
@description('Webhook shared secret. Store in Key Vault and deliver through a secret Event Grid header.')
@minLength(1)
param webhookApiKey string
@description('Enable only after the deployed app passes readiness, so Event Grid can validate the webhook.')
param deployEventSubscription bool = false
@description('Provision infrastructure first; enable the app after image push.')
param deployApp bool = false
param imageTag string = 'latest'
@description('Optional sha256 digest. The orchestrator pins the verified build to this digest.')
param imageDigest string = ''
@description('Container repository inside the selected registry.')
param imageRepository string = 'agent-observatory'
@description('Existing ACR name. Empty creates a registry in this resource group.')
param existingRegistryName string = ''
param existingRegistryResourceGroup string = resourceGroup().name
param existingRegistrySubscriptionId string = subscription().subscriptionId
param existingCosmosAccountName string = ''
param existingCosmosResourceGroup string = resourceGroup().name
param existingCosmosSubscriptionId string = subscription().subscriptionId
@description('Use private connectivity without changing the shared Cosmos account network settings.')
param usePrivateCosmos bool = !empty(existingCosmosAccountName)
param usePrivateKeyVault bool = usePrivateCosmos
param vnetAddressPrefix string = '10.62.0.0/16'
param infrastructureSubnetPrefix string = '10.62.0.0/23'
param privateEndpointSubnetPrefix string = '10.62.2.0/27'
@description('Preserve the existing Container Apps subnet NSG when policy has attached one.')
param containerAppsNetworkSecurityGroupId string = ''
@description('Preserve the existing private endpoint subnet NSG when policy has attached one.')
param privateEndpointNetworkSecurityGroupId string = ''
param cosmosPrivateDnsZoneName string = 'privatelink.documents.azure.com'
param keyVaultPrivateDnsZoneName string = 'privatelink.vaultcore.azure.net'
@description('Optional existing Application Insights resource ID to observe. Defaults to this app telemetry.')
param observedInsightsResourceId string = ''
@description('Deploy a model without enabling the intentionally deferred agent behavior.')
param deployFoundryModel bool = true
param foundryModelName string = 'gpt-5.5'
@description('Empty lets Azure select the default version for the specified model.')
param foundryModelVersion string = ''
@description('Empty uses the model name as the deployment name.')
param foundryModelDeployment string = ''
param foundryModelFormat string = 'OpenAI'
param foundryModelSku string = 'GlobalStandard'
@minValue(1)
param foundryModelCapacity int = 1
param foundryProjectName string = 'observatory'
param scheduleEnabled bool = true
param scheduleCron string = '*/15 * * * *'
param cosmosDatabaseName string = 'agentops'
param cosmosContainerName string = 'executions'
param tags object = { application: 'agent-observatory' }

var suffix = uniqueString(resourceGroup().id)
var name = '${prefix}-${suffix}'
var registryName = empty(existingRegistryName) ? '${prefix}${suffix}' : existingRegistryName
var appName = '${prefix}-web'
var cosmosAccountName = empty(existingCosmosAccountName) ? '${name}-cosmos' : existingCosmosAccountName
var cosmosResourceGroup = empty(existingCosmosAccountName) ? resourceGroup().name : existingCosmosResourceGroup
var cosmosSubscriptionId = empty(existingCosmosAccountName) ? subscription().subscriptionId : existingCosmosSubscriptionId
var cosmosAccountId = resourceId(cosmosSubscriptionId, cosmosResourceGroup, 'Microsoft.DocumentDB/databaseAccounts', cosmosAccountName)
var modelDeploymentName = empty(foundryModelDeployment) ? foundryModelName : foundryModelDeployment
var usePrivateNetwork = usePrivateCosmos || usePrivateKeyVault
var imageReference = empty(imageDigest)
  ? '${registryAccess.outputs.loginServer}/${imageRepository}:${imageTag}'
  : '${registryAccess.outputs.loginServer}/${imageRepository}@${imageDigest}'

resource identity 'Microsoft.ManagedIdentity/userAssignedIdentities@2024-11-30' = {
  name: '${name}-app'
  location: location
  tags: tags
}

module registry 'modules/registry.bicep' = if (empty(existingRegistryName)) {
  name: '${prefix}-registry'
  params: { name: registryName, location: location, tags: tags }
}

module registryAccess 'modules/registry-access.bicep' = {
  name: '${prefix}-registry-access'
  scope: resourceGroup(
    empty(existingRegistryName) ? subscription().subscriptionId : existingRegistrySubscriptionId,
    empty(existingRegistryName) ? resourceGroup().name : existingRegistryResourceGroup
  )
  params: {
    registryName: registryName
    principalId: identity.properties.principalId
  }
  dependsOn: [registry]
}

resource workspace 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: '${name}-logs'
  location: location
  tags: tags
  properties: {
    sku: { name: 'PerGB2018' }
    retentionInDays: 30
  }
}

resource insights 'Microsoft.Insights/components@2020-02-02' = {
  name: '${name}-insights'
  location: location
  kind: 'web'
  tags: tags
  properties: {
    Application_Type: 'web'
    WorkspaceResourceId: workspace.id
  }
}

resource monitorReader 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(insights.id, identity.id, 'MonitoringReader')
  scope: insights
  properties: {
    principalId: identity.properties.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '43d0d8ad-25c7-4714-9337-8ba259a9fe05')
  }
}

module cosmosAccount 'modules/cosmos-account.bicep' = if (empty(existingCosmosAccountName)) {
  name: '${prefix}-cosmos-account'
  params: { name: cosmosAccountName, location: location, tags: tags, privateAccess: usePrivateCosmos }
}
module cosmosData 'modules/cosmos-data.bicep' = {
  name: '${prefix}-cosmos-data'
  scope: resourceGroup(cosmosSubscriptionId, cosmosResourceGroup)
  params: {
    accountName: cosmosAccountName
    databaseName: cosmosDatabaseName
    containerName: cosmosContainerName
    principalId: identity.properties.principalId
  }
  dependsOn: [cosmosAccount]
}

resource vault 'Microsoft.KeyVault/vaults@2024-11-01' = {
  name: '${take(prefix, 8)}${suffix}kv'
  location: location
  tags: tags
  properties: {
    tenantId: tenantId
    sku: { family: 'A', name: 'standard' }
    enableRbacAuthorization: true
    publicNetworkAccess: usePrivateKeyVault ? 'Disabled' : 'Enabled'
    enableSoftDelete: true
    enablePurgeProtection: true
    softDeleteRetentionInDays: 7
    accessPolicies: []
  }
}

resource oidcSecret 'Microsoft.KeyVault/vaults/secrets@2024-11-01' = if (!empty(entraClientSecret)) {
  parent: vault
  name: 'entra-client-secret'
  properties: { value: entraClientSecret }
}

resource webhookSecret 'Microsoft.KeyVault/vaults/secrets@2024-11-01' = {
  parent: vault
  name: 'webhook-api-key'
  properties: { value: webhookApiKey }
}

resource eventTopic 'Microsoft.EventGrid/topics@2025-02-15' = {
  name: '${name}-events'
  location: location
  tags: tags
  properties: {
    inputSchema: 'EventGridSchema'
    disableLocalAuth: true
    publicNetworkAccess: 'Enabled'
  }
}

resource vaultReader 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(vault.id, identity.id, 'KeyVaultSecretsUser')
  scope: vault
  properties: {
    principalId: identity.properties.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '4633458b-17de-408a-b874-0445c86b69e6')
  }
}

resource foundry 'Microsoft.CognitiveServices/accounts@2025-06-01' = {
  name: '${name}-foundry'
  location: foundryLocation
  tags: tags
  kind: 'AIServices'
  sku: { name: 'S0' }
  identity: { type: 'SystemAssigned' }
  properties: {
    customSubDomainName: '${name}-foundry'
    allowProjectManagement: true
    disableLocalAuth: true
    publicNetworkAccess: 'Enabled'
  }
}

resource foundryProject 'Microsoft.CognitiveServices/accounts/projects@2025-06-01' = {
  parent: foundry
  name: foundryProjectName
  location: foundryLocation
  identity: { type: 'SystemAssigned' }
  properties: {
    displayName: 'Agent Observatory'
    description: 'Agent implementation is intentionally deferred.'
  }
  dependsOn: [modelDeployment]
}

resource modelDeployment 'Microsoft.CognitiveServices/accounts/deployments@2025-06-01' = if (deployFoundryModel) {
  parent: foundry
  name: modelDeploymentName
  sku: { name: foundryModelSku, capacity: foundryModelCapacity }
  properties: {
    model: union({
      format: foundryModelFormat
      name: foundryModelName
    }, empty(foundryModelVersion) ? {} : { version: foundryModelVersion })
    versionUpgradeOption: 'OnceCurrentVersionExpired'
  }
}

resource foundryUser 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(foundryProject.id, identity.id, 'FoundryUser')
  scope: foundryProject
  properties: {
    principalId: identity.properties.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '53ca6127-db72-4b80-b1b0-d745d6d5456d')
  }
}

resource network 'Microsoft.Network/virtualNetworks@2024-05-01' = if (usePrivateNetwork) {
  name: '${name}-vnet'
  location: location
  tags: tags
  properties: {
    addressSpace: { addressPrefixes: [vnetAddressPrefix] }
    subnets: [
      {
        name: 'container-apps'
        properties: union({
          addressPrefix: infrastructureSubnetPrefix
          delegations: [{ name: 'container-apps', properties: { serviceName: 'Microsoft.App/environments' } }]
        }, empty(containerAppsNetworkSecurityGroupId) ? {} : {
          networkSecurityGroup: { id: containerAppsNetworkSecurityGroupId }
        })
      }
      {
        name: 'private-endpoints'
        properties: union({
          addressPrefix: privateEndpointSubnetPrefix
          privateEndpointNetworkPolicies: 'Disabled'
        }, empty(privateEndpointNetworkSecurityGroupId) ? {} : {
          networkSecurityGroup: { id: privateEndpointNetworkSecurityGroupId }
        })
      }
    ]
  }
}
resource privateDns 'Microsoft.Network/privateDnsZones@2024-06-01' = if (usePrivateCosmos) {
  name: cosmosPrivateDnsZoneName
  location: 'global'
  tags: tags
}
resource dnsLink 'Microsoft.Network/privateDnsZones/virtualNetworkLinks@2024-06-01' = if (usePrivateCosmos) {
  parent: privateDns
  name: '${name}-link'
  location: 'global'
  properties: { registrationEnabled: false, virtualNetwork: { id: network!.id } }
}
resource cosmosPrivateEndpoint 'Microsoft.Network/privateEndpoints@2024-05-01' = if (usePrivateCosmos) {
  name: '${name}-cosmos-pe'
  location: location
  tags: tags
  properties: {
    subnet: { id: '${network!.id}/subnets/private-endpoints' }
    privateLinkServiceConnections: [{
      name: 'cosmos-sql'
      properties: { privateLinkServiceId: cosmosAccountId, groupIds: ['Sql'] }
    }]
  }
  dependsOn: [cosmosAccount]
}
resource cosmosDnsGroup 'Microsoft.Network/privateEndpoints/privateDnsZoneGroups@2024-05-01' = if (usePrivateCosmos) {
  parent: cosmosPrivateEndpoint
  name: 'cosmos'
  properties: { privateDnsZoneConfigs: [{ name: 'cosmos', properties: { privateDnsZoneId: privateDns!.id } }] }
}

resource vaultPrivateDns 'Microsoft.Network/privateDnsZones@2024-06-01' = if (usePrivateKeyVault) {
  name: keyVaultPrivateDnsZoneName
  location: 'global'
  tags: tags
}
resource vaultDnsLink 'Microsoft.Network/privateDnsZones/virtualNetworkLinks@2024-06-01' = if (usePrivateKeyVault) {
  parent: vaultPrivateDns
  name: '${name}-vault-link'
  location: 'global'
  properties: { registrationEnabled: false, virtualNetwork: { id: network!.id } }
}
resource vaultPrivateEndpoint 'Microsoft.Network/privateEndpoints@2024-05-01' = if (usePrivateKeyVault) {
  name: '${name}-vault-pe'
  location: location
  tags: tags
  properties: {
    subnet: { id: '${network!.id}/subnets/private-endpoints' }
    privateLinkServiceConnections: [{
      name: 'key-vault'
      properties: { privateLinkServiceId: vault.id, groupIds: ['vault'] }
    }]
  }
}
resource vaultDnsGroup 'Microsoft.Network/privateEndpoints/privateDnsZoneGroups@2024-05-01' = if (usePrivateKeyVault) {
  parent: vaultPrivateEndpoint
  name: 'key-vault'
  properties: { privateDnsZoneConfigs: [{ name: 'vault', properties: { privateDnsZoneId: vaultPrivateDns!.id } }] }
}

resource environment 'Microsoft.App/managedEnvironments@2025-01-01' = {
  name: '${name}-env'
  location: location
  tags: tags
  properties: union({
    appLogsConfiguration: { destination: 'azure-monitor' }
    workloadProfiles: [{ name: 'Consumption', workloadProfileType: 'Consumption' }]
  }, usePrivateNetwork ? {
    vnetConfiguration: { infrastructureSubnetId: '${network!.id}/subnets/container-apps' }
  } : {})
}

resource environmentDiagnostics 'Microsoft.Insights/diagnosticSettings@2021-05-01-preview' = {
  name: 'container-logs'
  scope: environment
  properties: {
    workspaceId: workspace.id
    logs: [
      { category: 'ContainerAppConsoleLogs', enabled: true }
      { category: 'ContainerAppSystemLogs', enabled: true }
    ]
  }
}

resource app 'Microsoft.App/containerApps@2025-01-01' = if (deployApp) {
  name: appName
  location: location
  tags: tags
  identity: { type: 'UserAssigned', userAssignedIdentities: { '${identity.id}': {} } }
  properties: {
    managedEnvironmentId: environment.id
    configuration: {
      activeRevisionsMode: 'Single'
      ingress: { external: true, targetPort: 8080, transport: 'auto', allowInsecure: false }
      registries: [{ server: registryAccess.outputs.loginServer, identity: identity.id }]
      secrets: [
        {
          name: 'entra-client-secret'
          keyVaultUrl: '${vault.properties.vaultUri}secrets/entra-client-secret'
          identity: identity.id
        }
        {
          name: 'webhook-api-key'
          keyVaultUrl: '${vault.properties.vaultUri}secrets/${webhookSecret.name}'
          identity: identity.id
        }
      ]
    }
    template: {
      containers: [{
        name: 'web'
        image: imageReference
        resources: { cpu: json('0.5'), memory: '1Gi' }
        env: [
          { name: 'ASPNETCORE_ENVIRONMENT', value: 'Production' }
          { name: 'AZURE_CLIENT_ID', value: identity.properties.clientId }
          { name: 'AzureAd__TenantId', value: tenantId }
          { name: 'AzureAd__ClientId', value: entraClientId }
          { name: 'AzureAd__ClientSecret', secretRef: 'entra-client-secret' }
          { name: 'Webhook__ApiKey', secretRef: 'webhook-api-key' }
          { name: 'AllowedHosts', value: '${appName}.${environment.properties.defaultDomain}' }
          { name: 'Hosting__BehindContainerAppsIngress', value: 'true' }
          { name: 'ApplicationInsights__ConnectionString', value: insights.properties.ConnectionString }
          { name: 'Insights__ResourceId', value: empty(observedInsightsResourceId) ? insights.id : observedInsightsResourceId }
          { name: 'Cosmos__Endpoint', value: cosmosData.outputs.endpoint }
          { name: 'Cosmos__Database', value: cosmosDatabaseName }
          { name: 'Cosmos__Container', value: cosmosContainerName }
          { name: 'Scheduling__Enabled', value: string(scheduleEnabled) }
          { name: 'Scheduling__Cron', value: scheduleCron }
          { name: 'Foundry__ProjectEndpoint', value: 'https://${foundry.name}.services.ai.azure.com/api/projects/${foundryProject.name}' }
          { name: 'Foundry__ModelDeployment', value: modelDeploymentName }
        ]
        probes: [
          { type: 'Startup', httpGet: { path: '/health/live', port: 8080, scheme: 'HTTP', httpHeaders: [{ name: 'Host', value: '${appName}.${environment.properties.defaultDomain}' }] }, initialDelaySeconds: 5, periodSeconds: 5, failureThreshold: 60 }
          { type: 'Liveness', httpGet: { path: '/health/live', port: 8080, scheme: 'HTTP', httpHeaders: [{ name: 'Host', value: '${appName}.${environment.properties.defaultDomain}' }] }, periodSeconds: 30 }
          { type: 'Readiness', httpGet: { path: '/health/ready', port: 8080, scheme: 'HTTP', httpHeaders: [{ name: 'Host', value: '${appName}.${environment.properties.defaultDomain}' }] }, periodSeconds: 15 }
        ]
      }]
      // In-process SignalR and ephemeral cookie keys require this single-replica boundary.
      scale: { minReplicas: 1, maxReplicas: 1 }
    }
  }
  dependsOn: [oidcSecret, vaultReader, foundryUser, modelDeployment, cosmosDnsGroup, dnsLink, vaultDnsGroup, vaultDnsLink]
}

resource eventSubscription 'Microsoft.EventGrid/topics/eventSubscriptions@2025-02-15' = if (deployApp && deployEventSubscription) {
  parent: eventTopic
  name: 'agent-webhook'
  properties: {
    eventDeliverySchema: 'EventGridSchema'
    destination: {
      endpointType: 'WebHook'
      properties: {
        endpointUrl: 'https://${app!.properties.configuration.ingress.fqdn}/api/webhooks/eventgrid'
        maxEventsPerBatch: 1
        preferredBatchSizeInKilobytes: 64
        deliveryAttributeMappings: [{
          name: 'X-Api-Key'
          type: 'Static'
          properties: { value: webhookApiKey, isSecret: true }
        }]
      }
    }
    retryPolicy: { maxDeliveryAttempts: 3, eventTimeToLiveInMinutes: 60 }
  }
}

output registryName string = registryName
output registryLoginServer string = registryAccess.outputs.loginServer
output registryResourceGroup string = empty(existingRegistryName) ? resourceGroup().name : existingRegistryResourceGroup
output registrySubscriptionId string = empty(existingRegistryName) ? subscription().subscriptionId : existingRegistrySubscriptionId
output containerImage string = imageReference
output foundryAccountName string = foundry.name
output foundryProjectEndpoint string = 'https://${foundry.name}.services.ai.azure.com/api/projects/${foundryProject.name}'
output foundryDeploymentName string = modelDeploymentName
output containerAppName string = appName
output appIdentityName string = identity.name
output appIdentityPrincipalId string = identity.properties.principalId
output appIdentityClientId string = identity.properties.clientId
output applicationInsightsResourceId string = insights.id
output containerAppUrl string = deployApp ? 'https://${app!.properties.configuration.ingress.fqdn}' : ''
output cosmosAccountResourceId string = cosmosAccountId
output keyVaultName string = vault.name
output eventGridTopicName string = eventTopic.name
output eventGridTopicEndpoint string = eventTopic.properties.endpoint
output eventGridSubscriptionName string = 'agent-webhook'
