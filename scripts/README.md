# Local scripts

PowerShell 7 scripts resolve paths from `$PSScriptRoot` and select the SDK from `src/global.json`.

| Script | Purpose |
|---|---|
| `run-web.ps1` | Native emulator startup, direct web build/HTTPS launch, readiness, browser and web PID cleanup |
| `test.ps1` | Clean frontend install/build and Release managed tests |
| `test-wsl.ps1` | Same suite under WSL with isolated Linux artifacts |
| `setup-production-identity.ps1` | Secretless Entra login federation, managed-identity Cosmos access and copyable environment settings |

## Production identity setup

Use an existing **user-assigned managed identity**, an existing Cosmos NoSQL account
and a pre-provisioned execution container with partition key `/trigger`. The script
does not deploy a host, create database resources, read account keys or create client
secrets. It supports Azure public cloud. Sign in with Azure CLI to the resource tenant;
the caller needs Entra application-management permissions and Cosmos SQL role-assignment
write permission on the account.

```powershell
.\scripts\setup-production-identity.ps1 `
    -DisplayName 'Agent Observatory production' `
    -AppUrl 'https://<application-host>' `
    -ManagedIdentityResourceId '/subscriptions/<subscription>/resourceGroups/<identity-rg>/providers/Microsoft.ManagedIdentity/userAssignedIdentities/<identity>' `
    -CosmosAccountName '<cosmos-account>' `
    -ResourceGroup '<cosmos-rg>' `
    -SubscriptionId '<cosmos-subscription>' `
    -WhatIf
```

Remove `-WhatIf` to apply. Copy the printed `NAME=value` settings into the Azure
host's environment configuration. Output can also be captured with
`$settings = & .\scripts\setup-production-identity.ps1 ...`; informational messages
are on the host stream and only environment settings are on the success stream.
On subsequent runs, supply `-ClientId '<printed-login-client-id>'` to update the
same registration without duplicating roles, scopes, federation or Cosmos grants.
An existing display name is never silently reused.

`-Database` and `-Container` default to `agentops` and `executions`. Role/scope names
are configurable with `-ReaderRole`, `-OperatorRole` and `-RunScope`. Existing app
roles, scopes, API preauthorizations and web callbacks are preserved; incompatible
roles/scopes are rejected. API access tokens use version 2.

The current Microsoft.Identity.Web sign-in-only flow requests `response_type=id_token`.
The script enables **ID tokens** under the registration's Web implicit/hybrid settings
to match that flow; it does not enable implicit access-token issuance. Existing
access-token issuance settings are preserved. Without ID-token issuance, Entra
returns `AADSTS700054` during sign-in even when Cosmos health checks are healthy.

Attach the selected identity to the host yourself. The app registration identifies
interactive login/API tokens; `AZURE_CLIENT_ID` selects the **managed identity**, not
the login registration. Login uses a [federated managed-identity assertion](https://learn.microsoft.com/en-us/entra/msidweb/authentication/certificateless).
Cosmos receives its own container-scoped **Cosmos DB Built-in Data Contributor**
assignment for the identity principal. No Cosmos permissions are granted to human users
or the login registration. Existing broader grants are not removed.

Assign users/groups the reader or operator role on the enterprise application.
API clients need separate role assignment or consent to `Agent.Run`; the script does
not auto-consent clients or assign human users. Remove any old `AzureAd__ClientSecret`,
`AZURE_CLIENT_SECRET`, `Cosmos__ConnectionString` and competing `AzureAd:ClientCredentials`
entries. Keep separately required webhook settings. Federation requires the attached
Azure identity endpoint and is not a local-development credential.

This is a standalone setup/configuration path. The existing `azure\scripts\Deploy-All.ps1`
still uses its secret-based Key Vault login configuration; it does not consume these
settings and a redeployment can restore its secret-based settings. Do not use that
deployment path expecting it to preserve this secretless configuration.

After propagation, verify real sign-in and `/health/ready` from the deployed host with
network access to Cosmos. Offline tests do not prove live Entra/RBAC access. If setup
fails after creating the app, reuse the printed ClientId when retrying; changes are not
rolled back automatically.

Run the script's offline tests (no Azure resource changes):

```powershell
Import-Module Pester -MinimumVersion 5.7.1
Invoke-Pester -Path .\scripts\tests -CI -Output Detailed
```

## Direct local launcher

Configure Cosmos emulator and Entra settings in user-secrets or environment first; see [local development](../docs/local-development.md).

```powershell
.\scripts\run-web.ps1
.\scripts\run-web.ps1 -WebPort 5085 -NoBrowser
.\scripts\run-web.ps1 -SkipEmulatorStart -SmokeTest -NoBrowser
```

| Parameter | Default / meaning |
|---|---|
| `WebPort` | `5080`; HTTPS port, matching registered Entra callbacks |
| `CosmosPort` | `8081`; native emulator port, also configure it in the local connection string |
| `StartupTimeoutSeconds` | `180`; separate bounded emulator/application readiness waits |
| `Configuration` | `Debug` |
| `TenantId`, `ClientId` | Optional non-secret overrides |
| `NoBrowser` | Do not open a browser |
| `SkipEmulatorStart` | Use an already running local emulator; also required on non-Windows systems |
| `SmokeTest` | Exit after real readiness and anonymous API-401 checks |

The runner starts the native Windows emulator if absent (Windows elevation may be needed). It leaves this shared service and developer data running after stopping the web app. The web child is stopped by exact PID only. No database container startup is required.

`test.ps1` accepts `-Configuration`; `test-wsl.ps1` accepts `-Distribution`, `-Configuration` and optional `-NuGetSource`. Both use `src/AgentOps.slnx`. See [testing](../docs/testing.md) for opt-in emulator and browser tests and [deployment](../azure/README.md) for Azure-specific operations.
