namespace AgentOps.Web.Infrastructure;

public static class ServiceRegistration
{
    public static IServiceCollection AddAgentOps(this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment) =>
        services.AddAgentOpsOptions()
            .AddAgentOpsServices(environment)
            .AddAgentOpsWeb(configuration);
}
