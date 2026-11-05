using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using AgentOps.Web;
using AgentOps.Web.Features.Executions;
using AgentOps.Web.Features.Insights;
using AgentOps.Web.Features.Scheduling;
using AgentOps.Web.Features.Agent;
using AgentOps.Web.Features.Chat;
using AgentOps.Web.Features.Notifications;
using AgentOps.Web.Infrastructure;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;

namespace AgentOps.Tests;

internal sealed class AppFactory(IReadOnlyDictionary<string, string?>? settings = null, string environmentName = "Development",
    IAgentRunner? runner = null) : WebApplicationFactory<Program>
{
    public MemoryExecutionStore Store { get; } = new();
    public FakeInsights Insights { get; } = new();
    public MemoryChatStore Chats { get; } = new();
    public FakeChatAgent ChatAgent { get; } = new();
    public MemoryNotificationStore Notifications { get; } = new();
    public Moq.Mock<ITelemetryQuery> Telemetry { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(environmentName);
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["AzureAd:TenantId"] = "11111111-1111-1111-1111-111111111111",
            ["AzureAd:ClientId"] = "22222222-2222-2222-2222-222222222222",
            ["Webhook:ApiKey"] = "test-only-webhook-key",
            ["Cosmos:ConnectionString"] = "AccountEndpoint=http://localhost:8081/;AccountKey=dGVzdA==;",
            ["Cosmos:InitializeDatabase"] = "false",
            ["Scheduling:Enabled"] = "true",
            ["Scheduling:Cron"] = "0 0 1 1 *",
            ["Executions:RequestsPerMinute"] = "2",
            ["Executions:HistoryLimit"] = "2",
            ["ApplicationInsights:ConnectionString"] = "",
            ["AllowedHosts"] = "localhost;127.0.0.1"
        }).AddInMemoryCollection(settings ?? new Dictionary<string, string?>()));
        builder.ConfigureTestServices(services =>
        {
            Telemetry.Setup(query => query.QueryAsync(Moq.It.IsAny<TelemetryQueryRequest>(), Moq.It.IsAny<CancellationToken>()))
                .ReturnsAsync(new TelemetryQueryResult([], false));
            services.PostConfigure<AgentOps.Web.Infrastructure.Options.InsightsOptions>(options =>
                options.ResourceId = "/subscriptions/test/resourceGroups/test/providers/Microsoft.Insights/components/test");
            services.RemoveAll<ITelemetryQuery>();
            services.AddSingleton(Telemetry.Object);
            if (runner is not null)
            {
                services.RemoveAll<IAgentRunner>();
                services.AddSingleton(runner);
            }
            services.RemoveAll<IExecutionStore>();
            services.RemoveAll<IChatStore>();
            services.AddSingleton<IChatStore>(Chats);
            services.RemoveAll<INotificationStore>();
            services.AddSingleton<INotificationStore>(Notifications);
            services.RemoveAll<IChatAgent>();
            services.AddSingleton<IChatAgent>(ChatAgent);
            services.AddSingleton<IExecutionStore>(Store);
            services.RemoveAll<IInsightsReader>();
            services.AddSingleton<IInsightsReader>(Insights);
            services.Configure<HealthCheckServiceOptions>(options => options.Registrations.Clear());
            services.AddAuthentication(options =>
            {
                options.DefaultAuthenticateScheme = "Test";
                options.DefaultChallengeScheme = "Test";
                options.DefaultForbidScheme = "Test";
            }).AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>("Test", _ => { });
            services.Configure<AuthenticationOptions>(options =>
                options.SchemeMap["Bearer"].HandlerType = typeof(TestAuthenticationHandler));
        });
    }

    public HttpClient Client(string? role = null)
    {
        var client = CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, BaseAddress = new Uri("https://localhost") });
        if (role is not null) client.DefaultRequestHeaders.Add("X-Test-Role", role);
        return client;
    }
}

internal sealed class TestAuthenticationHandler(IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger, UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue("X-Test-Role", out var role))
            return Task.FromResult(AuthenticateResult.NoResult());
        var claims = new[] { new Claim(ClaimTypes.NameIdentifier, Request.Headers["X-Test-User"].FirstOrDefault() ?? "test-caller"), new Claim(ClaimTypes.Name, "Test operator"),
            new Claim(ClaimTypes.Role, role.ToString()) };
        var user = new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme.Name));
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(user, Scheme.Name)));
    }
}

internal sealed class FakeInsights : IInsightsReader
{
    private int logQueries;
    public int LogQueries => Volatile.Read(ref logQueries);
    public bool IsConfigured { get; set; } = true;
    public bool Fail { get; set; }
    public InsightsOverview Overview { get; set; } = new(42, 3, 125, 2);
    public IReadOnlyList<InsightLog> Logs { get; set; } = [new(DateTimeOffset.UtcNow, "2", "<script>alert('test')</script>", "operation-test")];
    public Task<InsightsOverview> OverviewAsync(CancellationToken cancellationToken) =>
        Fail ? throw new InvalidOperationException("private telemetry details") : Task.FromResult(Overview);
    public Task<IReadOnlyList<InsightLog>> LogsAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref logQueries);
        return Fail ? throw new InvalidOperationException("private telemetry details") : Task.FromResult(Logs);
    }
}
