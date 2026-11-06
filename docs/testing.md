# Testing

## Live telemetry agent verification

The normal suite covers KQL templates and bounds, sanitization, explicit failures, cancellation,
reference isolation, and real Agent Framework function invocation with a simulated model transport.
The default chat runner and query adapter can additionally be checked against real Azure resources:

```powershell
$env:AGENTOPS_LIVE_AZURE_TESTS = 'true'
$env:AGENTOPS_LIVE_INSIGHTS_RESOURCE_ID = '<Application Insights ARM resource ID>'
$env:AGENTOPS_LIVE_FOUNDRY_ENDPOINT = '<Foundry project HTTPS endpoint>'
$env:AGENTOPS_LIVE_FOUNDRY_DEPLOYMENT = '<function-calling model deployment>'
Push-Location src
dotnet test tests\AgentOps.Tests\AgentOps.Tests.csproj --configuration Release --filter TestCategory=LiveAzure --logger "console;verbosity=detailed"
Pop-Location
```

Requires `az login`, Azure Monitor read permission, and Foundry model access. This opt-in test
incurs model/query usage, prints actual model answers, and uses in-memory test chat storage rather
than writing verification conversations to production Cosmos. It first queries Insights directly,
then exercises exception listing, original-reference drill-down/correlation, and recent traces
through the default runner. It reports inconclusive without explicit opt-in or without recent
exceptions for the drill-down assertion; it never substitutes mock telemetry.

The suite uses MSTest, FluentAssertions and Moq. Default HTTP/browser tests replace external execution-history and Insights adapters only inside the test host; shipping history remains in Cosmos, with no authentication fallback. Tests use the shipping Hangfire.InMemory registration and real Hangfire server.

## Fast application checks

From the repository root:

```powershell
.\scripts\test.ps1
```

This performs a clean frontend dependency install/build and the Release suite. For already-built assets, select the pinned SDK by working from `src`:

```powershell
Push-Location src
dotnet test AgentOps.slnx --configuration Release
Pop-Location
```

Tests cover both execution triggers, failures/cancellation, independent finalization, notification failures, exact role/scope checks, real production Cosmos adapter registration, credential boundaries, protected pages/hubs/API, escaped HTML, separate histories, Running/terminal SignalR delivery and rate limiting. Scheduler tests inspect actual Hangfire.InMemory selection, typed UTC recurring registration, idempotent registration, disabled definitions, volatile state across restart and startup failure propagation. Tests explicitly trigger registered jobs and await execution events rather than waiting for recurring cron timers. Agent retry policy is inspected through Hangfire's actual job-filter provider.

## Real Cosmos emulator

Start the native emulator, trust its HTTPS certificate and opt in using its **local** connection string:

```powershell
$connection = Read-Host 'Local Cosmos emulator connection string' -AsSecureString
$env:AGENTOPS_TEST_COSMOS_CONNECTION_STRING = [System.Net.NetworkCredential]::new('', $connection).Password
Push-Location src
try {
    dotnet test AgentOps.slnx --configuration Release --filter TestCategory=Emulator
} finally {
    Pop-Location
    Remove-Item Env:\AGENTOPS_TEST_COSMOS_CONNECTION_STRING
}
```

Tests refuse non-loopback endpoints. They create uniquely named databases, then delete only those databases. One verifies camel-case history, trigger isolation, upserts, ordering and limits. The other triggers the registered job on a real Hangfire server with in-memory storage, verifies Running/terminal notifications, then stops that host and reads the durable terminal execution through a fresh Cosmos adapter.

If a local emulator certificate cannot be trusted, `AGENTOPS_TEST_EMULATOR_INSECURE_TLS=true` is an explicit test-only workaround, restricted to the already validated loopback endpoint. It does not change shipping configuration.

Without opt-in prerequisites tests are inconclusive, not replaced with mocks. Emulator success does not validate Azure managed-identity RBAC.

## Direct runtime smoke check

Configure Development Cosmos and Entra settings as described in [local development](local-development.md), then:

```powershell
.\scripts\run-web.ps1 -SkipEmulatorStart -NoBrowser -SmokeTest
```

This builds browser/C# assets, runs the real shipping application, waits for the Cosmos history container and verifies the anonymous execution API returns 401. It stops its own web PID, leaves the shared emulator intact, and does not pretend to verify a real Entra sign-in.

## Browser test

From `src`, after a test build:

```powershell
dotnet build tests\AgentOps.Tests\AgentOps.Tests.csproj --configuration Release
pwsh tests\AgentOps.Tests\bin\Release\net10.0\playwright.ps1 install chromium
$env:AGENTOPS_BROWSER_TESTS = 'true'
dotnet test AgentOps.slnx --configuration Release --filter TestCategory=Browser
```

An existing Edge installation can be selected with `AGENTOPS_BROWSER_CHANNEL=msedge`. The tests run real Razor/Kestrel/SignalR with test-only external adapters, check metrics, live execution updates, history bounds, reconnect recovery from authoritative history, a 390-pixel viewport, scheduled-history separation and safe text rendering. A real Hangfire recurring job is explicitly dispatched and held at the runner boundary so the browser must observe both Running and terminal states. These are not real tenant sign-in or Cosmos integration tests. `AGENTOPS_BROWSER_SCREENSHOT` optionally names an output screenshot.

`NotificationBrowserTests` also exercises 250-character Unicode boundaries, truncated message/raw-event previews, complete escaped dialog content, record switching, native keyboard focus containment, Escape/Close focus restoration and desktop/mobile long-JSON layout. `AGENTOPS_NOTIFICATION_DETAILS_SCREENSHOT` optionally sets a screenshot path prefix (desktop/mobile suffixes are added). Frontend tests independently cover preview boundaries and surrogate-pair safety.

## WSL

```powershell
.\scripts\test-wsl.ps1 -Distribution FedoraLinux-42
```

Install stable .NET 10 in that distribution first. The script selects `src/global.json` and writes separate `.artifacts-wsl` output so Linux/Windows artifacts do not collide. Build frontend assets first. If forwarding emulator variables through `WSLENV`, preserve existing entries; never broadly expose the emulator to make the test pass.

## Package proxies

Use external environment/package-manager configuration rather than personal registry URLs in Dockerfiles:

```powershell
$env:npm_config_registry = '<organization-npm-registry-url>'
.\scripts\test.ps1
.\scripts\test-wsl.ps1 -NuGetSource '<nuget-v3-index-url>'
```

Deployment Docker builds accept `--secret id=npmrc,src=<local-npmrc>` and optional `--build-arg NUGET_SOURCE=<nuget-v3-index-url>`. Never put credentials in build arguments.

## Infrastructure and real identity validation

```powershell
az bicep build --file azure\main.bicep
Import-Module Pester -MinimumVersion 5.7.1
Invoke-Pester -Path .\azure\tests -Output Detailed -CI
Invoke-Pester -Path .\scripts\tests -Output Detailed -CI
```

These compile actual Bicep/generated parameter files while mocking Azure and HTTP mutations. They verify separate Cosmos partition paths, new/existing/ABAC registries, model settings, deployment order, digest pinning, secret/environment cleanup, failures and readiness retries/timeouts. They do not upload source or spend Azure quota.

Live tenant validation still requires Entra sign-in, API token permissions, managed-identity data-plane checks, regional quota checks and a reviewed deployment what-if. The managed suite separately exercises forwarded hosts/peers, hop limits, CIDRs, HTTPS redirects and SignalR origin boundaries. CI runs managed/browser and offline infrastructure suites.

The production identity script tests mock Azure CLI and inspect the actual emitted
Graph/federation payloads, container-scoped role grant and copyable environment
summary. They cover reruns, preservation of existing app configuration, invalid
inputs, cross-tenant rejection, `-WhatIf`, CLI failures and temporary-file cleanup.
The managed configuration suite also verifies that shipping Entra registration binds
the secretless Microsoft.Identity.Web credential settings.
