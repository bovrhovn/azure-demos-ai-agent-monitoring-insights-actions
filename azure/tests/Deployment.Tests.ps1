BeforeAll {
    $script:azureRoot = Split-Path -Parent $PSScriptRoot
    $script:deployScript = Join-Path $script:azureRoot 'scripts\Deploy-All.ps1'
    $script:realAz = (Get-Command az -CommandType Application | Select-Object -First 1).Source
    $script:compiledJson = & az bicep build --file (Join-Path $script:azureRoot 'main.bicep') --stdout
    if ($LASTEXITCODE) { throw 'Actual Bicep compilation failed.' }
    $script:template = $script:compiledJson | ConvertFrom-Json -AsHashtable
    $script:testSecret = ConvertTo-SecureString 'test-only-not-a-credential' -AsPlainText -Force
    $script:originalParameters = $env:AGENTOPS_DEPLOY_PARAMETERS
    $script:originalSecret = $env:AGENTOPS_DEPLOY_ENTRA_CLIENT_SECRET
    $script:originalWebhookSecret = $env:AGENTOPS_DEPLOY_WEBHOOK_API_KEY
    $script:previousTestState = Get-Variable AgentOpsDeploymentTestState -Scope Global -ErrorAction SilentlyContinue
    $global:AgentOpsDeploymentTestState = @{
        CompiledJson = $script:compiledJson
        RealAz = $script:realAz
    }

    function az {
        param([Parameter(ValueFromRemainingArguments)][string[]]$Arguments)
        $global:LASTEXITCODE = 0
        $command = $Arguments -join ' '
        $state = $global:AgentOpsDeploymentTestState
        $state.LastEncoding = $env:PYTHONIOENCODING
        $state.Calls.Add($command)
        if ($state.Failure -and $command.Contains($state.Failure)) {
            $global:LASTEXITCODE = 1
            return
        }
        switch -Wildcard ($command) {
            'bicep build --file *' { return $state.CompiledJson }
            'bicep build-params *' {
                $file = $Arguments[[array]::IndexOf($Arguments, '--file') + 1]
                $state.GeneratedFiles.Add($file)
                $state.ParameterSource = Get-Content -LiteralPath $file -Raw
                $result = & $state.RealAz bicep build-params --file $file --stdout
                if ($LASTEXITCODE) { throw 'Actual generated parameter file compilation failed.' }
                return $result
            }
            'account show *' { return '{"id":"test-subscription","tenantId":"11111111-1111-1111-1111-111111111111"}' }
            'cognitiveservices model list *' { return $state.Catalogue }
            'group exists *' { return $state.GroupExists }
            'group create *' { return }
            'acr show *' { return (@{ roleAssignmentMode = $state.RegistryMode } | ConvertTo-Json) }
            'cosmosdb show *' { return '{"kind":"GlobalDocumentDB","publicNetworkAccess":"Disabled"}' }
            'ad app list *' { return '[]' }
            'ad app create *' { return '{"id":"test-object","appId":"44444444-4444-4444-4444-444444444444","appRoles":[],"identifierUris":[],"api":{"oauth2PermissionScopes":[]},"web":{"redirectUris":[]}}' }
            'ad app show *' { return '{"id":"test-object","appId":"44444444-4444-4444-4444-444444444444","appRoles":[],"identifierUris":[],"api":{"oauth2PermissionScopes":[]},"web":{"redirectUris":[]}}' }
            'ad sp list *' { return '[]' }
            'ad sp create *' { return }
            'ad app credential reset *' { return 'test-only-credential' }
            'rest --method PATCH *' {
                $body = $Arguments[[array]::IndexOf($Arguments, '--body') + 1].TrimStart('@')
                $state.EntraBody = Get-Content -LiteralPath $body -Raw | ConvertFrom-Json
                return
            }
            'acr run *' { return 'Test build completed' }
            'acr repository show *' { return $state.Digest }
            'eventgrid event-subscription show *' { return 'Succeeded' }
            'deployment group create *' {
                $settings = $env:AGENTOPS_DEPLOY_PARAMETERS | ConvertFrom-Json -AsHashtable
                $state.Deployments.Add($settings)
                $state.SecretDuringDeployment = $env:AGENTOPS_DEPLOY_ENTRA_CLIENT_SECRET
                $state.WebhookSecretDuringDeployment = $env:AGENTOPS_DEPLOY_WEBHOOK_API_KEY
                $outputs = @{
                    registryName = 'testregistry'
                    registryLoginServer = 'testregistry.azurecr.io'
                    registryResourceGroup = $(if ($settings.existingRegistryName) { $settings.existingRegistryResourceGroup } else { 'test-rg' })
                    registrySubscriptionId = $settings.existingRegistrySubscriptionId
                    containerImage = 'testregistry.azurecr.io/agent-observatory:test'
                    foundryProjectEndpoint = 'https://test.services.ai.azure.com/api/projects/observatory'
                    foundryDeploymentName = 'gpt-5.5'
                    appIdentityName = 'test-app'
                    appIdentityPrincipalId = '33333333-3333-3333-3333-333333333333'
                    eventGridTopicName = 'test-events'
                    eventGridSubscriptionName = 'agent-webhook'
                    containerAppUrl = $(if ($settings.deployApp) { 'https://test.example' } else { '' })
                }
                $wrapped = @{}
                foreach ($key in $outputs.Keys) { $wrapped[$key] = @{ value = $outputs[$key] } }
                return $wrapped | ConvertTo-Json -Depth 5
            }
            default { throw "Unexpected Azure CLI command in test: $command" }
        }
    }
}

AfterAll {
    $env:AGENTOPS_DEPLOY_PARAMETERS = $script:originalParameters
    $env:AGENTOPS_DEPLOY_ENTRA_CLIENT_SECRET = $script:originalSecret
    $env:AGENTOPS_DEPLOY_WEBHOOK_API_KEY = $script:originalWebhookSecret
    if ($null -eq $script:previousTestState) {
        Remove-Variable AgentOpsDeploymentTestState -Scope Global
    } else {
        $global:AgentOpsDeploymentTestState = $script:previousTestState.Value
    }
}

Describe 'Compiled infrastructure contract' {
    It 'keeps the webhook key in Key Vault and a secret Event Grid header, never a URL' {
        $script:template.parameters.webhookApiKey.type | Should -Be 'secureString'
        $secret = $script:template.resources | Where-Object {
            $_.type -eq 'Microsoft.KeyVault/vaults/secrets' -and $_.name -match 'webhook-api-key'
        }
        $secret.properties.value | Should -Be "[parameters('webhookApiKey')]"
        $app = $script:template.resources | Where-Object type -EQ 'Microsoft.App/containerApps'
        $app.properties.configuration.secrets | Where-Object name -EQ 'webhook-api-key' |
            ForEach-Object keyVaultUrl | Should -Match 'vaultUri'
        $app.properties.template.containers[0].env | Where-Object name -EQ 'Webhook__ApiKey' |
            ForEach-Object secretRef | Should -Be 'webhook-api-key'
        $subscription = $script:template.resources | Where-Object type -EQ 'Microsoft.EventGrid/topics/eventSubscriptions'
        $subscription.condition | Should -Match 'deployEventSubscription'
        $destination = $subscription.properties.destination.properties
        $destination.endpointUrl | Should -Match '/api/webhooks/eventgrid'
        $destination.endpointUrl | Should -Not -Match 'webhookApiKey|\?'
        $header = $destination.deliveryAttributeMappings[0]
        $header.name | Should -Be 'X-Api-Key'
        $header.properties.isSecret | Should -BeTrue
        $header.properties.value | Should -Be "[parameters('webhookApiKey')]"
        $script:compiledJson | Should -Not -Match 'Microsoft.DBforPostgreSQL|Hangfire__Host|Hangfire__Database|Hangfire__Username'
    }

    It 'bounds Key Vault names and serializes Foundry child writes' {
        $vault = $script:template.resources | Where-Object type -EQ 'Microsoft.KeyVault/vaults'
        $vault.name | Should -Match "take\(parameters\('prefix'\), 8\)"
        $project = $script:template.resources | Where-Object type -EQ 'Microsoft.CognitiveServices/accounts/projects'
        ($project.dependsOn -join ',') | Should -Match 'Microsoft.CognitiveServices/accounts/deployments'
        $dataModule = $script:template.resources | Where-Object { $_.name -match 'cosmos-data' }
        $containers = @($dataModule.properties.template.resources | Where-Object { $_.type -match '/containers$' })
        $containers.Count | Should -Be 1
        ($containers.properties.resource.partitionKey.paths | ForEach-Object { $_ }) | Should -Contain '/trigger'
        $script:template.parameters.ContainsKey('cosmosScheduleContainerName') | Should -BeFalse
        $app = $script:template.resources | Where-Object type -EQ 'Microsoft.App/containerApps'
        @($app.properties.template.containers[0].env | Where-Object name -EQ 'Cosmos__ScheduleContainer').Count | Should -Be 0
    }

    It 'connects private Key Vault references before creating the Container App' {
        $vault = $script:template.resources | Where-Object type -EQ 'Microsoft.KeyVault/vaults'
        $vault.properties.publicNetworkAccess | Should -Match 'usePrivateKeyVault'
        $script:template.parameters.keyVaultPrivateDnsZoneName.defaultValue | Should -Be 'privatelink.vaultcore.azure.net'
        $endpoint = $script:template.resources | Where-Object {
            $_.type -eq 'Microsoft.Network/privateEndpoints' -and $_.name -match 'vault-pe'
        }
        $endpoint.properties.privateLinkServiceConnections[0].properties.groupIds | Should -Contain 'vault'
        $app = $script:template.resources | Where-Object type -EQ 'Microsoft.App/containerApps'
        ($app.dependsOn -join ',') | Should -Match 'vault-link'
        ($app.dependsOn -join ',') | Should -Match 'vault-pe'
    }

    It 'can preserve policy-managed subnet security groups during redeployment' {
        $network = $script:template.resources | Where-Object type -EQ 'Microsoft.Network/virtualNetworks'
        $network.properties.subnets[0].properties | Should -Match 'containerAppsNetworkSecurityGroupId'
        $network.properties.subnets[1].properties | Should -Match 'privateEndpointNetworkSecurityGroupId'
    }

    It 'defaults to GPT-5.5, a configurable deployment name and a billable-resource opt-out' {
        $script:template.parameters.foundryModelName.defaultValue | Should -Be 'gpt-5.5'
        $script:template.parameters.foundryModelDeployment.defaultValue | Should -Be ''
        $script:template.parameters.foundryModelVersion.defaultValue | Should -Be ''
        $script:template.parameters.foundryModelSku.defaultValue | Should -Be 'GlobalStandard'
        $script:template.parameters.deployFoundryModel.defaultValue | Should -BeTrue
        $deployment = @($script:template.resources | Where-Object type -EQ 'Microsoft.CognitiveServices/accounts/deployments')
        $deployment.Count | Should -Be 1
        $deployment[0].condition | Should -Be "[parameters('deployFoundryModel')]"
        $deployment[0].properties.model | Should -Match 'foundryModelName'
    }

    It 'creates ACR only when no existing registry is selected and scopes access separately' {
        $modules = @($script:template.resources | Where-Object type -EQ 'Microsoft.Resources/deployments')
        $newRegistry = $modules | Where-Object { $_.name -match '-registry' -and $_.name -notmatch '-access' }
        $newRegistry.condition | Should -Be "[empty(parameters('existingRegistryName'))]"
        $access = $modules | Where-Object { $_.name -match 'registry-access' }
        $access.properties.template.resources.type | Should -Not -Contain 'Microsoft.ContainerRegistry/registries'
        $access.resourceGroup | Should -Match 'existingRegistryResourceGroup'
        $access.subscriptionId | Should -Match 'existingRegistrySubscriptionId'
        $access.properties.template.resources[0].properties.roleDefinitionId | Should -Match 'AbacRepositoryPermissions'
    }

    It 'uses managed identity, the verified image reference and forwarded ingress configuration' {
        $app = $script:template.resources | Where-Object type -EQ 'Microsoft.App/containerApps'
        $app.properties.template.containers[0].image | Should -Match 'imageDigest'
        $app.properties.template.containers[0].env | Where-Object name -EQ 'Hosting__BehindContainerAppsIngress' |
            ForEach-Object value | Should -Be 'true'
        $app.properties.configuration.registries[0].identity | Should -Match 'userAssignedIdentities'
        $script:template.parameters.entraClientSecret.type | Should -Be 'secureString'
    }

    It 'explicitly enables BuildKit and pushes the same parameterized image' {
        $task = Get-Content -LiteralPath (Join-Path $script:azureRoot 'acr-task.yaml') -Raw
        $task | Should -Match 'DOCKER_BUILDKIT=1'
        $task | Should -Match '--platform linux/amd64'
        $task | Should -Match '-f containers/Dockerfile'
        $image = '$Registry/{{.Values.imageRepository}}:{{.Values.imageTag}}'
        ([regex]::Matches($task, [regex]::Escape($image))).Count | Should -Be 2
        $task | Should -Match '- push:'
    }

    It 'preserves a reused Cosmos account while provisioning only application data and private connectivity' {
        $modules = @($script:template.resources | Where-Object type -EQ 'Microsoft.Resources/deployments')
        ($modules | Where-Object { $_.name -match 'cosmos-account' }).condition |
            Should -Be "[empty(parameters('existingCosmosAccountName'))]"
        $data = $modules | Where-Object { $_.name -match 'cosmos-data' }
        $data.properties.template.resources.type | Should -Not -Contain 'Microsoft.DocumentDB/databaseAccounts'
        $data.resourceGroup | Should -Match 'cosmosResourceGroup'
        $data.properties.template.resources.type | Should -Contain 'Microsoft.DocumentDB/databaseAccounts/sqlRoleAssignments'
        $script:template.resources.type | Should -Contain 'Microsoft.Network/privateEndpoints'
        $environment = $script:template.resources | Where-Object type -EQ 'Microsoft.App/managedEnvironments'
        ($environment.properties | ConvertTo-Json -Depth 20) | Should -Match 'infrastructureSubnetId'
    }
}

Describe 'Deployment orchestration with Azure commands mocked' {
    BeforeEach {
        $script:calls = [System.Collections.Generic.List[string]]::new()
        $script:deployments = [System.Collections.Generic.List[hashtable]]::new()
        $script:generatedFiles = [System.Collections.Generic.List[string]]::new()
        $state = $global:AgentOpsDeploymentTestState
        $state.Calls = $script:calls
        $state.Deployments = $script:deployments
        $state.GeneratedFiles = $script:generatedFiles
        $state.Failure = ''
        $state.GroupExists = 'false'
        $state.RegistryMode = 'LegacyRegistryPermissions'
        $state.Digest = "sha256:$('a' * 64)"
        $state.ReadinessCalls = 0
        $state.Catalogue = '[{"model":{"name":"gpt-5.5","format":"OpenAI","version":"2026-04-24"},"skus":[{"name":"GlobalStandard"}]}]'
        $script:arguments = @{
            ResourceGroup = 'test-rg'
            EntraClientId = '22222222-2222-2222-2222-222222222222'
            EntraClientSecret = $script:testSecret
            WebhookApiKey = $script:testSecret
            ImageTag = 'test'
            Confirm = $false
        }
        $env:AGENTOPS_DEPLOY_PARAMETERS = 'original-test-environment'
        $env:AGENTOPS_DEPLOY_ENTRA_CLIENT_SECRET = 'original-test-secret'
        Mock Invoke-WebRequest { [pscustomobject]@{ StatusCode = 200 } }
        Mock Start-Sleep { }
    }

    AfterEach {
        $env:AGENTOPS_DEPLOY_PARAMETERS | Should -Be 'original-test-environment'
        $env:AGENTOPS_DEPLOY_ENTRA_CLIENT_SECRET | Should -Be 'original-test-secret'
        foreach ($file in $script:generatedFiles) { Test-Path -LiteralPath $file | Should -BeFalse }
    }

    It 'provisions Cosmos, builds and verifies an image before enabling the app' {
        $result = & $script:deployScript @script:arguments
        $result.containerAppUrl.value | Should -Be 'https://test.example'
        $script:deployments.Count | Should -Be 3
        $script:deployments[0].deployApp | Should -BeFalse
        $script:deployments[0].existingRegistryName | Should -Be ''
        $script:deployments[1].deployApp | Should -BeTrue
        $script:deployments[1].imageDigest | Should -Be "sha256:$('a' * 64)"
        $build = @($script:calls | Where-Object { $_ -like 'acr run *' })
        $build.Count | Should -Be 1
        $build[0] | Should -Match '--file azure/acr-task.yaml'
        $build[0] | Should -Match '--set imageRepository=agent-observatory --set imageTag=test'
        $script:calls.IndexOf($build[0]) | Should -BeGreaterThan 0
        $firstDeployment = $script:calls | Where-Object { $_ -like 'deployment group create *infrastructure*' }
        $lastDeployment = $script:calls | Where-Object { $_ -like 'deployment group create *application*' }
        $script:calls.IndexOf($build[0]) | Should -BeGreaterThan $script:calls.IndexOf($firstDeployment)
        $script:calls.IndexOf($lastDeployment) | Should -BeGreaterThan $script:calls.IndexOf($build[0])
        $script:deployments[0].deployEventSubscription | Should -BeFalse
        $script:deployments[1].deployEventSubscription | Should -BeFalse
        $script:deployments[2].deployEventSubscription | Should -BeTrue
        $script:calls[-1] | Should -Match 'eventgrid event-subscription show'
        Should -Invoke Invoke-WebRequest -Times 1 -Exactly -ParameterFilter { $Uri -eq 'https://test.example/health/ready' }
    }

    It 'passes secrets only via restored process environment, never files or CLI arguments' {
        & $script:deployScript @script:arguments | Out-Null
        $global:AgentOpsDeploymentTestState.SecretDuringDeployment | Should -Be 'test-only-not-a-credential'
        $global:AgentOpsDeploymentTestState.WebhookSecretDuringDeployment | Should -Be 'test-only-not-a-credential'
        $global:AgentOpsDeploymentTestState.ParameterSource | Should -Match "readEnvironmentVariable\('AGENTOPS_DEPLOY_WEBHOOK_API_KEY'\)"
        $global:AgentOpsDeploymentTestState.ParameterSource | Should -Match "readEnvironmentVariable\('AGENTOPS_DEPLOY_ENTRA_CLIENT_SECRET'\)"
        $global:AgentOpsDeploymentTestState.ParameterSource | Should -Not -Match 'test-only-not-a-credential'
        ($script:calls -join "`n") | Should -Not -Match 'test-only-not-a-credential'
    }

    It 'preserves an existing Entra credential without requiring its value' {
        $script:arguments.Remove('EntraClientSecret')
        $script:arguments.ReuseEntraClientSecret = $true
        & $script:deployScript @script:arguments | Out-Null
        $global:AgentOpsDeploymentTestState.ParameterSource | Should -Not -Match 'param entraClientSecret'
        $script:deployments.Count | Should -Be 3
    }

    It 'does not report success when Event Grid endpoint validation fails' {
        $global:AgentOpsDeploymentTestState.Failure = 'deployment group create --name agentops-eventgrid'
        { & $script:deployScript @script:arguments } | Should -Throw '*Azure CLI failed*'
        @($script:calls | Where-Object { $_ -like 'eventgrid event-subscription show *' }).Count | Should -Be 0
    }

    It 'reuses a cross-resource-group ABAC registry and authenticates ACR Tasks as caller' {
        $script:arguments.AcrName = 'existingregistry'
        $script:arguments.AcrResourceGroup = 'shared-rg'
        $script:arguments.AcrSubscriptionId = 'shared-subscription'
        $global:AgentOpsDeploymentTestState.RegistryMode = 'AbacRepositoryPermissions'
        & $script:deployScript @script:arguments | Out-Null
        $script:deployments[0].existingRegistryName | Should -Be 'existingregistry'
        $script:deployments[0].existingRegistryResourceGroup | Should -Be 'shared-rg'
        $script:calls | Where-Object { $_ -like 'acr run *' } |
            Should -Match '--subscription shared-subscription.*--source-acr-auth-id \[caller\]'
    }

    It 'accepts custom model, deployment, version, capacity and schedule settings' {
        $script:arguments.ModelName = 'custom-model'
        $script:arguments.ModelDeploymentName = 'custom-deployment'
        $script:arguments.TemplateParameters = @{ foundryModelCapacity = 2; foundryModelVersion = '2026-01-01'; scheduleCron = '0 * * * *' }
        $global:AgentOpsDeploymentTestState.Catalogue = '[{"model":{"name":"custom-model","format":"OpenAI","version":"2026-01-01"},"skus":[{"name":"GlobalStandard"}]}]'
        & $script:deployScript @script:arguments | Out-Null
        $script:deployments[0].foundryModelName | Should -Be 'custom-model'
        $script:deployments[1].foundryModelDeployment | Should -Be 'custom-deployment'
        $script:deployments[1].foundryModelVersion | Should -Be '2026-01-01'
        $script:deployments[1].foundryModelCapacity | Should -Be 2
        $script:deployments[1].scheduleCron | Should -Be '0 * * * *'
    }

    It 'can opt out of model deployment without checking the model catalogue' {
        $script:arguments.TemplateParameters = @{ deployFoundryModel = $false }
        & $script:deployScript @script:arguments | Out-Null
        @($script:calls | Where-Object { $_ -like 'cognitiveservices model list *' }).Count | Should -Be 0
    }

    It 'supports redeployment without rebuilding' {
        $script:arguments.SkipBuild = $true
        $global:AgentOpsDeploymentTestState.GroupExists = 'true'
        & $script:deployScript @script:arguments | Out-Null
        @($script:calls | Where-Object { $_ -like 'acr run *' -or $_ -like 'group create *' }).Count | Should -Be 0
        $script:deployments.Count | Should -Be 3
    }

    It 'stops before applying an app when a cloud build fails and cleans up' {
        $global:AgentOpsDeploymentTestState.Failure = 'acr run'
        { & $script:deployScript @script:arguments } | Should -Throw '*Azure CLI failed*'
        $script:deployments.Count | Should -Be 1
        Should -Invoke Invoke-WebRequest -Times 0
    }

    It 'does not deploy an unverified image digest' {
        $global:AgentOpsDeploymentTestState.Digest = 'not-a-digest'
        { & $script:deployScript @script:arguments } | Should -Throw '*valid image digest*'
        $script:deployments.Count | Should -Be 1
    }

    It 'does not report success when the application deployment fails' {
        $global:AgentOpsDeploymentTestState.Failure = 'deployment group create --name agentops-application'
        { & $script:deployScript @script:arguments } | Should -Throw '*Azure CLI failed*'
        Should -Invoke Invoke-WebRequest -Times 0
    }

    It 'waits for a transient readiness failure and then succeeds' {
        Mock Invoke-WebRequest {
            $global:AgentOpsDeploymentTestState.ReadinessCalls++
            [pscustomobject]@{ StatusCode = $(if ($global:AgentOpsDeploymentTestState.ReadinessCalls -eq 1) { 503 } else { 200 }) }
        }
        & $script:deployScript @script:arguments | Out-Null
        Should -Invoke Invoke-WebRequest -Times 2 -Exactly
    }

    It 'fails explicitly if persistence never becomes ready' {
        $script:arguments.ReadinessTimeoutSeconds = 1
        Mock Invoke-WebRequest {
            [System.Threading.Thread]::Sleep(1100)
            [pscustomobject]@{ StatusCode = 503 }
        }
        { & $script:deployScript @script:arguments } | Should -Throw '*Readiness timed out*'
    }

    It 'fails unsupported models before creating any Azure resources' {
        $global:AgentOpsDeploymentTestState.Catalogue = '[]'
        { & $script:deployScript @script:arguments } | Should -Throw '*no fallback was deployed*'
        $script:deployments.Count | Should -Be 0
        @($script:calls | Where-Object { $_ -like 'group create *' }).Count | Should -Be 0
    }

    It 'does not modify Azure during WhatIf' {
        & $script:deployScript @script:arguments -WhatIf | Out-Null
        $script:deployments.Count | Should -Be 0
        @($script:calls | Where-Object { $_ -like 'acr run *' -or $_ -like 'group create *' }).Count | Should -Be 0
        $script:generatedFiles.Count | Should -Be 0
    }

    It 'streams Unicode Azure build logs as UTF-8 and restores the caller encoding' {
        $previousEncoding = $env:PYTHONIOENCODING
        try {
            $env:PYTHONIOENCODING = 'ascii'
            & $script:deployScript @script:arguments -WhatIf | Out-Null
            $global:AgentOpsDeploymentTestState.LastEncoding | Should -Be 'utf-8'
            $env:PYTHONIOENCODING | Should -Be 'ascii'
        } finally {
            $env:PYTHONIOENCODING = $previousEncoding
        }
    }

    It 'reuses the selected Cosmos account without disabling private access' {
        $script:arguments.CosmosAccountName = 'sharedcosmos'
        $script:arguments.CosmosResourceGroup = 'shared-rg'
        & $script:deployScript @script:arguments | Out-Null
        $script:deployments[0].existingCosmosAccountName | Should -Be 'sharedcosmos'
        $script:deployments[1].existingCosmosResourceGroup | Should -Be 'shared-rg'
    }

    It 'reads SKU availability nested under the actual regional model response' {
        $global:AgentOpsDeploymentTestState.Catalogue = '[{"model":{"name":"gpt-5.5","format":"OpenAI","version":"2026-04-24","skus":[{"name":"GlobalStandard"}]}}]'
        & $script:deployScript @script:arguments | Out-Null
        $script:deployments.Count | Should -Be 3
    }

    It 'creates a single-tenant web/API registration with typed credential output' {
        $registration = & (Join-Path $script:azureRoot 'scripts\Initialize-EntraApplication.ps1') -CreateClientSecret
        $registration.ClientId.ToString() | Should -Be '44444444-4444-4444-4444-444444444444'
        $registration.ClientSecret | Should -BeOfType [securestring]
        $global:AgentOpsDeploymentTestState.EntraBody.appRoles.value | Should -Contain 'AgentOps.Operator'
        $global:AgentOpsDeploymentTestState.EntraBody.api.oauth2PermissionScopes.value | Should -Contain 'Agent.Run'
        $global:AgentOpsDeploymentTestState.EntraBody.api.requestedAccessTokenVersion | Should -Be 2
    }

    It 'updates actual HTTPS callbacks without rotating credentials' {
        & (Join-Path $script:azureRoot 'scripts\Initialize-EntraApplication.ps1') `
            -ClientId '44444444-4444-4444-4444-444444444444' -ApplicationUrl 'https://test.example' | Out-Null
        $global:AgentOpsDeploymentTestState.EntraBody.web.redirectUris | Should -Contain 'https://test.example/signin-oidc'
        ($script:calls | Where-Object { $_ -like 'ad app credential reset *' }).Count | Should -Be 0
    }

    It 'rejects workflow overrides and unknown Bicep parameters' -ForEach @(
        @{ Parameters = @{ imageTag = 'unsafe' } }
        @{ Parameters = @{ nonexistentSetting = 'value' } }
    ) {
        $script:arguments.TemplateParameters = $Parameters
        { & $script:deployScript @script:arguments } | Should -Throw
        $script:deployments.Count | Should -Be 0
    }
}
