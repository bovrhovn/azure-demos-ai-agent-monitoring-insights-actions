using System.Collections.Concurrent;
using System.Net;
using AgentOps.Web;
using AgentOps.Web.Features.Executions;
using AgentOps.Web.Features.Insights;
using AgentOps.Web.Pages.Analytics;
using AgentOps.Web.Pages.Info;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;

namespace AgentOps.Tests;

[TestClass]
public sealed class RequestLoggingTests
{
    [TestMethod]
    [DataRow(RouteConstants.SchedulesPage, typeof(SchedulesModel), "Schedules page loaded")]
    [DataRow(RouteConstants.ExecutionsPage, typeof(ExecutionsModel), "Executions page loaded")]
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
        if (category == typeof(SchedulesModel) || category == typeof(ExecutionsModel))
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
            ["Insights:LogLimit"] = "3", ["Insights:LookbackMinutes"] = "37", ["Analytics:PageSize"] = "2"
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
