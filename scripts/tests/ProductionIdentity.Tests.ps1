BeforeAll {
    $script:setupScript = Join-Path $PSScriptRoot '..\setup-production-identity.ps1'
    $script:previousState = Get-Variable AgentOpsIdentityTestState -Scope Global -ErrorAction SilentlyContinue
    function az {
        param([Parameter(ValueFromRemainingArguments)][string[]]$Arguments)
        $global:LASTEXITCODE = 0
        $command = $Arguments -join ' '
        $state = $global:AgentOpsIdentityTestState
        $state.Calls.Add($command)
        foreach ($argument in $Arguments) {
            if ($argument.StartsWith('@')) { $state.Files.Add($argument.Substring(1)) }
        }
        if ($state.Failure -and $command.Contains($state.Failure)) {
            $global:LASTEXITCODE = 1
            return
        }
        switch -Wildcard ($command) {
            'cloud show *' { return @{ name = $state.Cloud } | ConvertTo-Json }
            'account show *' { return @{ id = 'cosmos-sub'; tenantId = $state.Tenant } | ConvertTo-Json }
            'identity show *' { return $state.Identity | ConvertTo-Json }
            'cosmosdb show *' { return $state.Cosmos | ConvertTo-Json }
            'cosmosdb sql container show *' { return @{ resource = @{ partitionKey = @{ paths = $state.Partitions } } } | ConvertTo-Json -Depth 5 }
            'ad app list *' { return ConvertTo-Json -InputObject $state.Matches -Depth 10 }
            'ad app create *' { return $state.App | ConvertTo-Json -Depth 10 }
            'ad app show *' { return $state.App | ConvertTo-Json -Depth 10 }
            'rest --method PATCH *' {
                $file = $Arguments[[array]::IndexOf($Arguments, '--body') + 1].TrimStart('@')
                $state.Patch = Get-Content -LiteralPath $file -Raw | ConvertFrom-Json -AsHashtable
                $state.App.appRoles = $state.Patch.appRoles
                $state.App.api = $state.Patch.api
                $state.App.web = $state.Patch.web
                $state.App.identifierUris = $state.Patch.identifierUris
                return
            }
            'ad sp list *' { return ConvertTo-Json -InputObject $state.ServicePrincipals }
            'ad sp create *' { $state.ServicePrincipals = @(@{ id = 'enterprise-app' }); return }
            'ad app federated-credential list *' { return ConvertTo-Json -InputObject $state.Federations -Depth 10 }
            'ad app federated-credential create *' {
                $file = $Arguments[[array]::IndexOf($Arguments, '--parameters') + 1].TrimStart('@')
                $state.Federation = Get-Content -LiteralPath $file -Raw | ConvertFrom-Json -AsHashtable
                $state.Federations = @($state.Federation)
                return
            }
            'cosmosdb sql role assignment list *' { return ConvertTo-Json -InputObject $state.Assignments }
            'cosmosdb sql role assignment create *' {
                $state.Assignments = @(@{
                    principalId = $Arguments[[array]::IndexOf($Arguments, '--principal-id') + 1]
                    scope = $Arguments[[array]::IndexOf($Arguments, '--scope') + 1]
                    roleDefinitionId = $Arguments[[array]::IndexOf($Arguments, '--role-definition-id') + 1]
                })
                return
            }
            default { throw "Unexpected Azure CLI command: $command" }
        }
    }
}

AfterAll {
    if ($null -eq $script:previousState) {
        Remove-Variable AgentOpsIdentityTestState -Scope Global -ErrorAction SilentlyContinue
    } else {
        $global:AgentOpsIdentityTestState = $script:previousState.Value
    }
}

Describe 'Production identity setup (offline Azure CLI contract)' {
    BeforeEach {
        $script:tenant = '11111111-1111-1111-1111-111111111111'
        $script:client = '22222222-2222-2222-2222-222222222222'
        $script:miClient = '33333333-3333-3333-3333-333333333333'
        $script:principal = '44444444-4444-4444-4444-444444444444'
        $script:cosmosId = '/subscriptions/cosmos-sub/resourceGroups/data-rg/providers/Microsoft.DocumentDB/databaseAccounts/test-cosmos'
        $script:parameters = @{
            DisplayName = 'Production observatory'
            AppUrl = 'https://observatory.example/operations/'
            ManagedIdentityResourceId = '/subscriptions/identity-sub/resourceGroups/identity-rg/providers/Microsoft.ManagedIdentity/userAssignedIdentities/observatory'
            CosmosAccountName = 'test-cosmos'; ResourceGroup = 'data-rg'; SubscriptionId = 'cosmos-sub'
        }
        $script:state = @{
            Calls = [Collections.Generic.List[string]]::new()
            Files = [Collections.Generic.List[string]]::new()
            Failure = ''; Cloud = 'AzureCloud'; Tenant = $script:tenant
            Identity = @{ tenantId = $script:tenant; clientId = $script:miClient; principalId = $script:principal }
            Cosmos = @{ id = $script:cosmosId; kind = 'GlobalDocumentDB'; documentEndpoint = 'https://test-cosmos.documents.azure.com/' }
            Partitions = @('/trigger')
            Matches = @(); ServicePrincipals = @(); Federations = @(); Assignments = @()
            App = @{
                id = 'app-object'; appId = $script:client; signInAudience = 'AzureADMyOrg'
                appRoles = @(); api = @{ oauth2PermissionScopes = @() }
                web = @{ redirectUris = @() }; identifierUris = @()
            }
        }
        $global:AgentOpsIdentityTestState = $script:state
    }

    It 'creates secretless login federation and grants Cosmos to the identity, not the registration' {
        $output = & $script:setupScript @script:parameters
        $settings = ConvertFrom-StringData ($output -join "`n")
        $settings.AzureAd__ClientId | Should -Be $script:client
        $settings.AZURE_CLIENT_ID | Should -Be $script:miClient
        $settings.AzureAd__ClientCredentials__0__SourceType | Should -Be 'SignedAssertionFromManagedIdentity'
        $settings.AzureAd__ClientCredentials__0__ManagedIdentityClientId | Should -Be $script:miClient
        $settings.AzureAd__TenantId | Should -Be $script:tenant
        $settings.Cosmos__Endpoint | Should -Be 'https://test-cosmos.documents.azure.com/'
        $settings.Cosmos__Database | Should -Be 'agentops'
        $settings.Cosmos__Container | Should -Be 'executions'
        $settings.Cosmos__InitializeDatabase | Should -Be 'false'
        $settings.Cosmos__AllowInsecureEmulatorTls | Should -Be 'false'
        $settings.ASPNETCORE_ENVIRONMENT | Should -Be 'Production'
        $settings.Keys | Should -Not -Contain 'AzureAd__ClientSecret'
        $settings.Keys | Should -Not -Contain 'Cosmos__ConnectionString'
        $script:state.Federation.subject | Should -Be $script:principal
        $script:state.Federation.issuer | Should -Be "https://login.microsoftonline.com/$script:tenant/v2.0"
        $script:state.Federation.audiences | Should -Be @('api://AzureADTokenExchange')
        $script:state.Assignments[0].principalId | Should -Be $script:principal
        $script:state.Assignments[0].scope | Should -Be "$script:cosmosId/dbs/agentops/colls/executions"
        $script:state.Assignments[0].roleDefinitionId | Should -Be "$script:cosmosId/sqlRoleDefinitions/00000000-0000-0000-0000-000000000002"
        $script:state.Patch.web.redirectUris | Should -Contain 'https://observatory.example/operations/signin-oidc'
        $script:state.Patch.web.redirectUris | Should -Contain 'https://observatory.example/operations/signout-callback-oidc'
        $script:state.Patch.web.implicitGrantSettings.enableIdTokenIssuance | Should -BeTrue
        $script:state.Patch.web.implicitGrantSettings.enableAccessTokenIssuance | Should -BeFalse
        $script:state.Patch.identifierUris | Should -Contain "api://$script:client"
        $script:state.Patch.api.requestedAccessTokenVersion | Should -Be 2
        $script:state.Patch.api.oauth2PermissionScopes[0].value | Should -Be 'Agent.Run'
        $script:state.Patch.api.oauth2PermissionScopes[0].type | Should -Be 'Admin'
        $reader = $script:state.Patch.appRoles | Where-Object value -EQ 'AgentOps.Reader'
        $operator = $script:state.Patch.appRoles | Where-Object value -EQ 'AgentOps.Operator'
        $reader.allowedMemberTypes | Should -Be @('User')
        $operator.allowedMemberTypes | Should -Be @('User', 'Application')
        ($script:state.Calls -join "`n") | Should -Not -Match 'credential reset|list-keys|list-connection-strings|--password'
        foreach ($file in $script:state.Files) { Test-Path -LiteralPath $file | Should -BeFalse }
    }

    It 'is rerunnable with ClientId and preserves existing registration configuration and role IDs' {
        $oldRole = @{ id = [guid]::NewGuid().ToString(); value = 'Unrelated.Role'; isEnabled = $true; allowedMemberTypes = @('User') }
        $oldScope = @{ id = [guid]::NewGuid().ToString(); value = 'Unrelated.Scope'; isEnabled = $true; type = 'Admin' }
        $script:state.App.appRoles = @($oldRole)
        $script:state.App.api = @{ oauth2PermissionScopes = @($oldScope); preAuthorizedApplications = @(@{ appId = 'other-app'; delegatedPermissionIds = @($oldScope.id) }) }
        $script:state.App.web = @{ redirectUris = @('https://other.example/signin-oidc'); logoutUrl = 'https://other.example/logout' }
        $script:state.App.identifierUris = @('api://existing-uri')
        & $script:setupScript @script:parameters | Out-Null
        $firstIds = @($script:state.Patch.appRoles.id)
        $firstScopeIds = @($script:state.Patch.api.oauth2PermissionScopes.id)
        $script:state.Calls.Clear()
        & $script:setupScript @script:parameters -ClientId $script:client | Out-Null
        $script:state.Patch.appRoles.id | Should -Be $firstIds
        $script:state.Patch.api.oauth2PermissionScopes.id | Should -Be $firstScopeIds
        $script:state.Patch.api.preAuthorizedApplications[0].appId | Should -Be 'other-app'
        $script:state.Patch.web.redirectUris | Should -Contain 'https://other.example/signin-oidc'
        $script:state.Patch.web.logoutUrl | Should -Be 'https://other.example/logout'
        $script:state.Patch.identifierUris | Should -Contain 'api://existing-uri'
        ($script:state.Calls -join "`n") | Should -Not -Match 'ad app create|ad sp create|federated-credential create|role assignment create'
    }

    It 'uses configurable database, container, roles and delegated scope' {
        $output = & $script:setupScript @script:parameters -Database history -Container runs -ReaderRole Demo.Read -OperatorRole Demo.Run -RunScope Runs.Execute
        $settings = ConvertFrom-StringData ($output -join "`n")
        $settings.Cosmos__Database | Should -Be history
        $settings.Cosmos__Container | Should -Be runs
        $settings.AzureAd__ReaderRole | Should -Be Demo.Read
        $settings.AzureAd__OperatorRole | Should -Be Demo.Run
        $settings.AzureAd__RunScope | Should -Be Runs.Execute
        $script:state.Patch.appRoles.value | Should -Be @('Demo.Read', 'Demo.Run')
        $script:state.Patch.api.oauth2PermissionScopes[0].value | Should -Be Runs.Execute
        $script:state.Assignments[0].scope | Should -Be "$script:cosmosId/dbs/history/colls/runs"
    }

    It 'enables ID tokens on existing registrations without changing their access-token issuance setting' -ForEach @(
        @{ AccessTokens = $false },
        @{ AccessTokens = $true }
    ) {
        $script:state.App.web.implicitGrantSettings = @{
            enableIdTokenIssuance = $false
            enableAccessTokenIssuance = $AccessTokens
        }
        & $script:setupScript @script:parameters -ClientId $script:client | Out-Null
        $script:state.Patch.web.implicitGrantSettings.enableIdTokenIssuance | Should -BeTrue
        $script:state.Patch.web.implicitGrantSettings.enableAccessTokenIssuance | Should -Be $AccessTokens
    }

    It 'supports offline WhatIf without calling Azure or emitting a success summary' {
        $output = & $script:setupScript @script:parameters -WhatIf
        $script:state.Calls.Count | Should -Be 0
        $output | Should -BeNullOrEmpty
    }

    It 'rejects unsafe app URL <Url> before any Azure operation' -ForEach @(
        @{ Url = 'http://observatory.example' },
        @{ Url = 'https://user:password@observatory.example' },
        @{ Url = 'https://observatory.example/?query=yes' },
        @{ Url = 'https://observatory.example/#fragment' },
        @{ Url = '/relative' }
    ) {
        $script:parameters.AppUrl = $Url
        { & $script:setupScript @script:parameters } | Should -Throw '*AppUrl must*'
        $script:state.Calls.Count | Should -Be 0
    }

    It 'refuses an existing display name instead of silently selecting or duplicating it' {
        $script:state.Matches = @($script:state.App)
        { & $script:setupScript @script:parameters } | Should -Throw '*ClientId*'
        ($script:state.Calls -join "`n") | Should -Not -Match 'ad app create|PATCH|assignment create'
    }

    It 'rejects incompatible tenant, cloud, account or partition before changing Entra' -ForEach @(
        @{ Invalid = 'tenant'; Message = '*same*tenant*' },
        @{ Invalid = 'cloud'; Message = '*public cloud*' },
        @{ Invalid = 'kind'; Message = '*NoSQL*' },
        @{ Invalid = 'partition'; Message = '*/trigger*' }
    ) {
        switch ($Invalid) {
            tenant { $script:state.Identity.tenantId = 'different-tenant'; $Message = '*must belong*tenant*' }
            cloud { $script:state.Cloud = 'AzureUSGovernment' }
            kind { $script:state.Cosmos.kind = 'MongoDB' }
            partition { $script:state.Partitions = @('/other') }
        }
        { & $script:setupScript @script:parameters } | Should -Throw $Message
        ($script:state.Calls -join "`n") | Should -Not -Match 'ad app create|PATCH|assignment create'
    }

    It 'rejects disabled or incompatible existing application roles' {
        $script:state.App.appRoles = @(@{ id = [guid]::NewGuid().ToString(); value = 'AgentOps.Operator'; isEnabled = $true; allowedMemberTypes = @('Application') })
        { & $script:setupScript @script:parameters -ClientId $script:client } | Should -Throw '*incompatible*'
        ($script:state.Calls -join "`n") | Should -Not -Match 'PATCH|assignment create'
    }

    It 'rejects a disabled delegated scope instead of replacing an existing grant ID' {
        $script:state.App.api.oauth2PermissionScopes = @(@{ id = [guid]::NewGuid().ToString(); value = 'Agent.Run'; isEnabled = $false; type = 'Admin' })
        { & $script:setupScript @script:parameters -ClientId $script:client } | Should -Throw '*scope*disabled*'
        ($script:state.Calls -join "`n") | Should -Not -Match 'PATCH|assignment create'
    }

    It 'rejects a multitenant existing application before modifying it' {
        $script:state.App.signInAudience = 'AzureADMultipleOrgs'
        { & $script:setupScript @script:parameters -ClientId $script:client } | Should -Throw '*single-tenant*'
        ($script:state.Calls -join "`n") | Should -Not -Match 'PATCH|assignment create'
    }

    It 'refuses a federation name collision rather than silently replacing its trust' {
        $script:state.Federations = @(@{
            name = "agentops-$script:principal"; issuer = "https://login.microsoftonline.com/$script:tenant/v2.0"
            subject = $script:miClient; audiences = @('api://AzureADTokenExchange')
        })
        { & $script:setupScript @script:parameters -ClientId $script:client } | Should -Throw '*different trust*'
        ($script:state.Calls -join "`n") | Should -Not -Match 'federated-credential create|assignment create'
    }

    It 'does not mistake another principal or container grant for the required Cosmos assignment' {
        $script:state.Assignments = @(
            @{ principalId = $script:client; scope = "$script:cosmosId/dbs/agentops/colls/executions"; roleDefinitionId = "$script:cosmosId/sqlRoleDefinitions/00000000-0000-0000-0000-000000000002" },
            @{ principalId = $script:principal; scope = "$script:cosmosId/dbs/agentops/colls/unrelated"; roleDefinitionId = "$script:cosmosId/sqlRoleDefinitions/00000000-0000-0000-0000-000000000002" }
        )
        & $script:setupScript @script:parameters | Out-Null
        $script:state.Calls | Where-Object { $_ -like 'cosmosdb sql role assignment create *' } | Should -HaveCount 1
        $script:state.Assignments[0].principalId | Should -Be $script:principal
        $script:state.Assignments[0].scope | Should -Be "$script:cosmosId/dbs/agentops/colls/executions"
    }

    It 'surfaces Azure CLI errors without a success summary and restores process encoding' -ForEach @(
        @{ Failure = 'identity show' },
        @{ Failure = 'ad app create' },
        @{ Failure = 'rest --method PATCH' },
        @{ Failure = 'federated-credential create' },
        @{ Failure = 'role assignment create' }
    ) {
        $script:state.Failure = $Failure
        $originalEncoding = $env:PYTHONIOENCODING
        $observed = [Collections.Generic.List[object]]::new()
        { & $script:setupScript @script:parameters | ForEach-Object { $observed.Add($_) } } | Should -Throw '*Azure CLI failed*'
        $observed.Count | Should -Be 0
        $env:PYTHONIOENCODING | Should -Be $originalEncoding
        foreach ($file in $script:state.Files) { Test-Path -LiteralPath $file | Should -BeFalse }
    }
}
