using System.Threading.RateLimiting;
using AgentOps.Web.Features.Identity;
using AgentOps.Web.Infrastructure.Options;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Options;
using Microsoft.Identity.Web.UI;

namespace AgentOps.Web.Infrastructure;

public static class WebInfrastructureRegistration
{
    public static IServiceCollection AddAgentOpsWeb(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddEntraIdentity(configuration);
        services.AddRazorPages(options =>
        {
            options.Conventions.AuthorizeFolder(RouteConstants.Root, Policies.View);
            options.Conventions.AddPageRoute(RouteConstants.IndexPage, RouteConstants.Root);
            options.Conventions.AddPageRoute(RouteConstants.IndexPage, RouteConstants.LegacyIndexPage);
            options.Conventions.AddPageRoute(RouteConstants.ErrorPage, RouteConstants.LegacyErrorPage);
            options.Conventions.AddPageRoute(RouteConstants.SchedulesPage, RouteConstants.LegacySchedulesPage);
            options.Conventions.AddPageRoute(RouteConstants.ExecutionsPage, RouteConstants.LegacyExecutionsPage);
            options.Conventions.AddPageRoute(RouteConstants.LogsPage, RouteConstants.LegacyLogsPage);
        }).AddMicrosoftIdentityUI();
        services.AddSignalR();
        services.AddProblemDetails();
        services.AddExceptionHandler<AppExceptionHandler>();
        services.AddOptions<ForwardedHeadersOptions>().Configure<IOptions<HostingOptions>>((options, hosting) =>
        {
            var settings = hosting.Value;
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;
            options.ForwardLimit = settings.ForwardLimit;
            options.AllowedHosts = configuration["AllowedHosts"]?.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? [];
            if (settings.BehindContainerAppsIngress || settings.KnownProxies.Length > 0 || settings.KnownNetworks.Length > 0)
            {
                options.KnownIPNetworks.Clear();
                options.KnownProxies.Clear();
                foreach (var proxy in settings.KnownProxies) options.KnownProxies.Add(System.Net.IPAddress.Parse(proxy));
                foreach (var network in settings.KnownNetworks) options.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(network));
            }
        });
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.AddPolicy("notification-tests", context =>
                RateLimitPartition.GetFixedWindowLimiter(context.User.FindFirst("oid")?.Value ??
                    context.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value ?? "authenticated",
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = context.RequestServices.GetRequiredService<IOptions<NotificationOptions>>().Value.TestRequestsPerMinute,
                        Window = TimeSpan.FromMinutes(1),
                        QueueLimit = 0
                    }));
            options.AddPolicy("chat", context =>
                RateLimitPartition.GetFixedWindowLimiter(context.User.FindFirst("oid")?.Value ??
                    context.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value ?? "authenticated",
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = context.RequestServices.GetRequiredService<IOptions<ChatOptions>>().Value.RequestsPerMinute,
                        Window = TimeSpan.FromMinutes(1),
                        QueueLimit = 0
                    }));
            options.AddPolicy("executions", context =>
                RateLimitPartition.GetFixedWindowLimiter(context.User.FindFirst("oid")?.Value ??
                    context.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value ?? "authenticated",
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = context.RequestServices.GetRequiredService<IOptions<ExecutionOptions>>().Value.RequestsPerMinute,
                        Window = TimeSpan.FromMinutes(1),
                        QueueLimit = 0
                    }));
        });
        services.AddApplicationInsightsTelemetry(options =>
            options.ConnectionString = configuration["ApplicationInsights:ConnectionString"]);
        services.AddLogging(logging => logging.AddFilter<Microsoft.Extensions.Logging.ApplicationInsights.ApplicationInsightsLoggerProvider>(
            "AgentOps", LogLevel.Information));
        services.AddHealthChecks().AddCheck<DependencyReadiness>("persistence");
        return services;
    }
}
