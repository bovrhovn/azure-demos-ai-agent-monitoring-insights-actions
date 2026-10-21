# Configuration reference

Application settings use one validated class per file in `Infrastructure/Options` and scheduling settings in `Features/Scheduling/SchedulingRegistration.cs`. `OptionsRegistration` binds and validates application options; `ApplicationServiceRegistration` registers application adapters and background services; `WebInfrastructureRegistration` registers Razor Pages, identity, SignalR, Problem Details, ingress handling, telemetry and health checks. `AddAgentOps` composes these registrations. ASP.NET Core configuration sources apply in their normal order: checked-in JSON defaults, Development JSON, Development user-secrets, environment variables, then command-line arguments. Use double underscores for nested environment variables.

No credentials belong in committed JSON, Dockerfiles, documentation examples with real values, or Squad state.

## Identity and telemetry

| Setting | Default / requirement |
|---|---|
| `AzureAd:Instance` | `https://login.microsoftonline.com/` |
| `AzureAd:TenantId` | Required tenant GUID |
| `AzureAd:ClientId` | Required application GUID |
| `AzureAd:ClientSecret` | Secret-based OIDC alternative from user-secrets / Key Vault; omit when using managed-identity federation |
| `AzureAd:ClientCredentials:0:SourceType` | `SignedAssertionFromManagedIdentity` for secretless production login |
| `AzureAd:ClientCredentials:0:ManagedIdentityClientId` | User-assigned identity client ID trusted by the login app's federated credential |
| `AzureAd:CallbackPath` | `/signin-oidc` |
| `AzureAd:SignedOutCallbackPath` | `/signout-callback-oidc` |
| `AzureAd:ReaderRole` | `AgentOps.Reader` |
| `AzureAd:OperatorRole` | `AgentOps.Operator` |
| `AzureAd:RunScope` | `Agent.Run` |
| `ApplicationInsights:ConnectionString` | Empty until configured; controls telemetry ingestion, not query access |
| `Insights:ResourceId` | Full monitored Application Insights ARM resource ID; empty displays a configuration notice |
| `Insights:LookbackMinutes` | `60`, range `1..10080` |
| `Insights:LogLimit` | `50`, range `1..200` |

Logs query `AppTraces`; overview queries `AppRequests` and `AppExceptions` on a workspace-based Application Insights resource. Queries use the server's `DefaultAzureCredential`, not the signed-in browser user's delegated identity.

## Persistence

| Setting | Default / requirement |
|---|---|
| `Cosmos:Database` | `agentops` |
| `Cosmos:Container` | `executions` |
| `Cosmos:ConnectionString` | Required **only in Development**, for the emulator |
| `Cosmos:Endpoint` | Required HTTPS Cosmos URL outside Development |
| `Cosmos:InitializeDatabase` | `false`; Development JSON sets `true`; rejected outside Development |
| `Cosmos:AllowInsecureEmulatorTls` | `false`; rejected outside Development |

Production never uses a Cosmos key/connection string. The Cosmos SDK uses `DefaultAzureCredential` and requires Cosmos **data-plane** RBAC. Azure control-plane Contributor alone is insufficient.

The footer identifies the configured execution data source: **Cosmos development**
with **Local emulator** in Development, or **Cosmos production** with the account
name derived from `Cosmos:Endpoint` in Production. Other environments retain their
actual environment label. Only database/container names and public account metadata
are rendered; connection strings and credentials are never sent to the browser.
The badge identifies the source, not a successful connectivity check; readiness
remains available through the health endpoints.

Deployments may reuse an existing Cosmos account with `CosmosAccountName`, `CosmosResourceGroup` and optional `CosmosSubscriptionId`. The deployment does not update shared account settings. Private connectivity is enabled by default for reused accounts, including Container Apps VNet integration, a Cosmos private endpoint and linked private DNS. See [deployment options](../azure/README.md#reuse-cosmos-without-changing-shared-account-settings).

`usePrivateKeyVault` defaults to `usePrivateCosmos`. Private Key Vault references use their own private endpoint and `privatelink.vaultcore.azure.net` DNS link, with dependency ordering before app creation. Either private-access setting enables the shared VNet infrastructure.

Execution history uses the singleton Cosmos client and `/trigger` partition key. Readiness checks this container even when dispatch is disabled. Hangfire uses process-local memory and requires no connection string or database.

## Event Grid webhook

`Webhook:ApiKey` is required and validated at startup. Configure it with user-secrets locally
or the Key Vault-backed `Webhook__ApiKey` secret reference in Container Apps.
The no-op `POST /api/webhooks/eventgrid` endpoint requires the `X-Api-Key` header;
Entra reader/operator sessions cannot substitute for this credential.
Event Grid subscription validation returns `validationResponse`, while normal notifications
return an empty `200 OK` without dispatching an execution. Query-string keys are not accepted.

## Execution and scheduling

| Setting | Default / requirement |
|---|---|
| `Scheduling:Enabled` | `true`; `false` removes the recurring definition at startup |
| `Scheduling:RecurringJobId` | `insights-agent`; stable ID registered once per startup |
| `Scheduling:Cron` | `*/15 * * * *`, interpreted in UTC |
| `Executions:HistoryLimit` | `50`, range `1..500` |
| `Executions:TimeoutSeconds` | `120`, range `1..3600` |
| `Executions:FinalizationTimeoutSeconds` | `15`, range `1..120`; independent of a canceled caller |
| `Executions:RequestsPerMinute` | `10`, range `1..100`, per authenticated API caller partition |
| `Analytics:PageSize` | `10`, range `1..200`; default rows per Vue analytics page |
| `Analytics:MaxPageSize` | `50`, range `1..200`; must be at least `PageSize` |

Restart to apply schedule changes. Five-field cron syntax is validated at startup and interpreted in UTC by Hangfire's minute-granularity recurring scheduler. One worker processes jobs. Queues and scheduling state are volatile and lost on restart; the configured recurring definition is re-registered, without recovery of missed work. Agent failures have no automatic retries; shutdown requeue behavior is distinct from retry. Registration failures fail startup. See [scheduling semantics](architecture.md#in-memory-scheduling-contract).

The handler timeout is cooperative: future agent/tool implementations must honor cancellation. Keep it below ingress/client timeouts for synchronous HTTP calls. Long runs should eventually move to an asynchronous accepted-job API.

## Foundry and hosting

| Setting | Default / requirement |
|---|---|
| `Foundry:ProjectEndpoint` | Empty until provided; HTTPS Foundry project endpoint |
| `Foundry:ModelDeployment` | Empty locally; Bicep supplies the selected deployment name (`gpt-5.5` by default) |
| `Foundry:AgentName` | `insights-agent` |
| `Hosting:BehindContainerAppsIngress` | `false`; Bicep sets `true` only for the ingress-protected app |
| `Hosting:ForwardLimit` | `1`, range `1..10`; maximum processed proxy hops |
| `Hosting:KnownProxies` | Empty; optional array of trusted proxy IP addresses |
| `Hosting:KnownNetworks` | Empty; optional array of trusted CIDR networks |
| `AllowedHosts` | `localhost`; Bicep supplies the Container App host |
| `ASPNETCORE_ENVIRONMENT` | The container defaults to `Production`; local launcher explicitly selects `Development` |
| `AZURE_CLIENT_ID` | In Azure, the user-assigned managed identity client ID, not the OIDC app registration client ID |

The factory validates Foundry endpoint/model inputs only when asked to create an agent. Startup and placeholder runs do not require a model and do not create or invoke an agent. The preview `Microsoft.Agents.AI.AzureAI` package is pinned; use `--prerelease` explicitly when evaluating upgrades.

`X-Forwarded-For`, `X-Forwarded-Proto` and `X-Forwarded-Host` are processed before HTTPS redirection, Entra authentication and SignalR origin validation. Forwarded hosts must match `AllowedHosts`; untrusted peers cannot change the external URL. Default local hosting retains ASP.NET Core's loopback trust.

`Hosting:BehindContainerAppsIngress` trusts the nearest proxy hop from any peer because Container Apps manages the ingress addresses. Do not enable it on a directly exposed Kestrel server. It assumes external traffic is constrained to the managed Container Apps ingress, which must sanitize forwarded headers. Health probes supply an allowed Host header.

`RouteConstants.cs` is the source of application-local routes, callback defaults and virtual asset URLs. Razor emits the SignalR/history URLs for JavaScript, avoiding separate browser route literals. `/health` and `/health/ready` run persistence readiness checks; `/health/live` checks the running process. All three are anonymous and exempt from application HTTPS redirection so internal HTTP probes execute the checks. Public Container Apps ingress still enforces HTTPS, and application pages/API retain authentication.

For other reverse proxies, leave that flag `false` and set `Hosting__KnownProxies__0=10.0.0.5` or `Hosting__KnownNetworks__0=10.0.0.0/24`. Explicit lists replace the framework defaults and take precedence over the Container Apps trust-all mode. Only raise `ForwardLimit` to match a verified trusted proxy chain. Invalid addresses/networks fail startup. These settings describe incoming reverse-proxy headers, not an outbound HTTP proxy.
