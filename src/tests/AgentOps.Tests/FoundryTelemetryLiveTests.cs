using AgentOps.Web.Features.Agent;
using AgentOps.Web.Features.Chat;
using AgentOps.Web.Features.Insights;
using AgentOps.Web.Infrastructure;
using Azure.Core;
using Azure.Identity;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace AgentOps.Tests;

[TestClass]
public sealed class FoundryTelemetryLiveTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [TestCategory("LiveAzure")]
    public async Task DefaultRunnerQueriesLiveInsightsAndResolvesFollowUpEvidence()
    {
        if (Environment.GetEnvironmentVariable("AGENTOPS_LIVE_AZURE_TESTS") != "true")
            Assert.Inconclusive("Opt-in live Azure test. Set AGENTOPS_LIVE_AZURE_TESTS=true and the live resource/project/model settings; requires Azure CLI login and query/model permission.");
        var resource = Required("AGENTOPS_LIVE_INSIGHTS_RESOURCE_ID");
        var endpoint = Required("AGENTOPS_LIVE_FOUNDRY_ENDPOINT");
        var model = Required("AGENTOPS_LIVE_FOUNDRY_DEPLOYMENT");
        var environment = new Mock<IHostEnvironment>();
        environment.SetupGet(item => item.EnvironmentName).Returns(Environments.Development);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.Configure<InsightsOptions>(settings => settings.ResourceId = resource);
        services.Configure<FoundryOptions>(settings =>
        {
            settings.ProjectEndpoint = endpoint;
            settings.ModelDeployment = model;
        });
        services.AddAgentOpsServices(environment.Object);
        services.AddSingleton<TokenCredential>(new AzureCliCredential());
        services.AddSingleton<TelemetryQuery>();
        services.AddSingleton<ObservedQuery>(provider => new(provider.GetRequiredService<TelemetryQuery>()));
        services.AddSingleton<ITelemetryQuery>(provider => provider.GetRequiredService<ObservedQuery>());
        await using var provider = services.BuildServiceProvider();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var query = provider.GetRequiredService<ITelemetryQuery>();
        var now = DateTimeOffset.UtcNow;
        var direct = await query.QueryAsync(new(TelemetryQueryKind.Exceptions, now.AddHours(-24), now, 3), cancellation.Token);
        TestContext.WriteLine($"Direct Azure Monitor query: {direct.Rows.Count} recent exception records; truncated={direct.Truncated}.");
        if (direct.Rows.Count == 0) Assert.Inconclusive("Live querying succeeded but no exceptions are available for the drill-down assertion in the last 24 hours.");
        var observed = provider.GetRequiredService<ObservedQuery>();
        var runner = provider.GetRequiredService<IChatAgent>();
        runner.Should().BeOfType<BasicFoundryAgentRunner>();
        var chatOptions = Options.Create(new ChatOptions { TimeoutSeconds = 180 });
        var store = new MemoryChatStore();
        var chat = new ChatService(store, runner, TimeProvider.System, chatOptions, NullLogger<ChatService>.Instance);
        var session = await chat.CreateAsync("live-verification", cancellation.Token);
        string[] prompts =
        [
            "Query Application Insights for the last 3 exceptions in the last 24 hours. List each with its UTC timestamp, exception type, short message and exact reference ID.",
            "Go deeper into exception 1 from that list. Fetch its exception details and correlated operation timeline. Summarize the stack and distinguish evidence from hypotheses.",
            "Give me the last 5 traces in the last 30 minutes. Include UTC timestamps, severity and short messages. Query the tool even if earlier results exist."
        ];
        TelemetryEvidence? firstException = null;
        for (var index = 0; index < prompts.Length; index++)
        {
            var before = observed.Requests.Count;
            var reply = await chat.SendAsync("live-verification", session.Id, prompts[index], cancellation.Token);
            reply.Should().NotBeNull();
            reply!.Content.Should().NotBeNullOrWhiteSpace();
            TestContext.WriteLine($"PROMPT: {prompts[index]}");
            TestContext.WriteLine($"AGENT ({reply.Model}): {reply.Content}");
            if (index == 0)
            {
                reply.Evidence.Should().NotBeEmpty("the real model must call the exception tool");
                firstException = reply.Evidence.First(item => item.Type == "exception");
            }
            if (index == 1)
            {
                reply.Evidence.Should().Contain(item => item.Reference == firstException!.Reference,
                    "the follow-up must query the original exception, not an independently ordered result");
                observed.Requests.Skip(before).Should().Contain(request =>
                    request.Kind == TelemetryQueryKind.ExceptionDetails && request.ItemId == firstException!.ItemId);
                observed.Requests.Skip(before).Should().Contain(request =>
                    request.Kind == TelemetryQueryKind.Operation && request.OperationId == firstException!.OperationId);
            }
            if (index == 2)
            {
                reply.Evidence.Should().OnlyContain(item => item.Type == "trace");
                observed.Requests.Skip(before).Should().Contain(request =>
                    request.Kind == TelemetryQueryKind.Traces && request.Count == 5 &&
                    request.End - request.Start == TimeSpan.FromMinutes(30));
            }
        }
    }

    private static string Required(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value :
            throw new InvalidOperationException($"Configure {name} for the opted-in live Azure test.");

    private sealed class ObservedQuery(ITelemetryQuery inner) : ITelemetryQuery
    {
        public List<TelemetryQueryRequest> Requests { get; } = [];

        public Task<TelemetryQueryResult> QueryAsync(TelemetryQueryRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return inner.QueryAsync(request, cancellationToken);
        }
    }
}
