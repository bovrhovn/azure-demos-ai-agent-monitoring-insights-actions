# Azure Deployment Plan

Status: Deployed
Generated: 2026-10-21

## 1. Project Overview

Modify the existing .NET Container App to use the current in-memory scheduler,
retain durable Cosmos execution history, and expose a no-op authenticated Event Grid webhook.

## 2. Approved Target

- Subscription: MCAPS Azure Demos (466e1dc9-e0b4-4178-9da3-6a24d4df5a13).
- Resource group: rg-aiagent; location: swedencentral.
- User confirmed this target and implementation/deployment on 2026-10-21.
- User separately approved deletion of agentops-heto2js6mjyf2-pg after live readiness.
- Never write credential values into this record.

## 3. Components

- Razor Pages, Minimal APIs, SignalR, Hangfire.InMemory: src/AgentOps.Web.
- MSTest integration suite: src/tests/AgentOps.Tests.
- Bicep and PowerShell orchestration: azure/main.bicep, azure/scripts/Deploy-All.ps1.

## 4. Recipe Selection

recipe.type: bicep

Retain the existing standalone Bicep/ACR Tasks deployment workflow and digest-pinned images.

## 5. Architecture

Reuse agentops-web and its existing managed environment, identity, Key Vault,
private network, telemetry and Foundry resources.
Reuse bovrhovncr and bovrhovncdb in rg-containers without changing account settings.
Retain the agentops Cosmos database and executions container partitioned by /trigger.
Add one Event Grid custom topic and one webhook event subscription.
Store webhook-api-key in Key Vault; Container Apps resolves it with the existing identity
and exposes Webhook__ApiKey via a secret reference.
Event Grid sends X-Api-Key as a secret static delivery header, including during validation.
Register the subscription only after app readiness and require Succeeded validation.
Normal notifications return HTTP 200 without executing agents.
No PostgreSQL resources or Hangfire database environment variables are deployed.

## 6. Capacity and Safety

Existing resources retain their SKUs, locations and counts.
The app remains one replica with 0.5 CPU and 1 GiB RAM.
Only the Event Grid topic/subscription are net-new top-level resources.
Event Grid quota CLI returned BadRequest (unsupported); live inventory has zero topics in
Sweden Central. Official Event Grid limits allow 100 custom topics per subscription/region
and 500 subscriptions per topic, so totals of one topic and one subscription are within limits.
ARM provider validation and what-if passed against the confirmed subscription.
Inherited caller Owner plus existing ACR Tasks/repository roles cover deployment/build.
Use incremental deployment, never complete-mode deletion.
Preserve the existing confidential Entra credential without reading or rotating it.
Delete only the individually approved PostgreSQL server after the new version is healthy.

## 7. Validation Proof

All validation checks pass requires:
- Core CLI/authentication, Bicep compile, ARM validate and what-if.
- Managed project build and regression tests.
- Static/live role verification.
- Azure policy validation through ARM provider validation.

| Check | Command | Result | Date |
|---|---|---|---|
| Webhook integration tests | dotnet test tests\AgentOps.Tests\AgentOps.Tests.csproj --configuration Release --filter FullyQualifiedName~WebhookTests | 18 passed | 2026-10-21 |
| Managed regression suite | dotnet test AgentOps.slnx --configuration Release | 79 passed, 4 opt-in tests skipped | 2026-10-21 |
| Infrastructure/orchestration | Invoke-Pester -Path .\azure\tests -CI | 31 passed | 2026-10-21 |
| Azure core/policy validation | validate-deployment.ps1 -Scope group -ResourceGroup rg-aiagent -Template azure\main.bicep -Parameters .azure\webhook-validation.bicepparam | OVERALL PASS; compilation, authentication, provider validation and what-if | 2026-10-21 |
| Static/live role checks | az role assignment list; az cosmosdb sql role assignment list; inspect Bicep role declarations | ACR repository reader, Key Vault Secrets User, Monitoring Reader, Foundry User, database-scoped Cosmos Data Contributor retained | 2026-10-21 |
| Cosmos contract | az cosmosdb sql container show -g rg-containers -a bovrhovncdb -d agentops -n executions | Existing container, /trigger partition key | 2026-10-21 |

What-if reviewed at resource level: no resource deletions; old PostgreSQL is ignored by
incremental deployment until the separately approved cleanup.
Policy-created subnet NSG attachments are explicitly preserved through template parameters.
No validation errors remain.
Validated by: azure-validate skill, 2026-10-21.

## 8. Execution

- Implementation and local tests complete.
- Validate Azure changes and permissions.
- Provision webhook secret/topic, build image, deploy app and verify readiness.
- Register Event Grid and verify authenticated handshake and delivery.
- Remove the approved PostgreSQL server and verify final resource inventory.

## 9. Live Deployment Evidence

- ACR run dt11 succeeded. The Windows CLI log reader failed on Unicode after the build
  succeeded; deployment resumed with SkipBuild after verifying the existing image digest.
- Image: bovrhovncr.azurecr.io/agent-observatory@sha256:479f766d3294fa111457fb3bc79a1101fc71d92b117738497265bfae9cb19c89.
- Ready revision: agentops-web--0000001, Healthy, RunningAtMaxScale, one replica.
- URL: https://agentops-web.ambitiouspebble-72f10e6e.swedencentral.azurecontainerapps.io.
- Live /health/ready returned 200, including real Cosmos access.
- Webhook without a key returned 401; authenticated notification returned empty 200;
  validation returned the exact supplied validationResponse.
- Webhook__ApiKey references webhook-api-key, which references the existing private Key Vault
  through the app's user-assigned identity. No literal key is in the environment value.
- Event Grid agent-webhook on agentops-heto2js6mjyf2-events reached Succeeded.
  Live registration confirms X-Api-Key is a Static delivery property with isSecret=true.
  App logs independently confirm the actual Event Grid subscription validation completed.
- No Hangfire__Host, Hangfire__Database or Hangfire__Username environment variables remain.
- App logs confirm Hangfire In-Memory Storage and successful recurring schedule registration.
- Approved PostgreSQL server deletion completed. Final rg-aiagent inventory contains no
  PostgreSQL resources; post-deletion /health/ready remains HTTP 200.
- Final public checks: / returns 302 to sign-in, /health/live and /health/ready return 200,
  and unauthenticated /api/executions returns 401.
- Final live roles retain registry pull, Key Vault secret read, monitoring read, Foundry access
  and database-scoped Cosmos data access. Event Grid registration remains Succeeded.
