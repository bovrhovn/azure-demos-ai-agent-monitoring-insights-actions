param registryName string
param principalId string

resource registry 'Microsoft.ContainerRegistry/registries@2025-11-01' existing = {
  name: registryName
}

var pullRole = (registry.properties.?roleAssignmentMode ?? 'LegacyRegistryPermissions') == 'AbacRepositoryPermissions'
  ? 'b93aa761-3e63-49ed-ac28-beffa264f7ac'
  : '7f951dda-4ed3-4680-a7ca-43fe172d538d'

resource pull 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(registry.id, principalId, 'container-pull')
  scope: registry
  properties: {
    principalId: principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', pullRole)
  }
}

output loginServer string = registry.properties.loginServer
