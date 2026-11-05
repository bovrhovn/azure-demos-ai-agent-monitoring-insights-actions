using AgentOps.Web.Infrastructure.Options;
using Microsoft.Extensions.Options;

namespace AgentOps.Web.Infrastructure;

public static class OptionsRegistration
{
    public static IServiceCollection AddAgentOpsOptions(this IServiceCollection services)
    {
        services.AddOptions<CosmosOptions>().BindConfiguration(CosmosOptions.Section).ValidateDataAnnotations().ValidateOnStart();
        services.AddSingleton<IValidateOptions<CosmosOptions>, CosmosOptionsValidator>();
        services.AddOptions<IdentityOptions>().BindConfiguration(IdentityOptions.Section).ValidateDataAnnotations()
            .Validate(o => Guid.TryParse(o.TenantId, out _) && Guid.TryParse(o.ClientId, out _),
                "AzureAd tenant and client IDs must be GUIDs.").ValidateOnStart();
        services.AddOptions<ExecutionOptions>().BindConfiguration(ExecutionOptions.Section).ValidateDataAnnotations().ValidateOnStart();
        services.AddOptions<InsightsOptions>().BindConfiguration(InsightsOptions.Section).ValidateDataAnnotations().ValidateOnStart();
        services.AddOptions<InsightsToolOptions>().BindConfiguration(InsightsToolOptions.Section).ValidateDataAnnotations()
            .Validate(o => o.DefaultLookbackMinutes <= o.MaxLookbackMinutes && o.DefaultCount <= o.MaxRows &&
                o.MaxFieldCharacters < o.MaxResultCharacters,
                "InsightsTools defaults and field size must fit within the configured maximums.").ValidateOnStart();
        services.AddOptions<AnalyticsOptions>().BindConfiguration(AnalyticsOptions.Section).ValidateDataAnnotations()
            .Validate(o => o.PageSize <= o.MaxPageSize, "Analytics:PageSize cannot exceed MaxPageSize.").ValidateOnStart();
        services.AddOptions<FoundryOptions>().BindConfiguration(FoundryOptions.Section);
        services.AddOptions<ChatOptions>().BindConfiguration(ChatOptions.Section).ValidateDataAnnotations()
            .Validate<Microsoft.Extensions.Options.IOptions<CosmosOptions>>((chat, cosmos) => chat.Container != cosmos.Value.Container,
                "Chat:Container must be different from Cosmos:Container.").ValidateOnStart();
        services.AddOptions<WebhookOptions>().BindConfiguration(WebhookOptions.Section).ValidateDataAnnotations().ValidateOnStart();
        services.AddOptions<HostingOptions>().BindConfiguration(HostingOptions.Section).ValidateDataAnnotations()
            .Validate(o => o.KnownProxies.All(value => System.Net.IPAddress.TryParse(value, out _)),
                "Hosting:KnownProxies must contain IP addresses.")
            .Validate(o => o.KnownNetworks.All(value => System.Net.IPNetwork.TryParse(value, out _)),
                "Hosting:KnownNetworks must contain CIDR networks.").ValidateOnStart();
        return services;
    }
}
