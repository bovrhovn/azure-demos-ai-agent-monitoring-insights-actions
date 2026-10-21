param name string
param location string
param tags object
param privateAccess bool

resource account 'Microsoft.DocumentDB/databaseAccounts@2025-04-15' = {
  name: name
  location: location
  kind: 'GlobalDocumentDB'
  tags: tags
  properties: {
    databaseAccountOfferType: 'Standard'
    disableLocalAuth: true
    minimalTlsVersion: 'Tls12'
    consistencyPolicy: { defaultConsistencyLevel: 'Session' }
    locations: [{ locationName: location, failoverPriority: 0, isZoneRedundant: false }]
    capabilities: [{ name: 'EnableServerless' }]
    publicNetworkAccess: privateAccess ? 'Disabled' : 'Enabled'
  }
}
