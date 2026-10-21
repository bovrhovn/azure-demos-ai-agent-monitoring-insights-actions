using System.Net;
using System.Net.Http.Json;
using AgentOps.Web;
using AgentOps.Web.Features.Executions;
using AgentOps.Web.Features.Insights;
using FluentAssertions;

namespace AgentOps.Tests;

[TestClass]
public sealed class AnalyticsTests
{
    [TestMethod]
    [DataRow(RouteConstants.AnalyticsOverview)]
    [DataRow(RouteConstants.AnalyticsSchedule)]
    [DataRow(RouteConstants.AnalyticsLogs)]
    [DataRow(RouteConstants.AnalyticsExecutions + "?trigger=Endpoint")]
    public async Task AnalyticsRequiresReaderOrOperatorSession(string path)
    {
        await using var factory = new AppFactory();
        using var anonymous = factory.Client();
        using var unrelated = factory.Client("Unrelated.Role");
        using var reader = factory.Client("AgentOps.Reader");
        using var account = factory.Client("AgentOps.Operator");
        (await anonymous.GetAsync(path)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await unrelated.GetAsync(path)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await reader.GetAsync(path)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await account.GetAsync(path)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [TestMethod]
    [DataRow(ExecutionContract.Endpoint)]
    [DataRow(ExecutionContract.Schedule)]
    public async Task ExecutionPagingIsNewestFirstBoundedAndTriggerSpecific(string trigger)
    {
        await using var factory = new AppFactory(new Dictionary<string, string?>
        {
            ["Executions:HistoryLimit"] = "5", ["Analytics:PageSize"] = "2", ["Analytics:MaxPageSize"] = "3"
        });
        var start = DateTimeOffset.UtcNow;
        for (var index = 0; index < 7; index++)
        {
            var record = new ExecutionRecord($"run-{index}", trigger, ExecutionContract.NotImplemented,
                start.AddMinutes(-index), start.AddMinutes(-index), "test", $"correlation-{index}", "<script>escaped</script>");
            await factory.Store.SaveAsync(record, CancellationToken.None);
            await factory.Store.SaveAsync(record with { Id = $"other-{index}",
                Trigger = trigger == ExecutionContract.Endpoint ? ExecutionContract.Schedule : ExecutionContract.Endpoint }, CancellationToken.None);
        }
        using var client = factory.Client("AgentOps.Reader");
        var url = $"{RouteConstants.AnalyticsExecutions}?trigger={trigger}";
        var first = (await client.GetFromJsonAsync<AnalyticsPage<ExecutionRecord>>(url))!;
        first.Items.Select(x => x.Id).Should().Equal("run-0", "run-1");
        first.Items.Should().OnlyContain(x => x.Trigger == trigger);
        first.Page.Should().Be(1);
        first.PageSize.Should().Be(2);
        first.TotalCount.Should().Be(5);
        first.TotalPages.Should().Be(3);
        first.Limit.Should().Be(5);
        var second = (await client.GetFromJsonAsync<AnalyticsPage<ExecutionRecord>>(url + "&page=2"))!;
        second.Items.Select(x => x.Id).Should().Equal("run-2", "run-3");
        var last = (await client.GetFromJsonAsync<AnalyticsPage<ExecutionRecord>>(url + "&page=2147483647"))!;
        last.Page.Should().Be(3);
        last.Items.Select(x => x.Id).Should().Equal("run-4");
        var custom = (await client.GetFromJsonAsync<AnalyticsPage<ExecutionRecord>>(url + "&pageSize=3&page=2"))!;
        custom.PageSize.Should().Be(3);
        custom.Items.Select(x => x.Id).Should().Equal("run-3", "run-4");
    }

    [TestMethod]
    [DataRow(RouteConstants.AnalyticsExecutions + "?trigger=Endpoint&page=0")]
    [DataRow(RouteConstants.AnalyticsExecutions + "?trigger=Endpoint&page=-1")]
    [DataRow(RouteConstants.AnalyticsExecutions + "?trigger=Endpoint&pageSize=0")]
    [DataRow(RouteConstants.AnalyticsExecutions + "?trigger=Endpoint&pageSize=51")]
    [DataRow(RouteConstants.AnalyticsExecutions + "?trigger=Endpoint&page=bad")]
    [DataRow(RouteConstants.AnalyticsExecutions + "?trigger=Unknown")]
    [DataRow(RouteConstants.AnalyticsLogs + "?page=-1")]
    [DataRow(RouteConstants.AnalyticsLogs + "?pageSize=0")]
    [DataRow(RouteConstants.AnalyticsLogs + "?pageSize=51")]
    [DataRow(RouteConstants.AnalyticsLogs + "?page=bad")]
    public async Task InvalidPagingOrTriggerReturnsBadRequest(string path)
    {
        await using var factory = new AppFactory();
        using var client = factory.Client("AgentOps.Reader");
        (await client.GetAsync(path)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [TestMethod]
    public async Task EmptyHistoryHasAnEmptyFirstPage()
    {
        await using var factory = new AppFactory();
        using var client = factory.Client("AgentOps.Reader");
        var page = (await client.GetFromJsonAsync<AnalyticsPage<ExecutionRecord>>(
            RouteConstants.AnalyticsExecutions + "?trigger=Endpoint&page=99"))!;
        page.Items.Should().BeEmpty();
        page.TotalCount.Should().Be(0);
        page.Page.Should().Be(1);
        page.TotalPages.Should().Be(1);
    }

    [TestMethod]
    public async Task LogsUseTheirOwnBoundAndConfiguredPageSize()
    {
        await using var factory = new AppFactory(new Dictionary<string, string?>
        {
            ["Insights:LogLimit"] = "3", ["Analytics:PageSize"] = "2"
        });
        factory.Insights.Logs = Enumerable.Range(0, 5).Select(index =>
            new InsightLog(DateTimeOffset.UtcNow.AddMinutes(-index), "2", $"Message {index}", $"operation-{index}")).ToArray();
        using var client = factory.Client("AgentOps.Reader");
        using var response = await client.GetAsync(RouteConstants.AnalyticsLogs + "?page=2");
        response.Headers.CacheControl!.NoStore.Should().BeTrue("refreshing traces must not reuse cached telemetry");
        var page = (await response.Content.ReadFromJsonAsync<AnalyticsPage<InsightLog>>())!;
        page.Items.Select(x => x.Message).Should().Equal("Message 2");
        page.TotalCount.Should().Be(3);
        page.TotalPages.Should().Be(2);
        page.PageSize.Should().Be(2);
        page.Limit.Should().Be(3);
    }

    [TestMethod]
    [DataRow(RouteConstants.LogsPage)]
    [DataRow(RouteConstants.LegacyLogsPage)]
    public async Task LogsRazorPageOnlyHostsVueAndQueriesTelemetryThroughMinimalApi(string route)
    {
        await using var factory = new AppFactory();
        factory.Insights.Fail = true;
        using var client = factory.Client("AgentOps.Reader");
        var html = await client.GetStringAsync(route);
        html.Should().Contain("data-logs").And.Contain("Loading the application logs viewer")
            .And.Contain($"data-analytics-logs=\"{RouteConstants.AnalyticsLogs}\"")
            .And.NotContain("<table").And.NotContain("<tbody").And.NotContain("private telemetry details");
        factory.Insights.LogQueries.Should().Be(0, "Razor must not query or render telemetry");
        var failed = await client.GetAsync(RouteConstants.AnalyticsLogs);
        failed.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        factory.Insights.LogQueries.Should().Be(1);
        factory.Insights.Fail = false;
        var page = (await client.GetFromJsonAsync<AnalyticsPage<InsightLog>>(RouteConstants.AnalyticsLogs))!;
        page.Items.Should().ContainSingle().Which.Message.Should().Be("<script>alert('test')</script>");
        factory.Insights.LogQueries.Should().Be(2);
    }

    [TestMethod]
    [DataRow(RouteConstants.AnalyticsOverview)]
    [DataRow(RouteConstants.AnalyticsLogs)]
    public async Task TelemetryDistinguishesUnconfiguredFailedAndEmptyResults(string path)
    {
        await using var factory = new AppFactory();
        using var client = factory.Client("AgentOps.Reader");
        factory.Insights.IsConfigured = false;
        var unconfigured = await client.GetAsync(path);
        unconfigured.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        (await unconfigured.Content.ReadAsStringAsync()).Should().Contain("Insights:ResourceId");
        factory.Insights.IsConfigured = true;
        factory.Insights.Fail = true;
        var failed = await client.GetAsync(path);
        failed.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        (await failed.Content.ReadAsStringAsync()).Should().Contain("traceId")
            .And.NotContain("private telemetry details").And.NotContain("\"requests\":0");
        factory.Insights.Fail = false;
        factory.Insights.Logs = [];
        factory.Insights.Overview = new(0, 0, 0, 0);
        var empty = await client.GetAsync(path);
        empty.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await empty.Content.ReadAsStringAsync();
        if (path == RouteConstants.AnalyticsOverview)
            json.Should().Contain("\"requests\":0").And.Contain("\"lookbackMinutes\":60");
        else
            json.Should().Contain("\"items\":[]").And.Contain("\"totalCount\":0");
    }

    [TestMethod]
    public async Task ScheduleApiReportsConfigurationAndVolatileDispatchTimes()
    {
        await using var factory = new AppFactory();
        using var client = factory.Client("AgentOps.Reader");
        var schedule = await client.GetStringAsync(RouteConstants.AnalyticsSchedule);
        schedule.Should().Contain("\"recurringJobId\":\"insights-agent\"")
            .And.Contain("\"enabled\":true").And.Contain("\"cron\":\"0 0 1 1 *\"")
            .And.Contain("\"nextExecution\":").And.Contain("\"lastExecution\":null");
    }
}
