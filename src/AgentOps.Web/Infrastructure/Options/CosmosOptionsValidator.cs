using Microsoft.Extensions.Options;

namespace AgentOps.Web.Infrastructure.Options;

/// <summary>Checks environment-specific credential boundaries before serving requests.</summary>
public sealed class CosmosOptionsValidator(IHostEnvironment environment) : IValidateOptions<CosmosOptions>
{
    public ValidateOptionsResult Validate(string? name, CosmosOptions options)
    {
        if (environment.IsDevelopment())
        {
            try
            {
                var connection = new System.Data.Common.DbConnectionStringBuilder { ConnectionString = options.ConnectionString };
                if (connection.TryGetValue("AccountEndpoint", out var endpoint) &&
                    Uri.TryCreate(endpoint.ToString(), UriKind.Absolute, out var emulator) && emulator is { IsLoopback: true, Scheme: "https" or "http" } &&
                    connection.TryGetValue("AccountKey", out var key) && !string.IsNullOrWhiteSpace(key.ToString()))
                    return ValidateOptionsResult.Success;
            }
            catch (ArgumentException) { }
            return ValidateOptionsResult.Fail("Cosmos:ConnectionString must identify a loopback development emulator with an account key.");
        }

        if (!string.IsNullOrWhiteSpace(options.ConnectionString) || options.AllowInsecureEmulatorTls || options.InitializeDatabase)
            return ValidateOptionsResult.Fail("Outside Development, Cosmos must use managed identity, trusted TLS and pre-provisioned resources.");
        return Uri.TryCreate(options.Endpoint, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail("Cosmos:Endpoint must be an HTTPS Azure Cosmos DB URL.");
    }
}
