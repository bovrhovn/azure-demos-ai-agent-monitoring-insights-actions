using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace AgentOps.Web.Infrastructure;

/// <summary>Only the Development emulator is provisioned by application code.</summary>
public sealed class CosmosInitialization(CosmosClient client, IOptions<CosmosOptions> options,
    IOptions<ChatOptions> chat, ILogger<CosmosInitialization> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var settings = options.Value;
        if (!settings.InitializeDatabase)
        {
            logger.LogDebug("Cosmos initialization skipped; using pre-provisioned resources");
            return;
        }
        logger.LogInformation("Initializing Cosmos database {Database} and execution/chat containers", settings.Database);
        try
        {
            var database = await client.CreateDatabaseIfNotExistsAsync(settings.Database, cancellationToken: cancellationToken);
            await database.Database.CreateContainerIfNotExistsAsync(settings.Container, "/trigger", cancellationToken: cancellationToken);
            await database.Database.CreateContainerIfNotExistsAsync(chat.Value.Container, "/ownerId", cancellationToken: cancellationToken);
            logger.LogInformation("Cosmos database {Database} and containers {ExecutionContainer}, {ChatContainer} initialized",
                settings.Database, settings.Container, chat.Value.Container);
        }
        catch (CosmosException exception)
        {
            logger.LogError(exception, "Initializing Cosmos database {Database} failed", settings.Database);
            throw;
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>Readiness requires the authoritative Cosmos execution-history container.</summary>
public sealed class DependencyReadiness(CosmosClient cosmos, IOptions<CosmosOptions> options, IOptions<ChatOptions> chat) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        await cosmos.GetContainer(options.Value.Database, options.Value.Container).ReadContainerAsync(cancellationToken: cancellationToken);
        await cosmos.GetContainer(options.Value.Database, chat.Value.Container).ReadContainerAsync(cancellationToken: cancellationToken);
        return HealthCheckResult.Healthy();
    }
}
