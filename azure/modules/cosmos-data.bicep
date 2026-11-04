param accountName string
param databaseName string
param containerName string
param chatContainerName string
param principalId string

resource account 'Microsoft.DocumentDB/databaseAccounts@2025-04-15' existing = {
  name: accountName
}
resource database 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases@2025-04-15' = {
  parent: account
  name: databaseName
  properties: { resource: { id: databaseName } }
}
resource container 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases/containers@2025-04-15' = {
  parent: database
  name: containerName
  properties: {
    resource: {
      id: containerName
      partitionKey: { paths: ['/trigger'], kind: 'Hash', version: 2 }
      indexingPolicy: {
        indexingMode: 'consistent'
        automatic: true
        includedPaths: [{ path: '/*' }]
        excludedPaths: [{ path: '/"_etag"/?' }]
      }
    }
  }
}
resource chatContainer 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases/containers@2025-04-15' = {
  parent: database
  name: chatContainerName
  properties: {
    resource: {
      id: chatContainerName
      partitionKey: { paths: ['/ownerId'], kind: 'Hash', version: 2 }
      indexingPolicy: {
        indexingMode: 'consistent'
        automatic: true
        includedPaths: [{ path: '/*' }]
        excludedPaths: [{ path: '/"_etag"/?' }, { path: '/content/?' }]
      }
    }
  }
}
resource contributor 'Microsoft.DocumentDB/databaseAccounts/sqlRoleAssignments@2025-04-15' = {
  parent: account
  name: guid(account.id, principalId, databaseName, 'execution-data')
  properties: {
    principalId: principalId
    roleDefinitionId: '${account.id}/sqlRoleDefinitions/00000000-0000-0000-0000-000000000002'
    scope: '${account.id}/dbs/${databaseName}'
  }
  dependsOn: [container, chatContainer]
}
output endpoint string = account.properties.documentEndpoint
