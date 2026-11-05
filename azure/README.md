# Deploy to Azure Container Apps

The Bicep is a deployable **demo scaffold**, not a production hardening baseline. It deploys **GPT-5.5** by default and passes the selected deployment name to the application. Agent behavior remains a TODO: provisioning a model does not enable model calls or actions.

## Automated build and deployment

Like the reference chat-interface project, `scripts/Deploy-All.ps1` uses **ACR Tasks** to compile the container and push it in Azure; the image build needs no local Docker daemon. `acr-task.yaml` explicitly enables BuildKit for the Dockerfile's optional secret mount, builds a Linux/amd64 image and pushes the same image. Bicep describes resources, while PowerShell coordinates the build and app activation:

| Stage | What happens |
|---|---|
| 1 | Compile Bicep, select subscription/settings, display the plan and check the regional model catalogue |
| 2 | Create the resource group if missing; provision/reuse ACR, managed identity, Cosmos with execution-history container, Key Vault, Application Insights and Foundry/project/model |
| 3 | Build/push the Linux image through ACR Tasks and verify its SHA-256 digest |
| 4 | Deploy Container Apps from that **digest-pinned image**, with keyless resource access and ingress forwarding |
| 5 | Poll `/health/ready` until the Cosmos execution-history container is accessible, or fail with a timeout |
| 6 | Register the Event Grid subscription after readiness, require its authenticated validation handshake to succeed, then print the application URL |

Use PowerShell 7 and Azure CLI/Bicep. Your deployment identity needs resource creation, role assignment and ACR Tasks permissions; an ABAC registry additionally requires repository write/read permissions for the caller. Source is uploaded to the selected ACR build service.

```powershell
az login
$settings = @{
    ResourceGroup = '<resource-group>'
    SubscriptionId = '<subscription-guid>'
    Location = 'swedencentral'
    EntraClientId = '<existing-web-api-registration-guid>'
}

# Local plan only: no resource creation, source upload or model deployment.
.\azure\scripts\Deploy-All.ps1 @settings -WhatIf

# Prompts securely for the confidential web-client secret unless supplied as
# -EntraClientSecret (SecureString) or through AZURE_CLIENT_SECRET.
# Also prompts for -WebhookApiKey (SecureString), or reads AGENTOPS_WEBHOOK_API_KEY.
$deployment = .\azure\scripts\Deploy-All.ps1 @settings
```

Complete the [Entra registration setup](../docs/local-development.md#configure-microsoft-entra-id) before deployment. The app registration already needs roles and API scope; the script does not create an identity provider or assign human users.

To create a **dedicated** registration instead of configuring one manually:

```powershell
$registration = .\azure\scripts\Initialize-EntraApplication.ps1 `
    -DisplayName 'Agent Observatory (my-environment)' -CreateClientSecret
$settings.EntraClientId = $registration.ClientId
$deployment = .\azure\scripts\Deploy-All.ps1 @settings `
    -EntraClientSecret $registration.ClientSecret
.\azure\scripts\Initialize-EntraApplication.ps1 -ClientId $registration.ClientId `
    -ApplicationUrl $deployment.containerAppUrl.value | Out-Null
```

This configures single-tenant sign-in, reader/operator app roles, the delegated `Agent.Run` scope and v2 access tokens. It creates the enterprise application but does not grant tenant consent or assign human roles. Capture the result; never serialize credential-bearing objects. Later callback updates without `-CreateClientSecret` do not rotate credentials. An existing display name requires an explicit `-ClientId`, preventing accidental updates to another registration.

Omitting `-AcrName` creates a deterministic registry. Reuse a registry without modifying its settings:

```powershell
$deployment = .\azure\scripts\Deploy-All.ps1 @settings `
    -AcrName '<existing-registry>' -AcrResourceGroup '<registry-resource-group>' `
    -AcrSubscriptionId '<registry-subscription-guid>'
```

Only the managed-identity pull role is added to an existing registry. Classic registries use `AcrPull`; ABAC registries use **Container Registry Repository Reader**, and ACR Tasks uses `--source-acr-auth-id [caller]`. Cross-subscription reuse requires the same Entra tenant and permission to assign roles there. Do not change an existing registry's permission model as part of a routine application redeploy.

### Reuse Cosmos without changing shared account settings

```powershell
$deployment = .\azure\scripts\Deploy-All.ps1 @settings `
    -AcrName '<existing-registry>' -AcrResourceGroup '<registry-group>' `
    -CosmosAccountName '<existing-nosql-account>' -CosmosResourceGroup '<cosmos-group>'
```

The Cosmos data module creates the dedicated database, execution-history container (`/trigger`), private chat container (`/ownerId`), notification inbox (`/id`) and a database-scoped data-plane grant in the account's resource group. Configure names through `cosmosDatabaseName`, `cosmosContainerName`, `chatContainerName` and `notificationContainerName`. Hangfire scheduling uses process-local memory, not a Cosmos schedule container or a relational database. Existing account authentication and network settings remain unchanged. By default, reusing an account provisions a VNet, Container Apps infrastructure subnet, private endpoint and linked private DNS zone in the app resource group. The account must permit approving that private endpoint. Public Cosmos access is not enabled as a workaround.

Key Vault also defaults to private access when private Cosmos connectivity is selected. Its separate `privatelink.vaultcore.azure.net` zone, VNet link and private endpoint are created before Container Apps resolves the managed-identity secret reference. `usePrivateKeyVault` can be configured independently; selecting either private service creates the required VNet-integrated environment. Without private Key Vault connectivity, the new vault explicitly uses public access with RBAC.

Configure non-overlapping CIDRs through `vnetAddressPrefix`, `infrastructureSubnetPrefix` and `privateEndpointSubnetPrefix`. `usePrivateCosmos=false` is accepted only when the existing account has public access enabled. Existing-account reuse across subscriptions requires the same tenant and permissions in both scopes.

On redeployment, retain any policy-attached subnet NSGs by passing their resource IDs through
`containerAppsNetworkSecurityGroupId` and `privateEndpointNetworkSecurityGroupId` in
`TemplateParameters`. Do not detach policy-managed security groups as resource cleanup.

### Model and deployment settings

The defaults are model **`gpt-5.5`**, deployment name **`gpt-5.5`**, format `OpenAI`, SKU `GlobalStandard` and capacity `1`. An omitted version uses Azure's default version for that model. If a different model is supplied without a deployment name, its name becomes the deployment name.

```powershell
$deployment = .\azure\scripts\Deploy-All.ps1 @settings `
    -FoundryLocation '<supported-region>' `
    -ModelName '<model-name>' -ModelDeploymentName '<deployment-name>' `
    -TemplateParameters @{
        foundryModelVersion = '<supported-version>'
        foundryModelSku = 'GlobalStandard'
        foundryModelCapacity = 1
        foundryProjectName = 'observatory'
        scheduleCron = '*/15 * * * *'
    }
```

Use a supported model/version/SKU from the [Azure model catalogue](https://learn.microsoft.com/azure/foundry/foundry-models/concepts/models-sold-directly-by-azure) and [regional availability](https://learn.microsoft.com/azure/foundry/foundry-models/concepts/model-region-availability). The script rejects a model/version/SKU missing from the regional catalogue **before creating resources**. Azure still enforces access, subscription quota and current capacity at deployment; no different model is silently selected. Provisioning can incur charges and allocate quota even though the app's placeholder runner makes no model calls.

To omit model provisioning, pass `-TemplateParameters @{ deployFoundryModel = $false }`. This does not delete a previously deployed model in incremental mode.

### Redeployment and options

Redeploy with a new image tag:

```powershell
$deployment = .\azure\scripts\Deploy-All.ps1 @settings `
    -ImageTag 'v2'
```

Use `-SkipBuild -ImageTag '<existing-tag>'` to deploy an existing image; the script verifies its registry digest before activation. Keep registry and template overrides consistent on later runs: they are not discovered from a previous deployment.

| Script setting | Default / purpose |
|---|---|
| `ResourceGroup`, `EntraClientId` | Required |
| `SubscriptionId` | Current Azure CLI subscription if omitted; passed explicitly to each Azure operation |
| `Location` / `FoundryLocation` | `swedencentral` / the selected resource location |
| `Prefix` | `agentops`; stable lowercase naming prefix |
| `AcrName`, `AcrResourceGroup`, `AcrSubscriptionId` | Create new when name omitted; otherwise existing registry, defaulting to the app group/subscription |
| `CosmosAccountName`, `CosmosResourceGroup`, `CosmosSubscriptionId` | Create new when name omitted; otherwise reuse a NoSQL account without updating its settings |
| `ImageRepository` / `ImageTag` | `agent-observatory` / a UTC timestamp; explicit tag required with `SkipBuild` |
| `ModelName`, `ModelDeploymentName` | Bicep defaults; deployment name follows the selected model when empty |
| `EntraClientSecret` | SecureString; secure prompt or `AZURE_CLIENT_SECRET` if omitted |
| `ReuseEntraClientSecret` | Retain the existing Key Vault Entra secret without reading or rotating it; only for an existing deployment |
| `WebhookApiKey` | Required SecureString; secure prompt or `AGENTOPS_WEBHOOK_API_KEY` if omitted |
| `TemplateParameters` | Additional named Bicep settings, including `tags`, `observedInsightsResourceId`, Cosmos database/container names and model/schedule parameters |
| `ReadinessTimeoutSeconds` / `PollSeconds` | `300` / `5`; bounded readiness polling |
| `WhatIf` | Local plan only; **not** an ARM what-if or permission/quota validation |

Secrets are passed through a temporary process environment, never CLI literals or generated files. The generated `.bicepparam` contains only environment references and is removed in `finally`; previous environment values are restored on success or failure. Compiled secure parameter output is not written to disk or printed. Build, deployment and readiness failures stop the workflow rather than reporting success.

Azure CLI child output uses UTF-8 for build tools that emit Unicode, with the caller's previous encoding restored afterward. If a CLI log-streaming failure occurs, inspect the existing ACR run before submitting another build: a disconnected log reader does not necessarily mean the cloud build failed.

Foundry model/project writes are serialized to avoid account-level operation conflicts, and Key Vault names stay within Azure's 24-character limit for every supported prefix length.

For ARM what-if or a manual Docker build/push, use the individual steps below.

## Provisioned resources

- ACR with admin credentials disabled and managed-identity image pulls.
- Container Apps environment and an optional non-root web Container App.
- User-assigned app identity and scoped Azure role assignments.
- New keyless serverless Cosmos DB or an existing NoSQL account; dedicated database and `/trigger` execution-history container.
- VNet integration and separate private endpoints/DNS for private Cosmos and Key Vault connectivity.
- Key Vault with RBAC and a confidential Entra web-client credential.
- Workspace-based Application Insights and Container Apps diagnostics.
- Microsoft Foundry account/project with local API-key authentication disabled and a Foundry User grant.
- Configurable model deployment, defaulting to GPT-5.5 with the same deployment name.

The Container App uses `DefaultAzureCredential` for Cosmos, Foundry and Application Insights reads. The web client's confidential OIDC credential is not a database/model API key; it is referenced from Key Vault.

### Event Grid webhook

`POST /api/webhooks/eventgrid` accepts Event Grid schema JSON arrays with the configured
`X-Api-Key` header. `WebhookOptions.ApiKey` binds `Webhook:ApiKey`; in Azure,
`Webhook__ApiKey` references the `webhook-api-key` Container Apps secret backed by Key Vault.
Set the local option with .NET user-secrets before starting the app. Never put the key in
tracked settings, request URLs, deployment outputs or source code.

The topic uses Entra-authenticated publishing (`disableLocalAuth=true`). Its subscription
stores the matching header as a secret static delivery property. Subscription validation
returns the exact `validationResponse`; ordinary notifications return an empty `200 OK`
only after each event is saved in `Notifications:Container` (default `notifications`).
Each record contains the raw event JSON, `data.message` (or an empty string when absent),
UTC `ReceivedDate`, `IsHandled`, an optional completion comment and a nullable chat session ID.
Subscription validation events are not saved to the inbox.
Events with an `id` are deduplicated by topic and event ID; retries preserve the original
receipt date, handled state, comment and session association. Events without an ID are
stored as separate receipts. Persistence failures return an error so Event Grid can retry.
Payloads are bounded by `Webhook:MaxPayloadBytes` (default 262144 bytes).

The **Webhook inbox** menu opens newest-first, with message search, continuation-token
paging and All/Handled/Unhandled filters (All by default). Readers can review notifications;
operators can change status and open chats. Completing always opens a confirmation dialog
with an optional comment; marking unhandled preserves the previous note. Starting a chat
reserves a retryable association, creates a private session and prefills its composer without
sending the message or invoking the agent. Messages must fit `Chat:MaxPromptLength` to start
a chat; the full raw event remains available in the inbox. Session links respect chat ownership.
In Azure, redeploy the data module to provision the notification container before running
the updated application; readiness now checks all three containers.

The deployment script registers the subscription only after readiness and requires
`provisioningState=Succeeded`; it does not bypass Event Grid's connection check.
When rotating the webhook key, redeploy both the Container Apps secret reference and
Event Grid registration with the same value.

Scheduling uses Hangfire.InMemory; no PostgreSQL server or relational scheduling environment
variables are required. Incremental deployment does not delete an old server: inventory it
and explicitly approve deletion only after the replacement app is healthy.

## Prerequisites

For the manual image-build flow, you need Azure CLI/Bicep, Docker, a subscription/resource group, and permission to create resources and role assignments. Check regional availability and quota for Cosmos, Container Apps and Foundry. The automated ACR Tasks flow does not require Docker.

Complete the [Entra registration setup](../docs/local-development.md#configure-microsoft-entra-id). Supply settings through environment variables:

```powershell
$env:AZURE_CLIENT_ID = '<web-api-registration-client-guid>'
# Set AZURE_CLIENT_SECRET and AGENTOPS_WEBHOOK_API_KEY securely in this process,
# not a tracked parameter file.
$resourceGroup = '<resource-group>'
az login
```

`main.bicepparam` reads the confidential credential from the environment as a secure parameter. Do not supply it as a command-line literal, commit it, or print deployment parameter dumps.

## 1. Compile and preview

```powershell
az bicep build --file azure\main.bicep
az deployment group what-if --resource-group $resourceGroup --parameters azure\main.bicepparam
```

Review resource cost and changes before applying. The included parameter file defaults to `swedencentral`; change location/Foundry location if your subscription needs another region.

## 2. Provision infrastructure without the app

```powershell
az deployment group create --name agentops `
    --resource-group $resourceGroup `
    --parameters azure\main.bicepparam
```

`deployApp=false` is intentional: the image and Cosmos resources/grants must exist before app startup. GPT-5.5 is still provisioned in this infrastructure phase unless `deployFoundryModel=false`. Read non-secret deployment outputs:

```powershell
$outputs = az deployment group show --name agentops `
    --resource-group $resourceGroup --query properties.outputs -o json | ConvertFrom-Json
```

## 3. Build and push

```powershell
$registry = $outputs.registryName.value
$loginServer = az acr show --name $registry --query loginServer -o tsv
$tag = 'initial'
docker build -f containers\Dockerfile -t "${loginServer}/agent-observatory:$tag" .
az acr login --name $registry
docker push "${loginServer}/agent-observatory:$tag"
```

Use external package configuration if your machine requires proxies; see [testing](../docs/testing.md#package-proxies).

## 4. Enable the app

Copy the parameter file before enabling the app:

```powershell
Copy-Item azure\main.bicepparam azure\app.bicepparam
```

In `azure/app.bicepparam`, change `param deployApp = false` to `param deployApp = true` and add `param imageTag = 'initial'`, using the exact tag pushed above. Keep the environment-variable references for credentials. Then preview and deploy the second parameter file:

```powershell
az deployment group what-if --resource-group $resourceGroup `
    --parameters azure\app.bicepparam
az deployment group create --name agentops --resource-group $resourceGroup `
    --parameters azure\app.bicepparam
```

Add `https://<app-host>/signin-oidc` and the logout callback to the Entra web registration. Grant your operators/readers their application roles and complete a real sign-in.

By default the dashboard observes the newly created Application Insights resource. To observe an existing one, set `observedInsightsResourceId` and grant the app identity **Monitoring Reader** on that resource. The template's automatic reader grant covers only its own Application Insights resource.

Validate `/health/ready`, the authorized dashboard, scoped bearer calls, and the Cosmos execution-history container's data-plane access in the target tenant. Allow for Azure RBAC propagation when troubleshooting startup.

## Deployment boundaries

- `minReplicas=maxReplicas=1` and a single active revision are deliberate. Multi-replica operation requires persistent encrypted cookie keys and a SignalR backplane/Azure SignalR design.
- Cookie keys are ephemeral in the container; a restart can require users to sign in again.
- Before sensitive production use, configure private networking for Cosmos/Foundry. Establish capacity, backups, DR, telemetry retention, execution TTL and SLOs.
- Hangfire.InMemory queues and scheduling state are lost on restart; the UTC recurring definition is re-registered at startup. Agent failures have no automatic retries. Keep one replica and cancellation cooperative; see [scheduling semantics](../docs/architecture.md#in-memory-scheduling-contract). Previously provisioned custom schedule containers are no longer used; remove them only through an explicit, reviewed cleanup procedure.
- The optional existing Application Insights resource needs a separate role grant.
- GPT-5.5 is deployed by default; model/version/SKU/capacity/deployment name are parameters. Agent behavior is still deferred. Review regional capacity, quota and cost before deployment.
- Treat image upgrades, schedule changes, orphan-job cleanup, schema migrations and Key Vault credential rotation as explicit operational procedures.

## Offline validation

```powershell
az bicep build --file azure\main.bicep
Import-Module Pester -MinimumVersion 5.7.1
Invoke-Pester -Path .\azure\tests -Output Detailed -CI
```

The deployment suite uses actual Bicep compilation and mocked Azure/HTTP operations. It tests the Cosmos execution-history partition path, new/existing/ABAC registry handling, model overrides, phase ordering, digest verification, secret cleanup, failures and readiness. It does not deploy resources or validate tenant authorization.
