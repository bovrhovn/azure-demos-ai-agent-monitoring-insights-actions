# Local development

This guide takes a developer from a clean checkout to an authenticated local Razor application.

## Prerequisites

- Stable .NET 10 SDK; `src/global.json` excludes preview SDKs and allows installed .NET 10 feature bands. Open `src/AgentOps.slnx` in the IDE.
- Node.js 24 LTS and npm for the official SignalR browser bundle.
- PowerShell 7 for the launchers.
- Native Windows Azure Cosmos DB emulator, or an already running loopback emulator.
- Azure CLI and access to your Entra tenant.
- An existing Application Insights resource if you want live telemetry.

The web application is server-rendered. There is no separate frontend server, no Node runtime in the final app container, and no npm registry customization in the Dockerfile.

## Configure Microsoft Entra ID

Create a single-tenant application registration for the web/API application:

1. Under **Authentication**, add a **Web** redirect URI: `https://localhost:5080/signin-oidc`. Add `https://localhost:5080/signout-callback-oidc` as the post-logout callback where required by your tenant configuration. Use the corresponding deployed HTTPS host for Azure. Under **Implicit grant and hybrid flows**, enable **ID tokens** for the current sign-in-only flow; leave **Access tokens** disabled. Missing ID-token issuance causes `AADSTS700054` even when the application's health endpoint is healthy.
2. Create a confidential client credential. Store it in user-secrets locally, never `appsettings.json`. In Azure it is passed securely to Key Vault and referenced by the Container App.
3. Define application roles `AgentOps.Reader` and `AgentOps.Operator`. Allow users/groups for web access; allow applications on the operator role if machine-to-machine invocation is needed.
4. Assign your test user one of these roles on the enterprise application. A tenant login alone does not grant access.
5. Under **Expose an API**, use `api://<client-id>` as the application ID URI and expose the delegated scope `Agent.Run`. Authorize/consent the client that will request tokens.
6. API callers must request a token **for this API**, not a Microsoft Graph token. App-only callers need the operator application role; delegated callers can use the exact `Agent.Run` scope or operator role.

No development authentication bypass is provided. The test project installs a fake authentication handler **only inside its own test host**.

## Store local settings

The project already has a `UserSecretsId`. The CLI stores values outside the repository:

```powershell
$project = '.\src\AgentOps.Web\AgentOps.Web.csproj'
dotnet user-secrets set 'AzureAd:TenantId' '<tenant-guid>' --project $project
dotnet user-secrets set 'AzureAd:ClientId' '<client-guid>' --project $project
dotnet user-secrets set 'Insights:ResourceId' '<full-resource-id>' --project $project
dotnet user-secrets set 'ApplicationInsights:ConnectionString' '<connection-string>' --project $project
dotnet user-secrets set 'Cosmos:ConnectionString' '<local-emulator-connection-string>' --project $project
```

To keep the web-client secret out of shell history, read it securely and pass JSON to standard input:

```powershell
$secret = Read-Host 'Entra web client secret' -AsSecureString
$credential = [System.Net.NetworkCredential]::new('', $secret)
@{ 'AzureAd:ClientSecret' = $credential.Password } |
    ConvertTo-Json -Compress |
    dotnet user-secrets set --project $project
$credential = $null
$secret = $null
```

Set the required webhook option the same way, without placing its value in shell history:

```powershell
$secret = Read-Host 'Event Grid webhook API key' -AsSecureString
$credential = [System.Net.NetworkCredential]::new('', $secret)
@{ 'Webhook:ApiKey' = $credential.Password } |
    ConvertTo-Json -Compress |
    dotnet user-secrets set --project $project
$credential = $null
$secret = $null
```

User-secrets are a local development convenience, not an encrypted production vault.

`Insights:ResourceId` identifies the resource to **read**. `ApplicationInsights:ConnectionString` selects the resource receiving this app's **own telemetry**. They may refer to different resources. A connection string cannot authorize read queries; those use `DefaultAzureCredential`.

```powershell
az login --tenant '<tenant-guid>'
dotnet dev-certs https --trust
.\scripts\run-web.ps1
```

The SDK uses your Azure developer sign-in for queries. Grant **Monitoring Reader** on the monitored Application Insights resource. Foundry is not invoked yet.

## What the runner does

`scripts/run-web.ps1` resolves the repository from its own location, selects the SDK from `src`, checks tools/certificate, starts the native Windows Cosmos emulator if needed, builds browser assets and C#, launches the web DLL as a tracked child process, and waits for `/health/ready`.

The default address is `https://localhost:5080`. Connection strings come from your user-secrets or environment and are not printed. Copy the local connection string from the emulator's Data Explorer. If you select `-CosmosPort`, use that same port in your configured connection string.

The native emulator authenticates with its local emulator key and HTTPS certificate. It does not test Azure managed-identity RBAC. Starting the emulator may require Windows elevation; if necessary start it from the Start menu first.

Ctrl+C stops only the tracked web process. The shared emulator and its data are left intact.

## Use existing local dependencies

```powershell
dotnet user-secrets set 'Cosmos:ConnectionString' '<emulator-connection-string>' --project $project
.\scripts\run-web.ps1 -SkipEmulatorStart -NoBrowser
```

Trust the emulator certificate. `Cosmos:AllowInsecureEmulatorTls=true` is an explicit Development-only escape hatch for a self-signed local emulator; it is rejected outside Development. Do not use this setting for deployed accounts. Production requires a trusted HTTPS Cosmos endpoint and managed identity.

## Diagnose startup

| Symptom | Action |
|---|---|
| Emulator not ready | Start the native Cosmos emulator from the Start menu; verify its local explorer and certificate |
| Port already in use | Select different runner ports; update the Entra redirect URI for the web port |
| AzureAd validation fails | Supply valid tenant/client GUIDs through user-secrets or environment |
| Sign-in succeeds but page returns 403 | Assign the user a reader/operator role, then sign out and back in |
| OIDC credential error | Check the confidential credential, expiry, callback URI and tenant |
| Readiness fails | Inspect startup output; the Cosmos execution-history container (`/trigger`) must be accessible |
| Overview query fails | Check the full resource ID, `az login`, Monitoring Reader permission and network access |
| Metrics/logs are blank or delayed | Check the selected time window and Azure ingestion delay; no sample telemetry is substituted |
| Corporate package TLS errors | Configure your package sources externally; see [testing](testing.md#package-proxies) |
