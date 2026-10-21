using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace AgentOps.Web.Infrastructure;

/// <summary>Only the Development emulator is provisioned by application code.</summary>
public sealed class CosmosInitialization(CosmosClient client, IOptions<CosmosOptions> options) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var settings = options.Value;
        if (!settings.InitializeDatabase)
            return;
        var database = await client.CreateDatabaseIfNotExistsAsync(settings.Database, cancellationToken: cancellationToken);
        await database.Database.CreateContainerIfNotExistsAsync(settings.Container, "/trigger", cancellationToken: cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>Readiness requires the authoritative Cosmos execution-history container.</summary>
public sealed class DependencyReadiness(CosmosClient cosmos, IOptions<CosmosOptions> options) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        await cosmos.GetContainer(options.Value.Database, options.Value.Container).ReadContainerAsync(cancellationToken: cancellationToken);
        return HealthCheckResult.Healthy();
    }
}
