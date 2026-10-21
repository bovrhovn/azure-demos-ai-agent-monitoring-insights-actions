# Deployment image

Local development runs the [web app directly](../docs/local-development.md) against the native Cosmos emulator. This folder contains only the optional Azure deployment image; no local database stack is needed.

`Dockerfile` builds browser assets with Node.js 24, selects the SDK and shared properties from `src`, publishes .NET 10, and runs the app as the non-root runtime user on port 8080. The image defaults to Production, requiring a trusted Cosmos HTTPS endpoint, managed identity and pre-provisioned execution/schedule containers.

```powershell
docker build -f containers\Dockerfile -t agent-observatory:local .
```

The optional `NUGET_SOURCE` build argument selects a NuGet index. A BuildKit `npmrc` secret configures npm only during installation; no personal registry is embedded in the Dockerfile. Do not pass credentials as build arguments.

The normal Azure flow uses ACR Tasks and needs no local Docker daemon; see [deployment](../azure/README.md).
