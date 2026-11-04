using System.Text.Json;
using AgentOps.Web.Features.Agent;
using AgentOps.Web.Features.Chat;
using AgentOps.Web.Features.Executions;
using AgentOps.Web.Features.Insights;
using AgentOps.Web.Features.Scheduling;
using Azure.Core;
using Azure.Identity;
using Azure.Monitor.Query.Logs;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Options;

namespace AgentOps.Web.Infrastructure;

public static class ApplicationServiceRegistration
{
    public static IServiceCollection AddAgentOpsServices(this IServiceCollection services, IHostEnvironment environment)
    {
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<TokenCredential>(provider =>
        {
            if (!environment.IsProduction())
                return new DefaultAzureCredential();
            var clientId = provider.GetRequiredService<IConfiguration>()["AZURE_CLIENT_ID"];
            return new ManagedIdentityCredential(string.IsNullOrWhiteSpace(clientId)
                ? ManagedIdentityId.SystemAssigned
                : ManagedIdentityId.FromUserAssignedClientId(clientId));
        });
        services.AddSingleton(provider =>
        {
            var settings = provider.GetRequiredService<IOptions<CosmosOptions>>().Value;
            var options = new CosmosClientOptions
            {
                ConnectionMode = ConnectionMode.Gateway,
                UseSystemTextJsonSerializerWithOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web)
            };
            if (environment.IsDevelopment() && settings.AllowInsecureEmulatorTls)
                options.HttpClientFactory = () => new HttpClient(new HttpClientHandler
                {
                    ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
                });
            return environment.IsDevelopment()
                ? new CosmosClient(settings.ConnectionString, options)
                : new CosmosClient(settings.Endpoint, provider.GetRequiredService<TokenCredential>(), options);
        });
        services.AddSingleton<IExecutionStore, CosmosExecutionStore>();
        services.AddSingleton<IExecutionNotifier, SignalRExecutionNotifier>();
        services.AddScoped<ExecuteAgent>();
        services.AddSingleton<IAgentRunner, PendingAgentRunner>();
        services.AddSingleton<IFoundryAgentFactory, FoundryAgentFactory>();
        services.AddSingleton<IChatStore, CosmosChatStore>();
        services.AddSingleton<IChatAgent, BasicFoundryAgentRunner>();
        services.AddSingleton<ChatService>();
        services.AddSingleton(provider => new LogsQueryClient(provider.GetRequiredService<TokenCredential>()));
        services.AddScoped<IInsightsReader, InsightsReader>();
        services.AddSingleton<CosmosDataSource>();
        services.AddHostedService<CosmosInitialization>();
        services.AddAgentScheduling();
        return services;
    }
}
