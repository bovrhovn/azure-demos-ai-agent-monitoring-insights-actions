# Agent Observatory

An ASP.NET Core **.NET 10 / C# Razor Pages** application for observing Application Insights and tracking Microsoft Foundry agent executions, on a schedule or through an authenticated Minimal API.

**Automated execution remains an infrastructure scaffold.** Scheduled/API runs are recorded as `NotImplemented`; they do not call a model or perform remediation. The separate **Agent chat** workspace invokes the configured Foundry model for text-only conversations, with no action tools.

## What is included

| Surface | Implementation |
|---|---|
| Overview | Request count, failures, mean latency and exception count from a configured Application Insights resource |
| Schedules | UTC recurring schedule, next/last dispatch, and scheduled execution history |
| Endpoint runs | Separate Cosmos DB history for `POST /api/executions`, updated live through SignalR |
| Application logs | Separate Application Insights trace viewer; not used as execution storage |
| Agent chat | Private Cosmos-backed sessions, historical context, model-labelled replies and message copying |
| Identity | Microsoft Entra ID OIDC for the web app; validated bearer tokens and role/scope authorization for the API |
| Agent integration | Microsoft Agent Framework + `AIProjectClient` behind `IFoundryAgentFactory`; behavior explicitly deferred |
| Persistence | Cosmos DB emulator connection string in Development; Cosmos HTTPS endpoint + `DefaultAzureCredential` outside Development |
| Background jobs | Official Hangfire ASP.NET Core server with Hangfire.InMemory process-local storage |
| Telemetry | Structured `ILogger` events exported to Application Insights using its connection string |
| Deployment | Non-root, multi-stage Linux container; Bicep for Azure Container Apps and supporting resources |
| Verification | MSTest unit/integration tests, real SignalR, opt-in Cosmos emulator and browser checks, WSL runner |

## Repository layout

```text
src/
  AgentOps.slnx     Solution (open this in your IDE)
  Directory.Build.props, global.json
  AgentOps.Web/
    Features/       Agent, Executions, Identity, Insights, Scheduling
    Infrastructure/ Validated options, Azure clients, readiness, safe error handling
    Pages/          Razor Pages and shared layout
    Frontend/       Official SignalR client integration
    wwwroot/        CSS and generated browser bundle
  tests/            MSTest, HTTP/SignalR, scheduler and opt-in Cosmos/browser tests
scripts/            Direct web launcher and Windows/WSL tests
azure/              Bicep resources and deployment parameters
containers/         Deployment Dockerfile (not used for local launch)
docs/               Setup, configuration, API, architecture and validation guides
```

## Run locally

Prerequisites: stable .NET 10 SDK, Node.js 24 LTS, PowerShell 7, the native Windows Azure Cosmos DB emulator (or an already running local emulator), Azure CLI, and an Entra web/API application registration. The configured Azure identity needs **Monitoring Reader** on the Application Insights resource you want to query. Local development runs the web app directly; no database containers are needed.

1. Follow [Entra setup](docs/local-development.md#configure-microsoft-entra-id): configure the HTTPS callback, application roles and delegated API scope.
2. Configure non-secret IDs and the confidential web-client credential using user-secrets:

   ```powershell
   $project = '.\src\AgentOps.Web\AgentOps.Web.csproj'
   dotnet user-secrets set 'AzureAd:TenantId' '<tenant-guid>' --project $project
   dotnet user-secrets set 'AzureAd:ClientId' '<application-client-guid>' --project $project
   # Supply the OIDC credential securely as described in docs/local-development.md.
   dotnet user-secrets set 'Insights:ResourceId' '<full-application-insights-resource-id>' --project $project
   dotnet user-secrets set 'ApplicationInsights:ConnectionString' '<telemetry-connection-string>' --project $project
   dotnet user-secrets set 'Cosmos:ConnectionString' '<local-emulator-connection-string>' --project $project
   ```

3. Sign in for Azure SDK access, trust the development certificate, and launch:

   ```powershell
   az login --tenant '<tenant-guid>'
   dotnet dev-certs https --trust
   .\scripts\run-web.ps1
   ```

The runner starts the native emulator if needed, builds assets and C# from `src`, waits for the Cosmos execution-history container, and opens **https://localhost:5080**. Use the emulator's local connection string and trust its certificate. Ctrl+C stops only the web application; the shared native emulator remains running.

```powershell
.\scripts\run-web.ps1 -WebPort 5085 -NoBrowser
.\scripts\run-web.ps1 -SkipEmulatorStart -NoBrowser
```

`-SkipEmulatorStart` uses an already running local emulator with your configured `Cosmos:ConnectionString`. Register any alternate HTTPS port as an Entra redirect URI. Development creates missing execution-history and chat containers; Bicep provisions them in Azure before app startup. Deployed environments retain managed-identity-only data access. Hangfire requires no scheduling database.

Open **Agent chat** from the menu with an operator account to create a session and send messages. Configure `Foundry:ProjectEndpoint` and `Foundry:ModelDeployment` first; the Azure SDK identity needs access to the Foundry project/model. Sending messages invokes a billable model. Enter sends; Shift+Enter adds a line. Each reply includes its model and a Copy button. The configurable recent history is sent with each prompt; the full saved timeline remains available through earlier-message paging.

Hangfire queues and scheduling state are **volatile** and lost on restart. The configured UTC recurring job is re-registered at startup; missed runs are not recovered across restarts. Failed agent jobs have no automatic retries. Cosmos execution history remains durable. The API still runs synchronously through the same execution handler as the background job. No Hangfire administrative dashboard is exposed.

Scheduling remains on the shared `ExecuteAgent` path. Cosmos ETags atomically reserve each UTC occurrence before running it. Dispatch is **at-most-once**, with no automatic action retries: a crash after reservation can lose that occurrence. Missed ticks coalesce to one run, and a bounded lease prevents overlapping dispatch. See [architecture](docs/architecture.md) for the cancellation and single-replica boundary.

Without `Insights:ResourceId`, the overview and logs show a configuration notice, not fabricated metrics. Authentication is **never bypassed** for local browsing.

## Run tests

```powershell
.\scripts\test.ps1
.\scripts\test-wsl.ps1 -Distribution FedoraLinux-42
```

Real Cosmos tests create isolated emulator databases and verify partitioned history and persistence from an actual Hangfire background execution beyond the scheduler's lifetime. They are opt-in and never count mock adapters as real Cosmos coverage.

Browser verification is opt-in; see [testing](docs/testing.md) for Chromium/Edge setup, infrastructure prerequisites, and corporate package-proxy overrides.

## Deploy to Azure

Start with [azure/README.md](azure/README.md). `azure\scripts\Deploy-All.ps1` provisions infrastructure, builds/pushes the container in ACR Tasks, deploys the digest-pinned image and waits for readiness. Omit `-AcrName` to create a registry or provide an existing one. Reuse Cosmos with `-CosmosAccountName` and `-CosmosResourceGroup`; private connectivity preserves a shared account's network settings. `Initialize-EntraApplication.ps1` can create a dedicated web/API registration and configure the deployed callbacks. Use `-WhatIf` for a local plan without Azure changes. Normal local development/test scripts never deploy Azure resources.

The Bicep provisions Container Apps, ACR/access, a user-assigned identity, Cosmos DB with local/key authentication disabled and execution/chat NoSQL containers, Key Vault, workspace-based Application Insights, and a Foundry account/project. It deploys **GPT-5.5** by default; model, version, SKU, capacity and deployment name are configurable. Chat uses that deployment; automated placeholder runs still make no model calls.

This demo intentionally stays at **one replica** for in-process SignalR and cookie-key storage. Network isolation, persistent encrypted cookie keys, distributed notifications, idempotent actions, retention and recovery must be designed before a production rollout.

Health is anonymous at `/health` and `/health/ready` (persistence readiness), and `/health/live` (process liveness). Application pages, the execution API and SignalR remain protected. Application-local URLs and virtual assets are centralized in `src/AgentOps.Web/RouteConstants.cs`.

## Documentation

See the [documentation index](docs/README.md), [configuration reference](docs/configuration.md), [API reference](docs/api.md), and [architecture](docs/architecture.md). The local runner follows the preflight/readiness/process-cleanup style of the `azure-demos-ui-chat-interface` project.
