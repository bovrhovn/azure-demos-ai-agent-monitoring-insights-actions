using Microsoft.Extensions.Options;

namespace AgentOps.Web.Infrastructure;

/// <summary>Non-secret display metadata for the configured execution data source, not a health result.</summary>
public sealed class CosmosDataSource(IHostEnvironment environment, IOptions<CosmosOptions> options)
{
    public string EnvironmentName => environment.EnvironmentName;
    public string Label => $"Cosmos {EnvironmentName.ToLowerInvariant()}";
    public string ServiceName => environment.IsDevelopment() ? "Local emulator" : new Uri(options.Value.Endpoint).Host.Split('.')[0];
    public string Database => options.Value.Database;
    public string Container => options.Value.Container;
}
