# Azure Deployment Plan

Status: Validated
Generated: 2026-10-21

## 1. Project Overview

Deploy the current working-tree application with only the Endpoint runs page and
navigation removed to the existing agentops-web Container App. Preserve the execution
API, scheduled investigations, shared execution feed, hub and authoritative Cosmos history.
Preserve existing runtime settings, secrets, identity, ingress, probes and scale.
The current approved update builds a new immutable image and updates only the application
revision. The notifications container already exists. Do not provision resources, rotate
credentials, modify Event Grid, or invoke production diagnostic/test actions.

## 2. Approved Target

- Subscription: MCAPS Azure Demos (466e1dc9-e0b4-4178-9da3-6a24d4df5a13).
- Resource group: rg-aiagent; location: swedencentral.
- User confirmed this target and implementation/deployment on 2026-10-21.
- Current image-only rollout explicitly authorized on 2026-11-05; no resource
  deletions, credential rotations or production test-data generation are authorized.
- Endpoint runs UI removal and production image-only redeployment explicitly approved
  on 2026-11-06, including the existing subscription, Sweden Central app and registry.
- Never write credential values into this record.

## 3. Components

- Razor Pages, Minimal APIs, SignalR, Hangfire.InMemory: src/AgentOps.Web.
- MSTest integration suite: src/tests/AgentOps.Tests.
- Bicep and PowerShell orchestration: azure/main.bicep, azure/scripts/Deploy-All.ps1.

## 4. Recipe Selection

recipe.type: azcli

Use ACR Tasks and a digest-pinned image with az containerapp update --image.
Do not reprovision infrastructure or recreate Event Grid subscriptions. Preserve every
credential. No infrastructure deployment is authorized for this update.

## 5. Architecture

Reuse agentops-web and its existing managed environment, identity, Key Vault,
private network, telemetry and Foundry resources.
Reuse bovrhovncr and bovrhovncdb in rg-containers without changing account settings.
Retain the agentops Cosmos database and executions container partitioned by /trigger.
Preserve the existing Event Grid custom topic and webhook event subscription.
Keep webhook-api-key in Key Vault; Container Apps resolves it with the existing identity
and exposes Webhook__ApiKey via a secret reference.
Event Grid sends X-Api-Key as a secret static delivery header, including during validation.
Preserve the existing Succeeded subscription without re-registration.
Normal notifications return HTTP 200 without executing agents.
No PostgreSQL resources or Hangfire database environment variables are deployed.

## 6. Capacity and Safety

Existing resources retain their SKUs, locations and counts.
The app remains one replica with 0.5 CPU and 1 GiB RAM.
No new resources or capacity are required for this image-only update.
ARM provider validation and what-if passed against the confirmed subscription.
Inherited caller Owner plus existing ACR Tasks/repository roles cover deployment/build.
Use only the Container Apps image/revision update; do not apply infrastructure templates.
Preserve the existing confidential Entra credential without reading or rotating it.
Historical PostgreSQL cleanup is complete; no deletion is part of this update.

## 7. Validation Proof

### Endpoint runs UI removal image-only update (2026-11-06)

- All validation checks pass requires:
  - Core Validation: fresh CLI/authentication, API-version-matched live snapshot
    compilation, ARM provider validation and what-if with zero changes.
  - Docker Build: existing ACR BuildKit task, current disk context, unique tag,
    ABAC caller authentication and independently verified digest.
  - Azure Policy Validation: scoped policy review and ARM provider validation.
  - Frontend tests/build, relevant managed Release tests including authenticated
    removed-route checks, API authorization/lifecycle and scheduled browser feeds.
  - Static role declaration verification and live role checks, safe before/after
    configuration comparison, health and served-asset proof.
- Fresh validation on 2026-11-06; historical release proof below is not reused.
- Regression gate was demonstrated before removal: both authenticated old-route
  absence tests failed. After removal, focused UI/API/logging/browser suite passed:
  62 passed, zero skipped/failed. Full Release suite with browser checks enabled:
  274 passed, 5 explicitly opt-in external tests skipped, zero failures (279 total).
  Skips: four Cosmos emulator tests and one live Azure telemetry/model test.
- `scripts/test.ps1` completed clean frontend install, 15 frontend tests and Vite
  build using the approved npm proxy via process environment. Its initial managed
  compile exposed an overview link to the removed route; that link and an obsolete
  history-page test were corrected before the successful Release suites.
- Touched C# files pass `dotnet format --verify-no-changes --no-restore`; diff
  whitespace validation passes. No infrastructure templates were changed.
- Core helper `validate-deployment.ps1` passed CLI/authentication, API-2025-01-01
  snapshot compilation, ARM provider validation and what-if: Create 0, Modify 0,
  Delete 0. Snapshot contents remain only in process memory, passed through a
  Bicepparam environment expression; artifacts contain no production config values.
  An initial secure-object what-if masked values as unknown modifications; the
  resolvable in-memory object removed these false differences. No template applied.
- Scoped CLI policy inspection returned no assignments; provider/policy validation
  passed. Azure MCP policy inspection used another identity and returned 403; the
  approved authenticated CLI succeeded without changing permissions.
- Static roles verified in `azure/main.bicep`, `azure/modules/registry-access.bicep`
  and `azure/modules/cosmos-data.bicep`: registry ABAC Repository Reader, vault
  Secrets User, Insights Monitoring Reader, project Foundry User, database Cosmos
  Data Contributor. Existing live roles and managed environment readiness confirmed.
- Safe rollback baseline: `agentops-web--inbox-20261106-1105`, image
  `bovrhovncr.azurecr.io/agent-observatory@sha256:cb07a46d0b1733914ebeae0f19750a8e06a4f1f75c810dfec58c52e1d5831973`.
  One replica, Single mode and 24 group resource IDs captured; normalized config
  hashes exclude only image/revisionSuffix. No secret values were fetched.
- ACR BuildKit task `dt1g` submitted from 112 current disk inputs, with private,
  secret, git/Squad, dependency and generated-output paths excluded. The existing
  task recipe uses a public npm registry-only BuildKit secret (no credentials),
  without altering tracked Docker/config files. ABAC flag verified at
  `azure/scripts/Deploy-All.ps1:197`: `--source-acr-auth-id '[caller]'`.
- ACR run `dt1g` Succeeded at 2026-11-06T13:11:09Z. Unique tag
  `no-endpoint-ui-20261106131005`; task output and independent
  `az acr repository show --image` agree on immutable digest:
  `sha256:ba8de6e91d96d1e31c12872dad6f51428aefab14a71ec2d80458b1df814d2d21`.
  All 112 build-input SHA-256 values match current disk source. CLI streaming logs
  hit a Windows character-encoding error; management-plane task success and
  independently resolved repository digest provide successful build proof.
- Fresh preparation/validation checks pass with no unresolved deployment failures.
  Production action APIs, chat, diagnostic events and model calls were not invoked.

### Scheduled error investigations image-only update (2026-11-05)

- Explicit approval: build the current disk tree, including new untracked source files,
  and update only the existing production application's image/revision.
- All validation checks pass requires:
  - Core Validation: CLI/authentication, API-version-matched live snapshot Bicep
    compilation, ARM provider validation and what-if with zero changes.
  - Docker Build: existing ACR BuildKit task, current disk context, unique tag,
    ABAC caller authentication and independently verified digest.
  - Azure Policy Validation: scoped policy review and ARM provider validation.
  - Fresh Release suite with browser checks, frontend tests/build, changed-source
    formatting verification and existing infrastructure tests.
  - Static role declarations and production prerequisite inspection; scoped live
    role verification again after deployment.
- Fresh validation completed on 2026-11-05; previous release proof was not reused.
- Managed Release suite with AGENTOPS_BROWSER_TESTS=true and --no-restore:
  271 passed, 5 explicitly opt-in skipped (4 Cosmos emulator, 1 live Foundry/Insights),
  zero failures. Frontend: 13 passed; Vite production build succeeded.
- Changed existing/new C# files passed dotnet format --verify-no-changes --no-restore;
  intentionally deleted legacy paths were excluded. Release build: zero warnings/errors.
  Pester: 31 passed, zero skipped/failed. Main Bicep compilation and diff check passed.
- Core validation helper: OVERALL PASS, what-if Create 0, Modify 0, Delete 0,
  using an API-2025-01-01 raw snapshot. Initial CLI-extension-shaped snapshot failed
  schema validation; replacing it with the API-matched GET resolved the error.
- Scoped Azure CLI policy list returned no assignments; provider/policy validation passed.
  Azure MCP used a different identity and received 403; the authenticated CLI succeeded
  without any permission changes.
- Current disk build context includes ErrorInvestigationRunner.cs, LocalWebhookDelivery.cs
  and NotificationTestService.cs; package lock exists. Secret filename inspection passed.
  Existing .dockerignore excludes secret sources, dependency/output and repository state.
- ACR BuildKit run dt1c succeeded at 2026-11-05T12:17:22Z with ABAC caller authentication.
  Unique tag scheduled-20261105121614, independently verified digest:
  sha256:e0be7bfbc54bd563fc2b4c23fb153a01aa6f266a25e85fc2a321cc09111170ff.
- Static roles verified: registry ABAC Repository Reader, vault Secrets User,
  Insights Monitoring Reader, project Foundry User, database Cosmos Data Contributor.
- Production prerequisites confirmed: Foundry project observatory Succeeded;
  configured gpt-5-mini-544625 deployment Succeeded; configured Insights resource Succeeded.
  InsightsTools enabled, MaxToolCalls defaults to 6, bounded 60-minute lookback;
  executions/chats/notifications containers present with expected partition keys.
  Existing identity grants match the above scopes. Concrete AllowedHosts, ingress
  forwarding enabled and runtime image listener port 8080 support loopback delivery.
- No unresolved validation failures or required out-of-scope configuration changes.
- Baseline confirmed: agentops-web--inbox-20261105-111724, image
  sha256:be0b670c9ee435065c0c0cbaebd065f022fac582e6ec5a8f8d6e32a77dd3f050.
- Preserve enabled UTC schedule */15 * * * *, all credentials, roles, identity,
  networking, probes, scale, Event Grid subscriptions and existing resource counts.
- No synthetic production events, automated chat sends, handling changes or fixes.

#### Scheduled-investigation production rollout verification

- azure-deploy invoked only after fresh azure-validate completed with Validated status.
  Existing subscription, Sweden Central group/environment, propagated ABAC registry
  reader and unchanged baseline confirmed before the image-only update.
  No AZD/provisioning step applies to this existing pure-CLI image-only rollout.
- Image-only update started 2026-11-05T12:20:51Z; revision
  agentops-web--investigate-20261105-122051 became Healthy with one ready replica,
  zero restarts and 100% traffic. It is the only active revision.
- Image: bovrhovncr.azurecr.io/agent-observatory@sha256:e0be7bfbc54bd563fc2b4c23fb153a01aa6f266a25e85fc2a321cc09111170ff.
- Endpoint: https://agentops-web.ambitiouspebble-72f10e6e.swedencentral.azurecontainerapps.io
- All three health endpoints returned HTTP 200; served JavaScript and CSS exactly
  match current local build hashes. Webhook without credentials returned 401.
  Schedules, Notifications and both diagnostic actions require sign-in (302).
- API-version-matched before/after comparison confirms unchanged identity, tags,
  configuration, managed environment, workload profile and runtime template except
  image/revisionSuffix. Secret references normalize absent versus empty value.
  Credentials were neither retrieved nor changed.
- Postdeployment scoped live role verification passed for principal
  153fe6f0-ee4a-4b1d-b159-906518a8a245: registry Repository Reader, vault Secrets User,
  Insights Monitoring Reader, project Foundry User, database Cosmos Data Contributor.
  Event Grid subscription agent-webhook remains Succeeded and was not modified.
- Console metadata confirms actual HTTP listener [::]:8080 and registration of
  insights-agent using the unchanged enabled UTC cron */15 * * * *.
- Rollback retained: agentops-web--inbox-20261105-111724 (inactive, Healthy), image
  bovrhovncr.azurecr.io/agent-observatory@sha256:be0b670c9ee435065c0c0cbaebd065f022fac582e6ec5a8f8d6e32a77dd3f050.
- Natural scheduled execution observed at 2026-11-05T12:30:11Z; real exception and
  severity-filtered trace queries both returned zero rows without truncation.
  Execution finished Completed at 12:30:14Z, after terminal Cosmos persistence.
  This verifies the active real runner rather than the former NotImplemented path.
- The empty-evidence branch correctly invoked neither Foundry nor the webhook.
  Production model inference and proposal delivery are therefore not end-to-end
  verified; management-plane model/project readiness and roles are verified, and the
  actual authenticated loopback delivery contract passed local production-mode tests.
  Authenticated production UI is also not verified. No synthetic errors or events
  were generated to force those paths.
- Distinct scheduled-* evidence files are persisted in the existing session files
  directory; application source, index, branches and Squad state were not modified.

### Operator inbox diagnostic controls image-only update (2026-11-05)

- Explicit user request: deploy the current on-disk application to existing production
  rg-aiagent/agentops-web in Sweden Central, MCAPS Azure Demos.
- Scope: image/revision update only; preserve secrets, environment, managed identity,
  ingress, networking, probes, roles, Event Grid and one-replica scaling.
- All validation checks pass requires:
  - Core validation: CLI/authentication, compile an API-version-matched live app
    snapshot, ARM provider validation and what-if with no resource changes.
  - Docker build: existing BuildKit ACR task against the current on-disk source,
    unique tag, ABAC caller authentication and independently verified digest.
  - Azure Policy validation through scoped ARM provider validation.
  - Full managed Release suite with browser tests enabled; frontend tests/build.
  - Static role declaration verification and post-deployment scoped live verification.
- Fresh azure-validate workflow restarted using its supported invocation without
  CompletedStep; old success is not reused.
- Full Release regression suite with AGENTOPS_BROWSER_TESTS=true and --no-restore:
  254 passed, 5 explicitly opt-in Cosmos/live tests skipped, zero failures.
- Fresh frontend tests: 13 passed; Vite production build succeeded. Release solution
  build --no-restore succeeded with zero warnings/errors. Pester: 31 passed.
- Core validate-deployment.ps1 on diagnostics-app-validation.bicep passed all checks:
  CLI/authentication, Bicep compilation, ARM provider/policy validation and what-if
  Create 0, Modify 0, Delete 0. Snapshot is validation-only and never deployed.
- Repository main.bicep compilation and git diff --check passed.
- Build context inspection found no .env variants, private-key/certificate files,
  .npmrc or NuGet.Config outside excluded dependency/output directories. Existing
  .dockerignore excludes Git, Azure state, Squad, build outputs and secret formats.
  The current untracked NotificationTestService.cs was present in the disk context.
- Existing BuildKit ACR run dt1b succeeded on 2026-11-05T11:12:51Z using caller
  authentication for ABAC. Unique tag diagnostics-20261105111144-a8e25a4;
  registry independently confirms digest
  sha256:be0b670c9ee435065c0c0cbaebd065f022fac582e6ec5a8f8d6e32a77dd3f050.
- Static roles verified unchanged: ABAC Repository Reader on the registry,
  Key Vault Secrets User on the vault, Monitoring Reader on Insights, Foundry User
  on the project and Cosmos Data Contributor scoped to the agentops database.
- No validation failures remain; no source, credential or infrastructure changes
  were necessary.
- Rollback reference verified live before validation:
  agentops-web--ui-20261105-092342,
  bovrhovncr.azurecr.io/agent-observatory@sha256:11cdaedb04b71c2a91141b4f54367ff169ece1a22588d923c207129138599350.

### Notification inbox and mixed telemetry update (2026-11-05)

- User confirmed MCAPS Azure Demos (466e1dc9-e0b4-4178-9da3-6a24d4df5a13),
  rg-aiagent/agentops-web, Sweden Central, for the current on-disk application.
- Approved provisioning only the missing agentops/notifications container on bovrhovncdb
  in rg-containers. Preserve all existing data, resources, secrets, configuration,
  permissions and single-replica scaling. Preserve unrelated staged/index changes.
- All validation checks pass:
  - Core validation: CLI/authentication, Bicep compilation, ARM provider validation and
    what-if for an API-version-matched application snapshot and the isolated container.
  - Docker build: existing BuildKit ACR task, immutable tag and independently verified digest.
  - Azure Policy validation: ARM provider validation of both scoped templates.
  - Frontend and Release regression checks, including browser verification.
  - Static role verification and live managed-identity role confirmation.
- 2026-11-05T09:20Z validation proof:
  - `npm test --prefix src\AgentOps.Web`: 13 passed; production Vite build succeeded.
  - `dotnet test AgentOps.slnx --configuration Release` from src, with browser tests:
    247 passed, 5 opt-in Cosmos/live tests skipped, zero failures.
  - Initial regression found a request sample using `myapiKey`; the test resolver now
    supports that alias and rejects unresolved variables. All 30 webhook tests passed
    independently and the complete Release suite subsequently passed.
  - `Invoke-Pester -Path .\azure\tests -CI`: 31 passed, zero failures.
  - Repository main.bicep compilation and `git diff --check` passed.
  - `validate-deployment.ps1` for the API-version-matched application snapshot:
    OVERALL PASS, Create 0, Modify 0, Delete 0. Snapshot is validation-only.
  - `validate-deployment.ps1` for the isolated notifications template: OVERALL PASS.
    Resource-level JSON what-if confirms exactly one Create:
    bovrhovncdb/agentops/notifications. No modifications or deletions.
  - Both scoped templates passed ARM provider/policy validation.
  - ACR BuildKit task dt1a succeeded; image tag deploy-20261105091820-a8e25a4.
    Manifest independently verified as
    sha256:11cdaedb04b71c2a91141b4f54367ff169ece1a22588d923c207129138599350.
  - Earlier dt19 compiled successfully but push lacked ABAC caller authentication.
    Retrying with the repository's `--source-acr-auth-id [caller]` resolved it without
    changing roles. Windows log-output encoding was bypassed using downloaded logs.
  - Static role declarations verified: database-scoped Cosmos Data Contributor,
    ABAC Container Registry Repository Reader, Key Vault Secrets User, Monitoring
    Reader and project-scoped Foundry User. Existing live grants match.
  - No secrets, permissions, infrastructure settings, SKUs or resource counts need
    changes beyond the explicitly approved new notifications container.

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

- Fresh azure-validate workflow completed with current proof and status Validated.
- azure-deploy invoked; azcli predeploy checklist completed for the explicitly confirmed
  existing subscription, resource group, location and managed environment.
- AZD initialization/provisioning and new-role propagation are not applicable:
  no infrastructure or role changes; existing ABAC Repository Reader verified before rollout.
- Existing BuildKit ACR task built current on-disk source; digest independently verified.
- Applied only az containerapp update --image with a unique revision suffix.
- Verified sole active healthy revision, readiness, security boundaries, assets and
  preservation of non-image settings and runtime roles. No live test signal was sent.

## 9. Live Deployment Evidence

### Operator inbox diagnostic controls image-only update (2026-11-05)

- Final live verification at 2026-11-05T11:21Z against the explicitly approved existing target.
- URL: https://agentops-web.ambitiouspebble-72f10e6e.swedencentral.azurecontainerapps.io.
- ACR BuildKit run dt1b succeeded; tag diagnostics-20261105111144-a8e25a4.
- Digest-pinned image:
  bovrhovncr.azurecr.io/agent-observatory@sha256:be0b670c9ee435065c0c0cbaebd065f022fac582e6ec5a8f8d6e32a77dd3f050.
- Revision agentops-web--inbox-20261105-111724 is the sole active revision,
  Healthy, Provisioned, RunningAtMaxScale, one ready replica, zero container restarts,
  100 percent traffic. latestReadyRevisionName matches; application state Succeeded.
- /health, /health/live and /health/ready all return HTTP 200 after rollout.
- Public JS and CSS exactly match the locally built/tested assets:
  - /js/app.js SHA-256 DDD102E9E5E57D30EB0A9BF0C3C9AA39848454FED82FE4C863A5C70CB5A37BA0.
  - /css/site.css SHA-256 B1B70CCF288699F78606D2B594FFEA0B034ED48ED05D8C76B4E0509B64EB24FF.
  - Verified Generate telemetry, Send test webhook and Send a signal. Follow the
    evidence. labels plus notification-test-tools styles in the served assets.
- Unauthenticated webhook POST returns 401. Both operator diagnostic POST routes
  redirect 302 to Microsoft sign-in; redirects were not followed. No authenticated
  diagnostic action, synthetic notification, webhook delivery or telemetry test was sent.
- API-version-matched before/after comparison confirms all non-image configuration
  unchanged, including environment variables/secret references, identity, registry,
  ingress, probes, CPU/memory, scale, tags and managed environment. No normalization
  of settings was needed; only image and revisionSuffix were excluded.
- Existing ARM role assignments are unchanged and verified at individual resource
  scopes: Container Registry Repository Reader, Monitoring Reader, Key Vault Secrets
  User and Foundry User. Cosmos Data Contributor remains scoped to /dbs/agentops;
  notifications container exists with /id partition key.
- Event Grid topic/subscription configuration checked before/after is unchanged;
  subscription remains Succeeded. No Event Grid or credential changes were made.
- Validation: 254 managed tests passed, 5 opt-in Cosmos/live tests skipped;
  13 frontend tests and 31 Pester tests passed. Release build: zero warnings/errors.
- Authenticated production UI and diagnostic delivery were not exercised without a
  user session or separate permission to emit test signals. Local browser coverage
  passed and deployed asset bytes were independently matched.
- Rollback reference retained:
  agentops-web--ui-20261105-092342,
  bovrhovncr.azurecr.io/agent-observatory@sha256:11cdaedb04b71c2a91141b4f54367ff169ece1a22588d923c207129138599350.
- Fresh workflow state: .azure/validate-status.json completedStep UpdateStatus.
  Distinct diagnostics-* evidence snapshots are in the existing session files directory.
  No application source or Git index changes were made by the deployment.

### Notification inbox and mixed telemetry update (2026-11-05)

- Completed at 2026-11-05T09:27Z against the user-confirmed production target.
- Incremental deployment agentops-notifications-20261105 succeeded, creating only
  bovrhovncdb/agentops/notifications. Verified /id partition key and repository indexing
  policy; no existing containers, data, throughput or account settings were changed.
- ACR BuildKit task dt1a succeeded. Deployed the digest-pinned image
  bovrhovncr.azurecr.io/agent-observatory@sha256:11cdaedb04b71c2a91141b4f54367ff169ece1a22588d923c207129138599350.
- Revision agentops-web--ui-20261105-092342 is Healthy, RunningAtMaxScale, one
  replica, and receives 100 percent traffic. It is the only active revision.
- URL: https://agentops-web.ambitiouspebble-72f10e6e.swedencentral.azurecontainerapps.io.
- /health, /health/live and /health/ready all return HTTP 200 Healthy.
- Live browser bundle exactly matches the tested local production bundle:
  SHA-256 83F6C0719A13373768A8A1CEDBCA2ECD2CEA1D31D18BDB112C416DB65F1CB767.
  New query-window and trace-timeline styles are present in the deployed stylesheet.
- Unauthenticated webhook POST returns HTTP 401. No production test notification
  was inserted and no webhook or Entra credential was read, changed or rotated.
- API-version-matched snapshots confirm image/revision are the only meaningful app
  changes. CLI serialization added an empty value beside an unchanged webhook secret
  reference; comparison normalized that missing-versus-empty representation.
  All secret references, environment settings, identity, network, registry, probes,
  scale, tags and managed environment remain unchanged.
- Post-deployment live grants remain correct: Container Registry Repository Reader,
  Key Vault Secrets User, Monitoring Reader, Foundry User, and database-scoped
  Cosmos Data Contributor covering executions, chats and notifications.
- Validation: Release solution including browser tests 247 passed, 5 opt-in/live
  tests skipped; frontend 13 passed; infrastructure/orchestration 31 passed.
- User-authenticated production inbox/chat interaction was not exercised; browser
  behavior was verified against the test host and the deployed bundle was matched.

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
