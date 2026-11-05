# Azure Deployment Plan

Status: Deployed
Generated: 2026-10-21

## 1. Project Overview

Deploy the current working-tree application to the existing agentops-web Container App.
Preserve existing runtime settings, identity, ingress, probes and scale.
The current approved update builds a new immutable image and rotates only the webhook
Key Vault secret, its application reference, and the matching Event Grid delivery header.

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

recipe.type: azcli

Use ACR Tasks and a digest-pinned image with az containerapp update --image.
Do not reprovision infrastructure or recreate Event Grid subscriptions. Rotate only the
explicitly approved webhook credential; preserve the confidential Entra credential.

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

### Production redeploy and webhook rotation (2026-11-04, current request)

- User confirmed MCAPS Azure Demos (466e1dc9-e0b4-4178-9da3-6a24d4df5a13),
  rg-aiagent/agentops-web, Sweden Central, for the current working tree.
- Approved rotation of webhook-api-key in the existing Key Vault, retaining the
  Webhook__ApiKey secret-backed environment variable, and synchronizing the matching
  Event Grid subscription delivery header. Never record the credential value.
- Preserve all other configuration, identities, network restrictions, permissions,
  ingress, probes and single-replica scaling. No infrastructure-wide deployment.
- All validation checks pass requires:
  - Core CLI/authentication, Bicep compilation, ARM validation and what-if of a
    version-matched live application snapshot and scoped secret/header updates.
  - Build the current Linux/amd64 image using the existing BuildKit ACR task,
    verify its digest, and run frontend and Release regression checks.
  - Verify Azure Policy through ARM provider validation and review existing
    managed-identity role declarations and live grants.
- 2026-11-04T13:38Z: frontend tests 10 passed; production browser bundle built.
  Release solution tests with browser verification: 219 passed, 4 opt-in/live
  prerequisites skipped, no failures.
- validate-deployment.ps1 for app-validation.bicep in session storage: OVERALL PASS,
  including CLI/authentication, compilation, ARM provider validation and what-if
  with Create 0, Modify 0, Delete 0.
- validate-deployment.ps1 for rotation-validation.bicep with a non-deployed secure
  placeholder: OVERALL PASS. Resource-level what-if targets only the existing
  webhook secret and matching Event Grid subscription; no deletions.
- ACR BuildKit task dt18 succeeded at 2026-11-04T13:38:17Z. Registry manifest
  independently matches sha256:e57f281443fcba05365cf4f8ce58b2ed0debda55c79db52193dbf96e2c79bd9d,
  tag deploy-20261104133413-9cf0c9a.
- Repository main.bicep compilation passed. Static ABAC Repository Reader,
  Key Vault Secrets User, Monitoring Reader, Foundry User and database-scoped
  Cosmos contributor declarations agree with existing live identity grants.
- No permissions, firewall settings, SKUs or resource counts need changes.
- Initial ACR submission rejected positional syntax before queuing any task;
  repeated --set arguments resolved it. All validation failures are resolved.

### Current update (2026-11-04)

- User confirmed MCAPS Azure Demos, existing rg-aiagent/agentops-web in Sweden Central.
- Deploy current on-disk source at commit 5ae814c; preserve the unrelated staged/index state.
- User approved the required agentops/chats container on bovrhovncdb in rg-containers,
  partitioned by /ownerId, using the repository indexing policy and serverless capacity.
- Keep all existing resources, secrets, identity, ingress, probes and scale unchanged;
  no infrastructure-wide deployment or permission changes.
- Validation steps: CLI/authentication, Bicep compilation, ARM provider validation and
  what-if for a live app configuration snapshot and the new chat container; container
  image build; frontend/Release regression suite; static and live role verification.
- scripts\test.ps1: frontend build passed; 182 tests passed, 10 opt-in tests skipped,
  zero failures. npm used the user-configured package feed.
- Existing identity has unconditional ACR Repository Reader, Key Vault Secrets User,
  Monitoring Reader, Foundry User, and database-scoped Cosmos Data Contributor.
- Foundry model deployment gpt-5.5 exists and is Succeeded.
- Invoke-Pester -Path .\azure\tests -CI: 31 passed, zero failures.
- validate-deployment.ps1 for app-validation.bicep in session storage: OVERALL PASS,
  including ARM provider/policy validation and what-if Create 0, Modify 0, Delete 0.
  Snapshot uses the same API version as the validation resource.
- validate-deployment.ps1 for chat-container.bicep in session storage: OVERALL PASS.
  Structured what-if confirms exactly one Create (agentops/chats), no modifications
  or deletions; all other existing resources are Ignore.
- BuildKit-enabled azure/acr-task.yaml via az acr run completed successfully (dt14)
  at 2026-11-04T09:57:19Z. Linux/amd64 image pushed and digest verified:
  bovrhovncr.azurecr.io/agent-observatory:deploy-20261104095049-5ae814c,
  sha256:e6c24ef7ac0d207435a84e45c991af5fde83b7590fb400b9827ae5c7f29ca5a0.
- Initial plain az acr build (dt13) failed because the Dockerfile requires BuildKit;
  the repository's existing task resolved this without changing source or Dockerfile.
- Static registry-access.bicep correctly selects Repository Reader for ABAC registries;
  cosmos-data.bicep uses database-scoped Data Contributor covering executions and chats.
- Validated by azure-validate workflow on 2026-11-04; all failures resolved.

### Current image-only update (2026-10-21)

- `scripts\test.ps1`: frontend build succeeded; Release suite 134 passed,
  5 opt-in tests skipped, zero failures.
- `validate-deployment.ps1 -Scope group -ResourceGroup rg-aiagent -Template
  <session-files>\app-validation.bicep`: OVERALL PASS; CLI, authentication,
  Bicep compilation, provider/policy validation and what-if all passed.
- Final configuration snapshot what-if: Create 0, Modify 0, Delete 0.
  Validation snapshot stays in session storage and is never applied.
- Static RBAC declarations and live identity roles agree: ABAC registry repository
  reader, Key Vault Secrets User, Monitoring Reader, Foundry User and
  database-scoped Cosmos data contributor. No role changes are needed.
- Existing app uses the same Sweden Central target and approved subscription.
- All existing environment variables and Key Vault references will be preserved;
  Analytics page-size settings have valid typed defaults and need no new variables.

All validation checks pass requires:
- Core CLI/authentication, Bicep compile, ARM validate and what-if.
- Validate an exact live configuration snapshot for this image-only update.
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

### UI redeploy and webhook rotation (2026-11-04, current request)

- ACR BuildKit task dt18 succeeded. Deployed the digest-pinned image
  bovrhovncr.azurecr.io/agent-observatory@sha256:e57f281443fcba05365cf4f8ce58b2ed0debda55c79db52193dbf96e2c79bd9d.
- Revision agentops-web--ui-20261104-134236 is Healthy, RunningAtMaxScale, one
  replica, and receives 100 percent traffic. It is the only active revision.
- URL: https://agentops-web.ambitiouspebble-72f10e6e.swedencentral.azurecontainerapps.io.
- Secure incremental ARM deployment agentops-webhook-key-20261104134112
  succeeded, rotating only webhook-api-key. No credential value was written to
  source files, session artifacts, deployment outputs, or this plan.
- Webhook__ApiKey remains exclusively secret-backed. The webhook-api-key
  Container Apps secret now pins Key Vault version ba396cbc070544359f7acf0c77a1729f
  through the unchanged managed identity, ensuring the new revision resolves it.
- Live webhook POST using the requested key returned HTTP 200; an invalid key
  returned HTTP 401. The verification notification invoked no agent actions.
- Event Grid agent-webhook is Succeeded with X-Api-Key as a secret static delivery
  header. The new revision logged the actual authenticated subscription-validation
  request at 2026-11-04T13:46:30Z. Secret values are masked on subsequent reads;
  literal read-back equality is therefore not a valid verification method.
- Initial Event Grid PATCH attempts returned InvalidRequest/empty payload without
  changing the subscription. A full UTF-8 create-or-update request preserving the
  validated configuration succeeded; no subscription deletion was performed.
- Final /health, /health/live and /health/ready all return HTTP 200.
- Live browser bundle exactly matches the tested local production build:
  144090 bytes, SHA-256 45C781E3772AF7A2F83A1F737442C9E5447471299FA3C17AE996F9B4C3ACCB53.
  Next-step, diagnostic and stack-frame markup/styles are present.
- API-version-matched before/after comparison confirms all other settings,
  environment variables, identity, network, registry, probes, scale and resources
  unchanged. Live runtime roles remain unchanged and sufficient.
- Frontend tests: 10 passed. Release solution with browser tests: 219 passed,
  4 emulator/live tests skipped because their prerequisites were not enabled.

### Latest deployment (2026-11-04)

- Current on-disk application at commit 5ae814c built with existing BuildKit ACR task.
- ACR run dt14 succeeded; tag deploy-20261104095049-5ae814c.
- Image: bovrhovncr.azurecr.io/agent-observatory@sha256:e6c24ef7ac0d207435a84e45c991af5fde83b7590fb400b9827ae5c7f29ca5a0.
- Approved incremental deployment agentops-chat-20261104 succeeded in rg-containers,
  creating only agentops/chats with /ownerId and the repository indexing policy.
- Revision agentops-web--0000003 is Healthy, RunningAtMaxScale, one replica,
  latestReadyRevisionName matches, and receives 100 percent traffic.
- URL: https://agentops-web.ambitiouspebble-72f10e6e.swedencentral.azurecontainerapps.io.
- /health, /health/live and /health/ready all return HTTP 200; readiness reads both
  real Cosmos containers. /js/app.js returns HTTP 200, 132449 bytes.
- Root, analytics executions and chat redirect to sign-in (302).
  Unauthenticated execution and Event Grid webhook POSTs correctly return 401.
- API-version-matched before/after comparison confirms configuration, identity,
  environment and template unchanged apart from image/generated revision suffix;
  empty value fields alongside secretRef are normalized for comparison.
- Live RBAC rechecked: unconditional ACR Repository Reader, Monitoring Reader,
  Key Vault Secrets User, Foundry User, and database-scoped Cosmos Data Contributor.
- Logs confirm Production startup, Hangfire In-Memory Storage, one worker and the
  configured recurring schedule. Scheduled execution persisted NotImplemented as
  intended; the execution agent remains the intentionally pending implementation.
- Existing Event Grid subscription agent-webhook remains Succeeded and targets
  /api/webhooks/eventgrid. Full event delivery verification is NOT claimed:
  current CLI principal lacks topic publish permission, topic local authentication
  is disabled, and Key Vault correctly blocks this machine outside its private link.
  No permissions, keys, firewall settings or authentication policies were changed.
- Interactive sign-in/chat and opt-in browser/emulator tests were not verified live.
  Local Release suite: 182 passed, 10 skipped; infrastructure suite: 31 passed.
- All pre-existing staged additions/missing legacy files remain untouched.

### Latest image-only deployment (2026-10-21)

- ACR run dt12 succeeded; tag deploy-20261021124405.
- Image: bovrhovncr.azurecr.io/agent-observatory@sha256:d61c4e46f32f49f2913f23d196bcde887963802f9d14f009c74fb98ae42d0382.
- Revision agentops-web--0000002 is Healthy, RunningAtMaxScale, one replica,
  with 100 percent traffic.
- URL: https://agentops-web.ambitiouspebble-72f10e6e.swedencentral.azurecontainerapps.io.
- Live /health, /health/live and /health/ready all return HTTP 200.
- All 17 existing runtime environment variables and both Key Vault secret
  references are semantically unchanged. Azure CLI added empty value fields
  beside existing secretRef fields; the secret references remain authoritative.
- Identity, registry configuration, ingress, probes, CPU/memory and scale
  are unchanged. No infrastructure or role assignments were redeployed.
- Root and analytics UI redirect to sign-in; the browser bundle returns 200.
  Unauthenticated execution API and webhook POST return 401.
- Startup logs confirm Production, Hangfire In-Memory Storage, one worker and
  the existing UTC recurring schedule. Event Grid remains Succeeded.

### Earlier infrastructure deployment

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
