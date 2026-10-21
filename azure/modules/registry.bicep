param name string
param location string
param tags object = {}

resource registry 'Microsoft.ContainerRegistry/registries@2025-11-01' = {
  name: name
  location: location
  tags: tags
  sku: { name: 'Basic' }
  properties: {
    adminUserEnabled: false
    anonymousPullEnabled: false
    roleAssignmentMode: 'LegacyRegistryPermissions'
  }
}
