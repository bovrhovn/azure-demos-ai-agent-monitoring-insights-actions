using System.Collections.Concurrent;
using System.Net;
using AgentOps.Web;
using AgentOps.Web.Features.Agent;
using AgentOps.Web.Features.Chat;
using AgentOps.Web.Features.Executions;
using AgentOps.Web.Features.Insights;
using AgentOps.Web.Pages.Analytics;
using AgentOps.Web.Pages.Info;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using System.Net.Http.Json;

namespace AgentOps.Tests;

[TestClass]
public sealed class RequestLoggingTests
{
    [TestMethod]
    public async Task ChatLogsLifecycleMetadataWithoutConversationContentsOrOwnerIdentity()
    {
        await using var factory = new AppFactory();
        var logs = new RequestLogs();
        await using var host = factory.WithWebHostBuilder(builder => builder.ConfigureLogging(logging => logging.AddProvider(logs)));
        using var client = host.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-Role", "AgentOps.Operator");
        client.DefaultRequestHeaders.Add("X-Test-User", "private-owner");
        await ChatTests.AuthorizePosts(client);
        var created = await client.PostAsync(RouteConstants.ChatSessions, null);
        var session = (await created.Content.ReadFromJsonAsync<ChatSession>())!;
        var path = $"{RouteConstants.ChatSessions}/{session.Id}";
        (await client.PostAsJsonAsync(path + "/messages", new { content = "private prompt content" })).EnsureSuccessStatusCode();
        (await client.DeleteAsync(path)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        var entries = logs.Entries.Where(entry => entry.Category == typeof(ChatService).FullName).ToArray();
        entries.Should().Contain(entry => entry.Message.Contains("created"));
        entries.Should().Contain(entry => entry.Message.StartsWith("Loaded 0 context messages"));
        entries.Should().Contain(entry => entry.Message.Contains("invoking agent"));
        entries.Should().Contain(entry => entry.State.ContainsKey("Model") && Equals(entry.State["Model"], "test-deployment"));
        entries.Should().Contain(entry => entry.Message.EndsWith("and its messages deleted"));
        entries.Should().OnlyContain(entry => entry.State.ContainsKey("SessionId") && Equals(entry.State["SessionId"], session.Id));
        logs.Entries.Should().OnlyContain(entry => !entry.Message.Contains("private", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    [DataRow("agent")]
    [DataRow("persistence")]
    [DataRow("deletion")]
    public async Task ChatLogsExceptionsWithSessionIdAndDoesNotReportSuccessfulCompletion(string failure)
    {
        await using var factory = new AppFactory();
        var logs = new RequestLogs();
        await using var host = factory.WithWebHostBuilder(builder => builder.ConfigureLogging(logging => logging.AddProvider(logs)));
        using var client = host.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-Role", "AgentOps.Operator");
        await ChatTests.AuthorizePosts(client);
        var session = (await (await client.PostAsync(RouteConstants.ChatSessions, null)).Content.ReadFromJsonAsync<ChatSession>())!;
        factory.ChatAgent.Fail = failure == "agent";
        factory.Chats.FailAppend = failure == "persistence";
        factory.Chats.FailDelete = failure == "deletion";
        var path = $"{RouteConstants.ChatSessions}/{session.Id}";
        var response = failure == "deletion" ? await client.DeleteAsync(path)
            : await client.PostAsJsonAsync(path + "/messages", new { content = "private prompt" });
        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        var entries = logs.Entries.Where(entry => entry.Category == typeof(ChatService).FullName).ToArray();
        var error = entries.Should().ContainSingle(entry => entry.Level == LogLevel.Error).Subject;
        error.Exception.Should().BeOfType<InvalidOperationException>();
        error.State["SessionId"].Should().Be(session.Id);
        entries.Should().NotContain(entry => entry.Message.StartsWith("Agent message") || entry.Message.EndsWith("and its messages deleted"));
        entries.Should().OnlyContain(entry => !entry.Message.Contains("private", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public async Task FoundryFactoryAndRunnerLogConfigurationFailureWithoutInstructionsOrPrompts()
    {
        await using var factory = new AppFactory(new Dictionary<string, string?> { ["Foundry:ProjectEndpoint"] = "" });
        var logs = new RequestLogs();
        await using var host = factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureLogging(logging => logging.AddProvider(logs));
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IChatAgent>();
                services.AddSingleton<IChatAgent, BasicFoundryAgentRunner>();
            });
        });
        using var client = host.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-Role", "AgentOps.Operator");
        await ChatTests.AuthorizePosts(client);
        var session = (await (await client.PostAsync(RouteConstants.ChatSessions, null)).Content.ReadFromJsonAsync<ChatSession>())!;
        (await client.PostAsJsonAsync($"{RouteConstants.ChatSessions}/{session.Id}/messages", new { content = "private prompt" }))
            .StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        logs.Entries.Should().Contain(entry => entry.Category == typeof(FoundryAgentFactory).FullName && entry.Level == LogLevel.Warning);
        logs.Entries.Should().Contain(entry => entry.Category == typeof(BasicFoundryAgentRunner).FullName &&
            entry.Level == LogLevel.Error && entry.Exception is FoundryConfigurationException);
        logs.Entries.Should().OnlyContain(entry => !entry.Message.Contains("private", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    [DataRow(RouteConstants.SchedulesPage, typeof(SchedulesModel), "Schedules page loaded")]
    [DataRow(RouteConstants.IndexPage, typeof(IndexModel), "Overview page loaded")]
    [DataRow(RouteConstants.LogsPage, typeof(LogsModel), "Application logs viewer loaded")]
    public async Task PagesLogOneMeaningfulCompletionWithoutRecordContents(string path, Type category, string message)
    {
        await using var factory = new AppFactory();
        var logs = new RequestLogs();
        await using var host = factory.WithWebHostBuilder(builder => builder.ConfigureLogging(logging => logging.AddProvider(logs)));
        var now = DateTimeOffset.UtcNow;
        foreach (var trigger in new[] { ExecutionContract.Endpoint, ExecutionContract.Schedule })
            await factory.Store.SaveAsync(new($"run-{trigger}", trigger, ExecutionContract.Running, now, null,
                "private caller", "private correlation", "private summary"), CancellationToken.None);
        using var client = host.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-Role", "AgentOps.Reader");

        (await client.GetAsync(path)).StatusCode.Should().Be(HttpStatusCode.OK);

        var entry = logs.Entries.Should().ContainSingle(x => x.Category == category.FullName).Subject;
        entry.Level.Should().Be(LogLevel.Information);
        entry.Message.Should().StartWith(message);
        if (category == typeof(SchedulesModel))
        {
            entry.State["RecurringJobId"].Should().Be("insights-agent");
            entry.State["Enabled"].Should().Be(true);
            entry.State["ScheduleFound"].Should().Be(true);
        }
        if (category == typeof(SchedulesModel))
        {
            entry.State["ExecutionCount"].Should().Be(1);
            entry.State["HistoryLimit"].Should().Be(2);
        }
        logs.Entries.Should().OnlyContain(x => !x.Message.Contains("private", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public async Task LogAnalyticsReportsEffectivePagingAndBoundsWithoutDumpingTelemetry()
    {
        await using var factory = new AppFactory(new Dictionary<string, string?>
        {
            ["Insights:LogLimit"] = "3",
            ["Insights:LookbackMinutes"] = "37",
            ["Analytics:PageSize"] = "2"
        });
        factory.Insights.Logs = Enumerable.Range(0, 5).Select(index =>
            new InsightLog(DateTimeOffset.UtcNow, "2", "private telemetry", "private operation")).ToArray();
        var logs = new RequestLogs();
        await using var host = factory.WithWebHostBuilder(builder => builder.ConfigureLogging(logging => logging.AddProvider(logs)));
        using var client = host.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-Role", "AgentOps.Reader");

        (await client.GetAsync(RouteConstants.AnalyticsLogs + "?page=99")).StatusCode.Should().Be(HttpStatusCode.OK);

        var entry = logs.Entries.Should().ContainSingle(x => x.Category == typeof(AnalyticsEndpoints).FullName).Subject;
        entry.Level.Should().Be(LogLevel.Information);
        entry.State["Page"].Should().Be(2);
        entry.State["PageSize"].Should().Be(2);
        entry.State["LogCount"].Should().Be(1);
        entry.State["TotalCount"].Should().Be(3);
        entry.State["LogLimit"].Should().Be(3);
        entry.State["LookbackMinutes"].Should().Be(37);
        entry.Message.Should().NotContain("private");
    }

    [TestMethod]
    [DataRow(RouteConstants.IndexPage, typeof(IndexModel))]
    [DataRow(RouteConstants.AnalyticsOverview, typeof(AnalyticsEndpoints))]
    [DataRow(RouteConstants.AnalyticsLogs, typeof(AnalyticsEndpoints))]
    public async Task UnconfiguredAndFailedTelemetryNeverLogSuccessfulLoads(string path, Type category)
    {
        await using var factory = new AppFactory();
        var logs = new RequestLogs();
        await using var host = factory.WithWebHostBuilder(builder => builder.ConfigureLogging(logging => logging.AddProvider(logs)));
        using var client = host.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-Role", "AgentOps.Reader");
        factory.Insights.IsConfigured = false;

        var unconfigured = await client.GetAsync(path);

        unconfigured.StatusCode.Should().Be(category == typeof(IndexModel) ? HttpStatusCode.OK : HttpStatusCode.ServiceUnavailable);
        var skipped = logs.Entries.Should().ContainSingle(x => x.Category == category.FullName).Subject;
        skipped.Level.Should().Be(LogLevel.Information);
        skipped.Message.Should().Contain("not configured");
        logs.Entries.Clear();
        factory.Insights.IsConfigured = true;
        factory.Insights.Fail = true;

        var failed = await client.GetAsync(path);

        failed.StatusCode.Should().Be(category == typeof(IndexModel) ? HttpStatusCode.OK : HttpStatusCode.InternalServerError);
        logs.Entries.Should().Contain(x => x.Level == LogLevel.Error && x.Exception is InvalidOperationException);
        logs.Entries.Should().NotContain(x => x.Level == LogLevel.Information && x.Message.Contains("loaded"));
    }

    [TestMethod]
    public async Task RejectedRequestsDoNotEmitSuccessfulApplicationEvents()
    {
        await using var factory = new AppFactory();
        var logs = new RequestLogs();
        await using var host = factory.WithWebHostBuilder(builder => builder.ConfigureLogging(logging => logging.AddProvider(logs)));
        using var client = host.CreateClient();
        (await client.GetAsync(RouteConstants.SchedulesPage)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        client.DefaultRequestHeaders.Add("X-Test-Role", "AgentOps.Reader");
        (await client.GetAsync(RouteConstants.AnalyticsLogs + "?page=0")).StatusCode.Should().Be(HttpStatusCode.BadRequest);

        logs.Entries.Should().NotContain(x => x.Category == typeof(SchedulesModel).FullName ||
            x.Category == typeof(AnalyticsEndpoints).FullName);
    }

    private sealed record Entry(string Category, LogLevel Level, string Message,
        IReadOnlyDictionary<string, object?> State, Exception? Exception);

    private sealed class RequestLogs : ILoggerProvider
    {
        public ConcurrentQueue<Entry> Entries { get; } = new();
        public ILogger CreateLogger(string categoryName) => new Logger(categoryName, Entries);
        public void Dispose() { }

        private sealed class Logger(string category, ConcurrentQueue<Entry> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => category.StartsWith("AgentOps.Web.", StringComparison.Ordinal);
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                if (IsEnabled(logLevel))
                    entries.Enqueue(new(category, logLevel, formatter(state, exception),
                        ((IEnumerable<KeyValuePair<string, object?>>)state!).ToDictionary(x => x.Key, x => x.Value), exception));
            }
        }
    }
}
