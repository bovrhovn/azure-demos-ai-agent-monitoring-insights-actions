using './main.bicep'

param prefix = 'agentops'
param location = 'swedencentral'
param entraClientId = readEnvironmentVariable('AZURE_CLIENT_ID')
param entraClientSecret = readEnvironmentVariable('AZURE_CLIENT_SECRET')
param webhookApiKey = readEnvironmentVariable('AGENTOPS_WEBHOOK_API_KEY')
param deployApp = false

// Model, version, deployment name, SKU and capacity default in main.bicep.
// Add explicit parameter values here to override them for a manual deployment.
// The automated scripts/Deploy-All.ps1 exposes these through named options and TemplateParameters.
