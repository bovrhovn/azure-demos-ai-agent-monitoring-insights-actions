using System.Net;
using System.Net.Http.Json;
using AgentOps.Web.Features.Executions;
using AgentOps.Web.Features.Scheduling;
using FluentAssertions;
using Moq;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Hangfire;

namespace AgentOps.Tests;

[TestClass]
public sealed class WebTests
{
    [TestMethod]
    [DataRow("/")]
    [DataRow("/Schedules")]
    [DataRow("/Executions")]
    [DataRow("/Logs")]
    [DataRow("/Info/Index")]
    [DataRow("/Info/Error")]
    [DataRow("/Analytics/Schedules")]
    [DataRow("/Analytics/Executions")]
    [DataRow("/Analytics/Logs")]
    [DataRow("/ui/executions?trigger=Endpoint")]
    [DataRow("/hubs/executions/negotiate?negotiateVersion=1")]
    public async Task ProtectedSurfacesRejectAnonymousUsers(string path)
    {
        await using var factory = new AppFactory();
        using var client = factory.Client();
        using var response = path.Contains("negotiate", StringComparison.Ordinal)
            ? await client.PostAsync(path, null)
            : await client.GetAsync(path);
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [TestMethod]
    public async Task ApiRejectsAnonymousAndReaderButAllowsOperator()
    {
        await using var factory = new AppFactory();
        using var anonymous = factory.Client();
        using var reader = factory.Client("AgentOps.Reader");
        using var account = factory.Client("AgentOps.Operator");
        (await anonymous.PostAsync("/api/executions", null)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await reader.PostAsync("/api/executions", null)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var response = await account.PostAsync("/api/executions", null);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var run = await response.Content.ReadFromJsonAsync<ExecutionRecord>();
        run!.Status.Should().Be(ExecutionContract.Completed);
        run.Trigger.Should().Be(ExecutionContract.Endpoint);
        run.RequestedBy.Should().Be("test-caller");
        (await account.GetFromJsonAsync<ExecutionRecord[]>("/api/executions"))!.Should().ContainSingle(x => x.Id == run.Id);
        (await account.GetFromJsonAsync<ExecutionRecord>($"/api/executions/{run.Id}"))!.Should().Be(run);
        (await account.GetAsync("/api/executions/missing")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [TestMethod]
    public async Task SchedulePageShowsVolatileHangfireStatusWithoutAnAdministrativeDashboard()
    {
        await using var factory = new AppFactory();
        using var reader = factory.Client("AgentOps.Reader");
        var response = await reader.GetAsync("/Schedules");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var html = await response.Content.ReadAsStringAsync();
        html.Should().Contain("HANGFIRE IN-MEMORY").And.Contain("lost on restart").And.Contain("0 0 1 1 *");
        using var anonymous = factory.Client();
        (await anonymous.GetAsync("/hangfire")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await reader.GetAsync("/hangfire")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [TestMethod]
    public async Task ReaderPagesRenderActualMetricsVueLogsShellAndSeparateHistories()
    {
        await using var factory = new AppFactory();
        using var client = factory.Client("AgentOps.Reader");
        var endpointRun = new ExecutionRecord("endpoint-id", ExecutionContract.Endpoint, ExecutionContract.NotImplemented, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "test", "correlation-endpoint", "Endpoint only");
        await factory.Store.SaveAsync(endpointRun, CancellationToken.None);
        await factory.Store.SaveAsync(endpointRun with { Id = "schedule-id", Trigger = ExecutionContract.Schedule, Summary = "Schedule only" }, CancellationToken.None);

        var overview = await client.GetStringAsync("/");
        overview.Should().Contain("TOTAL REQUESTS").And.Contain(">42<").And.Contain(">125<");
        var logs = await client.GetStringAsync("/Logs");
        logs.Should().Contain("data-logs").And.Contain("Enable JavaScript")
            .And.NotContain("<table").And.NotContain("<script>alert");
        var endpoints = await client.GetStringAsync("/Executions");
        endpoints.Should().Contain("Endpoint only").And.NotContain("Schedule only");
        var schedules = await client.GetStringAsync("/Schedules");
        schedules.Should().Contain("Schedule only").And.NotContain("Endpoint only");
        schedules.Should().Contain("0 0 1 1 *");
    }

    [TestMethod]
    public async Task QueryErrorsAreVisibleAndNeverAppearAsZeroMetrics()
    {
        await using var factory = new AppFactory();
        factory.Insights.Fail = true;
        using var client = factory.Client("AgentOps.Reader");
        var html = await client.GetStringAsync("/");
        html.Should().Contain("could not be queried").And.NotContain("private telemetry details").And.NotContain(">42<");
    }

    [TestMethod]
    public async Task HealthProbeIsAnonymousAndUnknownTriggerIsValidationError()
    {
        await using var factory = new AppFactory();
        using var anonymous = factory.Client();
        (await anonymous.GetStringAsync("/health/live")).Should().Be("Healthy");
        using var reader = factory.Client("AgentOps.Reader");
        var response = await reader.GetAsync("/ui/executions?trigger=bad");
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("Use Endpoint or Schedule");
    }

    [TestMethod]
    public async Task HubRejectsCrossOriginConnectionsEvenForAuthenticatedUsers()
    {
        await using var factory = new AppFactory();
        using var client = factory.Client("AgentOps.Reader");
        client.DefaultRequestHeaders.Add("Origin", "https://untrusted.example");
        (await client.PostAsync("/hubs/executions/negotiate?negotiateVersion=1", null)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        client.DefaultRequestHeaders.Remove("Origin");
        client.DefaultRequestHeaders.Add("Origin", "https://localhost");
        (await client.PostAsync("/hubs/executions/negotiate?negotiateVersion=1", null)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [TestMethod]
    public async Task HangfireDispatchesRegisteredRecurringJobThroughSharedPipeline()
    {
        await using var factory = new AppFactory();
        using var client = factory.Client("AgentOps.Reader");
        await client.GetAsync("/health/live");
        factory.Services.GetRequiredService<IRecurringJobManager>().Trigger("insights-agent");
        var run = await factory.Store.ScheduledCompletion.Task.WaitAsync(TimeSpan.FromSeconds(20));
        run.Status.Should().Be(ExecutionContract.Completed);
        run.Trigger.Should().Be(ExecutionContract.Schedule);
        run.RequestedBy.Should().Be("hangfire");
        factory.Store.Writes.Select(record => record.Status).Should()
            .Equal(ExecutionContract.Running, ExecutionContract.Completed);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task BothTriggersDeliverRunningAndTerminalRecordsToConnectedSignalRViewer(bool scheduled)
    {
        await using var factory = new AppFactory();
        using var client = factory.Client("AgentOps.Operator");
        await client.GetAsync("/health/live");
        var started = new TaskCompletionSource<ExecutionRecord>(TaskCreationOptions.RunContinuationsAsynchronously);
        var completed = new TaskCompletionSource<ExecutionRecord>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var hub = new HubConnectionBuilder().WithUrl("https://localhost/hubs/executions", options =>
        {
            options.Transports = HttpTransportType.LongPolling;
            options.Headers.Add("X-Test-Role", "AgentOps.Reader");
            options.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler();
        }).Build();
        hub.On<ExecutionRecord>(ExecutionContract.ChangedEvent, record =>
        {
            if (record.Status == ExecutionContract.Running) started.TrySetResult(record);
            if (record.Status == ExecutionContract.Completed) completed.TrySetResult(record);
        });
        await hub.StartAsync();
        ExecutionRecord? result;
        if (scheduled)
        {
            factory.Services.GetRequiredService<IRecurringJobManager>().Trigger("insights-agent");
            result = await factory.Store.ScheduledCompletion.Task.WaitAsync(TimeSpan.FromSeconds(20));
        }
        else
        {
            var response = await client.PostAsync("/api/executions", null);
            result = await response.Content.ReadFromJsonAsync<ExecutionRecord>();
        }
        (await started.Task.WaitAsync(TimeSpan.FromSeconds(5))).Id.Should().Be(result!.Id);
        (await completed.Task.WaitAsync(TimeSpan.FromSeconds(5))).Should().Be(result);
        await hub.StopAsync();
    }

    [TestMethod]
    public async Task EndpointEnforcesConfiguredRateLimit()
    {
        await using var factory = new AppFactory();
        using var client = factory.Client("AgentOps.Operator");
        (await client.PostAsync("/api/executions", null)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.PostAsync("/api/executions", null)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.PostAsync("/api/executions", null)).StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        factory.Store.Writes.Should().HaveCount(4);
    }
}
