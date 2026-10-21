# Copilot Coding Agent — Squad Instructions

You are working on a project that uses **Squad**, an AI team framework. When picking up issues autonomously, follow these guidelines.

## Coordinator Canary Check

**IMPORTANT — run this check on EVERY session start, before doing any work:**

Inspect your `agent_instructions` for the coordinator canaries. This is a **two-state payload-integrity check within a known Squad coordinator session**; it verifies whether the coordinator payload loaded fully or was visibly truncated. It is **not** proof that an agent with no canaries is safely non-Squad — wholly absent payloads and proven non-Squad agents require a host-level identity beacon before custom-agent instruction ingestion.

1. If the HEAD canary `SQUAD_COORDINATOR_CANARY_HEAD_b7d2` is **absent**, the Squad coordinator payload is not observable from inside this prompt. Treat non-Squad discrimination as an **OPEN host-level concern**; do not claim this check proved the current agent is non-Squad.
2. If the HEAD canary `SQUAD_COORDINATOR_CANARY_HEAD_b7d2` is **present** and the EOF canary `SQUAD_COORDINATOR_CANARY_a8f3` is **present**, the coordinator prompt loaded fully. Proceed with normal Squad behavior.
3. If the HEAD canary `SQUAD_COORDINATOR_CANARY_HEAD_b7d2` is **present** but the EOF canary `SQUAD_COORDINATOR_CANARY_a8f3` is **absent**, the coordinator prompt loaded but was **truncated** before the end.

**When the coordinator prompt is truncated, you MUST:**
1. **STOP** — do not proceed with standard Squad behavior.
2. **WARN the user** with this exact message:
   ```
   ⚠️ Squad coordinator (squad.agent.md) loaded but appears to be truncated before the EOF canary. Do NOT proceed with standard Squad behavior — Squad's safety rails are incomplete. Please restart your session.
   ```
3. Do not continue with normal Squad routing, spawning, PR, or branch-protection behavior after emitting the warning.

## Team Context

Before starting work on any issue:

1. Read `.squad/team.md` for the team roster, member roles, and your capability profile.
2. Read `.squad/routing.md` for work routing rules.
3. If the issue has a `squad:{member}` label, read that member's charter at `.squad/agents/{member}/charter.md` to understand their domain expertise and coding style — work in their voice.

## Capability Self-Check

Before starting work, check your capability profile in `.squad/team.md` under the **Coding Agent → Capabilities** section.

- **🟢 Good fit** — proceed autonomously.
- **🟡 Needs review** — proceed, but note in the PR description that a squad member should review.
- **🔴 Not suitable** — do NOT start work. Instead, comment on the issue:
  ```
  🤖 This issue doesn't match my capability profile (reason: {why}). Suggesting reassignment to a squad member.
  ```

## Branch Naming

Use the squad branch convention:
```
squad/{issue-number}-{kebab-case-slug}
```
Example: `squad/42-fix-login-validation`

## PR Guidelines

When opening a PR:
- Reference the issue: `Closes #{issue-number}`
- If the issue had a `squad:{member}` label, mention the member: `Working as {member} ({role})`
- If this is a 🟡 needs-review task, add to the PR description: `⚠️ This task was flagged as "needs review" — please have a squad member review before merging.`
- Follow any project conventions in `.squad/decisions.md`

## Decisions

If you make a decision that affects other team members, write it to:
```
.squad/decisions/inbox/copilot-{brief-slug}.md
```
The Scribe will merge it into the shared decisions file.

---

# Agent Observatory Repository Guidance

## Toolchain and commands

- The solution is `src\AgentOps.slnx`; `src\global.json` pins .NET SDK `10.0.100`. Run direct managed commands from `src` so SDK selection applies. Node.js 24 LTS builds the browser bundle; repository scripts use PowerShell 7.
- Run the normal local verification, which performs a clean frontend install/build and the Release test suite:

  ```powershell
  .\scripts\test.ps1
  ```

- For an already-built frontend, run all managed tests:

  ```powershell
  Push-Location src
  dotnet test AgentOps.slnx --configuration Release
  Pop-Location
  ```

- Run one MSTest method with its fully-qualified name:

  ```powershell
  Push-Location src
  dotnet test tests\AgentOps.Tests\AgentOps.Tests.csproj --configuration Release --filter "FullyQualifiedName~AgentOps.Tests.ExecutionTests.MethodName"
  Pop-Location
  ```

- Build the SignalR browser bundle after changing `Frontend\site.js`:

  ```powershell
  npm ci --prefix src\AgentOps.Web
  npm run build --prefix src\AgentOps.Web
  ```

- Start the local HTTPS web application directly with the native Cosmos emulator:

  ```powershell
  .\scripts\run-web.ps1 -NoBrowser
  ```

  Configure `Cosmos:ConnectionString` for the local emulator first. Use `-SkipEmulatorStart` with an already running emulator; no database containers are required.

- Infrastructure checks are explicit:

  ```powershell
  az bicep build --file azure\main.bicep
  Invoke-Pester -Path .\azure\tests -CI
  ```

- Cosmos integration tests are opt-in and require `AGENTOPS_TEST_COSMOS_CONNECTION_STRING`; run them with `--filter TestCategory=Emulator`. Browser tests require Playwright Chromium plus `AGENTOPS_BROWSER_TESTS=true`, then use `--filter TestCategory=Browser`. These tests intentionally report inconclusive rather than substitute mocks when their prerequisites are unavailable.

## Architecture

- This is a .NET 10 Razor Pages application with a Minimal API, SignalR, Hangfire with official Hangfire.InMemory storage, and Azure SDK integrations. Feature folders own their vertical slices; `Infrastructure\ServiceRegistration.cs` composes validated options, cloud clients, authentication, health checks, and hosting concerns. `Features\Scheduling` owns scheduling options, registration and jobs.
- Both `POST /api/executions` and the scheduler's `AgentJob` call `ExecuteAgent`. Preserve that shared path so both triggers retain identical lifecycle, correlation, persistence, error, cancellation, and notification behavior.
- Execution records have a deliberate ordering: save `Running` to Cosmos, notify SignalR, run the agent, then save and notify the terminal record using an independent bounded finalization token. Cosmos history is authoritative; SignalR is best-effort and must never change a successfully persisted result into a failure.
- Keep the three data planes separate: durable Cosmos execution history partitioned by `trigger`, volatile Hangfire.InMemory scheduling state, and Application Insights telemetry. Re-register the configured UTC recurring job at startup; queued work and scheduling state are lost on restart. Agent failures have no automatic retries, but Hangfire shutdown requeueing is not an exactly-once guarantee. Preserve cooperative bounded cancellation and one replica/worker. Do not add relational scheduling storage or derive execution history from logs or transient hub events.
- `PendingAgentRunner` is intentionally non-functional: it returns `NotImplemented` and never creates or invokes a Foundry model. Real behavior belongs behind `IAgentRunner` and `IFoundryAgentFactory`; define the action, authorization, cancellation, idempotency, and lifecycle contract before enabling a model or tool.
- Razor Pages and the SignalR hub are reader/operator session surfaces. `/api/executions` is bearer-only and requires either the operator role or exact `Agent.Run` scope. Keep API and UI authorization distinct.

## Repository conventions

- Keep application-local routes, callback defaults and virtual assets in `RouteConstants.cs`; render browser endpoint URLs from Razor rather than duplicating route literals in JavaScript. `/health`, `/health/live` and `/health/ready` must remain anonymous and bypass application HTTPS redirection for real internal probe execution.
- Add settings through typed, data-annotated classes in `Infrastructure\AppOptions.cs`, bind and validate them in `AddAgentOps`, and preserve the credential boundary. Development uses a local Cosmos emulator connection string; non-Development requires trusted HTTPS, `DefaultAzureCredential` and pre-provisioned containers.
- Do not add credentials to tracked JSON, Dockerfiles, Bicep parameter files, or documentation. Use user-secrets locally and Key Vault/environment configuration in Azure.
- Execution API and UI records use the immutable `ExecutionRecord` contract and the `ExecutionContract` trigger/status constants. Keep query history bounded by `Executions:HistoryLimit`, scoped to a known trigger partition, newest first.
- Surface failures rather than fabricating healthy data: Insights distinguishes unavailable configuration, empty results, and query failures; the exception handler returns safe Problem Details while correlated details stay in structured logs.
- The official SignalR client is bundled from `Frontend\site.js` to `wwwroot\js\app.js`. Render received strings with DOM `textContent` rather than HTML, preserve reconnect history reconciliation, and do not hand-edit the generated bundle.
- Tests use `AppFactory` to replace only external adapters with test-host implementations; the shipping application has no in-memory execution-history fallback or authentication bypass. Scheduling intentionally uses the real Hangfire.InMemory provider in both shipping and test hosts. Real Cosmos tests are explicitly opt-in, local-emulator-only and never claimed as passing when inconclusive.
- The deployment intentionally runs one replica because cookie keys and SignalR lack distributed backing. Do not raise replica counts or add automatic action retries without the associated persistence, backplane, idempotency, and recovery design.
