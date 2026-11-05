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

Logs query `AppTraces`; overview queries `AppRequests` and `AppExceptions` on a workspace-based Application Insights resource. Azure SDK clients share the server's credential, not the signed-in browser user's delegated identity: `DefaultAzureCredential` outside Production, and `ManagedIdentityCredential` in Production.

## Persistence

| Setting | Default / requirement |
|---|---|
| `Cosmos:Database` | `agentops` |
| `Cosmos:Container` | `executions` |
| `Cosmos:ConnectionString` | Required **only in Development**, for the emulator |
| `Cosmos:Endpoint` | Required HTTPS Cosmos URL outside Development |
| `Cosmos:InitializeDatabase` | `false`; Development JSON sets `true`; rejected outside Development |
| `Cosmos:AllowInsecureEmulatorTls` | `false`; rejected outside Development |

Production never uses a Cosmos key/connection string. The Cosmos SDK uses the shared `ManagedIdentityCredential` and requires Cosmos **data-plane** RBAC. Azure control-plane Contributor alone is insufficient.

The header beside the signed-in user identifies the configured execution data source: **Cosmos development**
with **Local emulator** in Development, or **Cosmos production** with the account
name derived from `Cosmos:Endpoint` in Production. Other environments retain their
actual environment label. Only database/container names and public account metadata
are rendered; connection strings and credentials are never sent to the browser.
The badge identifies the source, not a successful connectivity check; readiness
remains available through the health endpoints.

Deployments may reuse an existing Cosmos account with `CosmosAccountName`, `CosmosResourceGroup` and optional `CosmosSubscriptionId`. The deployment does not update shared account settings. Private connectivity is enabled by default for reused accounts, including Container Apps VNet integration, a Cosmos private endpoint and linked private DNS. See [deployment options](../azure/README.md#reuse-cosmos-without-changing-shared-account-settings).

`usePrivateKeyVault` defaults to `usePrivateCosmos`. Private Key Vault references use their own private endpoint and `privatelink.vaultcore.azure.net` DNS link, with dependency ordering before app creation. Either private-access setting enables the shared VNet infrastructure.

Execution history uses the singleton Cosmos client and `/trigger` partition key. Chat sessions and messages share a separate container partitioned by `/ownerId`, derived from the authenticated tenant/user identity. Readiness checks both containers even when dispatch is disabled. Development creates missing containers; Azure deployments provision them through Bicep, not elevated runtime permissions. Hangfire uses process-local memory and requires no connection string or database.

## Chat

| Setting | Default / requirement |
|---|---|
| `Chat:Container` | `chats`; must differ from `Cosmos:Container`; Bicep parameter `chatContainerName` |
| `Chat:SessionPageSize` | `30`, range `1..100`; Cosmos continuation-token paging |
| `Chat:MessagePageSize` | `40`, range `1..100`; latest messages, with earlier-message paging |
| `Chat:HistoryLimit` | `40`, range `1..100`; previous messages supplied with each new prompt |
| `Chat:MaxPromptLength` | `8000`, range `1..32000` characters |
| `Chat:MaxOutputTokens` | `2048`, range `1..16384`; model output bound |
| `Chat:RequestsPerMinute` | `20`, range `1..120`; session/message writes per authenticated caller |
| `Chat:TimeoutSeconds` | `120`, range `1..300`; cooperative request/model deadline |
| `Chat:FinalizationTimeoutSeconds` | `15`, range `1..60`; independent reply-persistence deadline |
| `Chat:Instructions` | Conversational assistant with read-only telemetry tools; no actions |

### Read-only telemetry tools

The default chat runner exposes `GetRecentExceptions`, `GetExceptionDetails`, `GetRecentTraces`
and `GetOperationTimeline`. Queries use server-owned KQL templates against `Insights:ResourceId`
through `LogsQueryClient.QueryResourceAsync`, with the Application Insights `exceptions`, `traces`,
`requests` and `dependencies` schema. No vector database, embeddings, MCP server or ingestion
connection string is required for querying.

| Setting | Default / requirement |
|---|---|
| `InsightsTools:Enabled` | `true`; `false` removes all telemetry tools |
| `InsightsTools:DefaultLookbackMinutes` | `60`; must not exceed the maximum |
| `InsightsTools:MaxLookbackMinutes` | `10080` (seven days), range `1..10080` |
| `InsightsTools:DefaultCount` | `5`; must not exceed `MaxRows` |
| `InsightsTools:MaxRows` | `20`, range `1..100` |
| `InsightsTools:QueryTimeoutSeconds` | `30`, range `1..120`; also bounded by the chat deadline |
| `InsightsTools:MaxToolCalls` | `6`, range `1..12`; bounds queries and framework invocation iterations per turn |
| `InsightsTools:MaxResultCharacters` | `16000`, range `1024..64000`; serialized tool-result budget |
| `InsightsTools:MaxFieldCharacters` | `4000`, range `128..8000`; must be below the result budget |
| `InsightsTools:MaxEvidenceReferences` | `100`, range `1..200`; bounded evidence references per turn/history context |

The server identity needs Monitoring Reader on the configured Application Insights resource.
Bicep grants this on the deployed component; observing a different component requires its own
role assignment. Query access is application-wide, not delegated per-user access. Only the
existing operator-authorized chat write path invokes the runner; readers cannot invoke tools.

Tool results are field-allowlisted (no user/client fields or custom dimensions), scrub common
credential/connection-string/token/email patterns, and mark clipped results explicitly.
This scrubber is defense in depth, not a guarantee against arbitrary sensitive text in logs:
do not ingest secrets or personal data, and approve telemetry disclosure to the configured model.
Telemetry content is untrusted evidence, never tool instructions. Failures, timeouts, missing
configuration and empty results remain distinct.

Assistant messages persist bounded evidence references in the existing owner-partitioned chat
container, atomically with the reply. Follow-ups resolve the original record and UTC query window,
not the current ordering of a new query. No raw tool payload or model thread is persisted.
Only references in the loaded conversation history are available; ambiguous or expired references
require clarification or a fresh list. Existing messages without evidence remain readable.

Operator sessions may create conversations and send messages; readers can only inspect their own existing conversations. A user prompt is saved before the model call. Model failures leave the prompt visible, return a safe error, and never fabricate an agent reply. Successful replies are saved atomically with updated session metadata. Calls in one conversation are serialized in the single application process; the existing one-replica restriction also applies to chat. Model invocations are not automatically retried.

Operators can delete their own session and all saved messages from the chat header after confirmation. Deletion and sending share the same per-conversation concurrency guard. Failed or canceled cleanup can be retried; a persistent deletion marker blocks new messages until cleanup finishes.

`ChatService`, the Cosmos chat adapter, the Foundry runner/factory and Cosmos initialization emit structured `ILogger` events. Logs include session/message IDs, history/result counts, model/deployment metadata, timings and exception objects for failed operations. Cancellation is logged separately from errors. Message text, session titles, owner identities, instructions and credentials are not included in application log templates; provider exception details stay server-side.

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
| `AZURE_CLIENT_ID` | Production user-assigned managed identity client ID; if omitted, use system-assigned identity. This is not the OIDC app registration client ID. |

Chat uses `BasicFoundryAgentRunner` through `IChatAgent`. It creates a Microsoft Agent Framework agent with `FoundryAgentFactory`, supplies Cosmos conversation history with each prompt, and records the model from the response (or the configured deployment). Startup and automated placeholder runs do not require a model and do not invoke one. The preview `Microsoft.Agents.AI.AzureAI` package is pinned; use `--prerelease` explicitly when evaluating upgrades.

The factory validates endpoint/model inputs when creating an agent. Missing or invalid settings return HTTP 503 with a configuration message rather than an unexplained 500. The UI shows a correlation reference for failures; full exception details remain in server logs. A saved prompt does not imply the model was called. Configure `Foundry:ProjectEndpoint` and `Foundry:ModelDeployment` locally with user-secrets, then restart the app. Azure deployments supply these settings through Bicep. No API key is required.

Upstream Foundry HTTP 429 responses become an actionable HTTP 503 rate-limit notice, not an
unexplained HTTP 500 or an automatic model retry. The saved user prompt remains available.
Local user-secrets and Container App environment settings are independent: choosing a model
in Azure does not change `Foundry:ModelDeployment` locally. Tool conversations require several
model requests per turn; provision adequate request/token quota or select an existing deployment
with sufficient quota. The agent receives the current configured telemetry limits each turn.

`X-Forwarded-For`, `X-Forwarded-Proto` and `X-Forwarded-Host` are processed before HTTPS redirection, Entra authentication and SignalR origin validation. Forwarded hosts must match `AllowedHosts`; untrusted peers cannot change the external URL. Default local hosting retains ASP.NET Core's loopback trust.

`Hosting:BehindContainerAppsIngress` trusts the nearest proxy hop from any peer because Container Apps manages the ingress addresses. Do not enable it on a directly exposed Kestrel server. It assumes external traffic is constrained to the managed Container Apps ingress, which must sanitize forwarded headers. Health probes supply an allowed Host header.

`RouteConstants.cs` is the source of application-local routes, callback defaults and virtual asset URLs. Razor emits the SignalR/history URLs for JavaScript, avoiding separate browser route literals. `/health` and `/health/ready` run persistence readiness checks; `/health/live` checks the running process. All three are anonymous and exempt from application HTTPS redirection so internal HTTP probes execute the checks. Public Container Apps ingress still enforces HTTPS, and application pages/API retain authentication.

For other reverse proxies, leave that flag `false` and set `Hosting__KnownProxies__0=10.0.0.5` or `Hosting__KnownNetworks__0=10.0.0.0/24`. Explicit lists replace the framework defaults and take precedence over the Container Apps trust-all mode. Only raise `ForwardLimit` to match a verified trusted proxy chain. Invalid addresses/networks fail startup. These settings describe incoming reverse-proxy headers, not an outbound HTTP proxy.
