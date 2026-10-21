using System.Net;
using AgentOps.Web;
using AgentOps.Web.Features.Executions;
using AgentOps.Web.Features.Agent;
using AgentOps.Web.Features.Insights;
using Hangfire;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;

namespace AgentOps.Tests;

[TestClass]
public sealed class BrowserTests
{
    [TestMethod]
    [TestCategory("Browser")]
    public async Task RazorUiRendersResponsivelyAndReceivesLiveEndpointExecution()
    {
        if (Environment.GetEnvironmentVariable("AGENTOPS_BROWSER_TESTS") != "true")
            Assert.Inconclusive("Set AGENTOPS_BROWSER_TESTS=true and install Playwright Chromium to run browser verification.");
        await using var factory = new AppFactory();
        factory.UseKestrel();
        using var client = factory.CreateClient();
        var address = factory.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Channel = Environment.GetEnvironmentVariable("AGENTOPS_BROWSER_CHANNEL"),
            Headless = true
        });
        await using var context = await browser.NewContextAsync(new BrowserNewContextOptions
        {
            ExtraHTTPHeaders = new Dictionary<string, string> { ["X-Test-Role"] = "AgentOps.Operator" },
            ViewportSize = new ViewportSize { Width = 1440, Height = 1000 }
        });
        await context.AddInitScriptAsync("""
            window.executionSockets = [];
            window.executionStreams = [];
            const NativeWebSocket = window.WebSocket;
            window.WebSocket = class extends NativeWebSocket {
                constructor(...args) {
                    super(...args);
                    window.executionSockets.push(this);
                }
            };
            const NativeEventSource = window.EventSource;
            window.EventSource = class extends NativeEventSource {
                constructor(...args) {
                    super(...args);
                    window.executionStreams.push(this);
                }
            };
            """);
        var page = await context.NewPageAsync();
        await page.GotoAsync(address + "/");
        await Assertions.Expect(page.Locator(".data-source")).ToContainTextAsync("Cosmos development");
        await Assertions.Expect(page.Locator(".data-source-service")).ToHaveTextAsync("Local emulator");
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Level = 1 })).ToContainTextAsync("A clearer view.");
        await Assertions.Expect(page.Locator(".metric").First).ToContainTextAsync("42");
        await page.GetByRole(AriaRole.Navigation, new() { Name = "Primary navigation" })
            .GetByRole(AriaRole.Link, new() { Name = "Endpoint runs" }).ClickAsync();
        page.Url.Should().Be(address + RouteConstants.ExecutionsPage);
        await Assertions.Expect(page.Locator("#live-status")).ToHaveTextAsync("Live feed connected");
        var response = await context.APIRequest.PostAsync(address + "/api/executions");
        response.Status.Should().Be((int)HttpStatusCode.OK);
        var execution = System.Text.Json.JsonSerializer.Deserialize<ExecutionRecord>(await response.TextAsync(),
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))!;
        var row = page.Locator($"tr[data-execution-id='{execution.Id}']");
        await Assertions.Expect(row).ToContainTextAsync(ExecutionContract.NotImplemented);
        await Assertions.Expect(row).ToContainTextAsync("No model or action");
        var notifier = factory.Services.GetRequiredService<IExecutionNotifier>();
        foreach (var (id, minutes) in new[] { ("older-a", -1), ("older-b", -2) })
        {
            var older = execution with { Id = id, StartedAt = execution.StartedAt.AddMinutes(minutes) };
            await factory.Store.SaveAsync(older, CancellationToken.None);
            await notifier.PublishAsync(older, CancellationToken.None);
        }
        var refreshed = execution with { Summary = execution.Summary + " [live update]" };
        await factory.Store.SaveAsync(refreshed, CancellationToken.None);
        await notifier.PublishAsync(refreshed, CancellationToken.None);
        await Assertions.Expect(row).ToContainTextAsync("[live update]");
        await Assertions.Expect(page.Locator("#execution-rows tr")).ToHaveCountAsync(2);
        await Assertions.Expect(page.Locator("#execution-rows tr").First).ToHaveAttributeAsync("data-execution-id", execution.Id);
        await Assertions.Expect(page.Locator("#execution-rows")).Not.ToContainTextAsync("older-b");
        await context.SetOfflineAsync(true);
        await page.EvaluateAsync("""
            () => {
                window.executionSockets.forEach(socket => socket.close());
                window.executionStreams.forEach(stream => {
                    stream.close();
                    stream.dispatchEvent(new Event("error"));
                });
            }
            """);
        await Assertions.Expect(page.Locator("#live-status")).ToHaveTextAsync("Reconnecting live feed...");
        var missed = refreshed with { Summary = "<img src=x onerror=alert(1)> [recovered from history]" };
        await factory.Store.SaveAsync(missed, CancellationToken.None);
        await context.SetOfflineAsync(false);
        await Assertions.Expect(page.Locator("#live-status")).ToHaveTextAsync("Live feed connected");
        await Assertions.Expect(row).ToContainTextAsync("[recovered from history]");
        await Assertions.Expect(row.Locator("img")).ToHaveCountAsync(0);
        await page.SetViewportSizeAsync(390, 844);
        var overflow = await page.EvaluateAsync<bool>("document.documentElement.scrollWidth > window.innerWidth");
        overflow.Should().BeFalse("horizontal scrolling must be contained inside the history table, not the page");
        await page.GotoAsync(address + RouteConstants.SchedulesPage);
        await Assertions.Expect(page.Locator("[data-execution-feed='Schedule']")).Not.ToContainTextAsync(execution.Id);
        await page.GotoAsync(address + RouteConstants.LogsPage);
        await Assertions.Expect(page.Locator(".log-message")).ToContainTextAsync("<script>alert('test')</script>");
        if (Environment.GetEnvironmentVariable("AGENTOPS_BROWSER_SCREENSHOT") is { Length: > 0 } screenshot)
        {
            await page.SetViewportSizeAsync(1440, 1000);
            await page.GotoAsync(address + "/");
            await page.ScreenshotAsync(new PageScreenshotOptions { Path = screenshot, FullPage = true, Animations = ScreenshotAnimations.Disabled });
        }
    }

    [TestMethod]
    [TestCategory("Browser")]
    public async Task HangfireRunShowsRunningAndTerminalStateInSchedulesBrowserFeed()
    {
        if (Environment.GetEnvironmentVariable("AGENTOPS_BROWSER_TESTS") != "true")
            Assert.Inconclusive("Set AGENTOPS_BROWSER_TESTS=true and install a Playwright browser to run browser verification.");
        var runner = new ControlledRunner();
        await using var factory = new AppFactory(runner: runner);
        factory.UseKestrel();
        using var client = factory.CreateClient();
        var address = factory.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Channel = Environment.GetEnvironmentVariable("AGENTOPS_BROWSER_CHANNEL"),
            Headless = true
        });
        await using var context = await browser.NewContextAsync(new BrowserNewContextOptions
        {
            ExtraHTTPHeaders = new Dictionary<string, string> { ["X-Test-Role"] = "AgentOps.Reader" }
        });
        var page = await context.NewPageAsync();
        await page.GotoAsync(address + RouteConstants.SchedulesPage);
        await Assertions.Expect(page.Locator("#live-status")).ToHaveTextAsync("Live feed connected");
        factory.Services.GetRequiredService<IRecurringJobManager>().Trigger("insights-agent");
        try
        {
            await Assertions.Expect(page.Locator("#execution-rows tr")).ToHaveCountAsync(1);
            await Assertions.Expect(page.Locator("#execution-rows .badge")).ToHaveTextAsync(ExecutionContract.Running);
        }
        finally
        {
            runner.Complete.TrySetResult(new AgentOutcome(ExecutionContract.NotImplemented, "<script>unsafe</script> scheduled result"));
        }
        await Assertions.Expect(page.Locator("#execution-rows .badge")).ToHaveTextAsync(ExecutionContract.NotImplemented);
        await Assertions.Expect(page.Locator("#execution-rows .summary")).ToHaveTextAsync("<script>unsafe</script> scheduled result");
        await Assertions.Expect(page.Locator("#execution-rows script")).ToHaveCountAsync(0);
        factory.Store.Writes.Select(record => record.Status).Should().Equal(ExecutionContract.Running, ExecutionContract.NotImplemented);
    }

    [TestMethod]
    [TestCategory("Browser")]
    public async Task VueAnalyticsPagesNavigateBoundedHistoryAndLogsAndSurfaceFailures()
    {
        if (Environment.GetEnvironmentVariable("AGENTOPS_BROWSER_TESTS") != "true")
            Assert.Inconclusive("Set AGENTOPS_BROWSER_TESTS=true and install Playwright Chromium to run browser verification.");
        await using var factory = new AppFactory(new Dictionary<string, string?>
        {
            ["Executions:HistoryLimit"] = "5", ["Insights:LogLimit"] = "3", ["Analytics:PageSize"] = "2"
        });
        var start = DateTimeOffset.UtcNow;
        for (var index = 0; index < 6; index++)
        {
            await factory.Store.SaveAsync(new($"paged-{index}", ExecutionContract.Endpoint, ExecutionContract.NotImplemented,
                start.AddMinutes(-index), start.AddMinutes(-index), "test", $"correlation-{index}", $"Outcome {index}"), CancellationToken.None);
        }
        factory.Insights.Logs = Enumerable.Range(0, 4).Select(index =>
            new InsightLog(start.AddMinutes(-index), "2", $"<script>trace-{index}</script>", $"operation-{index}")).ToArray();
        factory.UseKestrel();
        using var client = factory.CreateClient();
        var address = factory.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Channel = Environment.GetEnvironmentVariable("AGENTOPS_BROWSER_CHANNEL"), Headless = true
        });
        await using var context = await browser.NewContextAsync(new BrowserNewContextOptions
        {
            ExtraHTTPHeaders = new Dictionary<string, string> { ["X-Test-Role"] = "AgentOps.Reader" }
        });
        var page = await context.NewPageAsync();
        var errors = new List<string>();
        page.PageError += (_, error) => errors.Add(error);
        await page.GotoAsync(address + RouteConstants.ExecutionsPage);
        await Assertions.Expect(page.Locator("#live-status")).ToHaveTextAsync("Live feed connected");
        await Assertions.Expect(page.Locator("#execution-rows tr")).ToHaveCountAsync(2);
        await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { Name = "Previous" })).ToBeDisabledAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Next", Exact = true }).ClickAsync();
        await Assertions.Expect(page.Locator("#execution-rows tr").First).ToHaveAttributeAsync("data-execution-id", "paged-2");
        await Assertions.Expect(page.Locator(".page-controls")).ToContainTextAsync("Page 2 of 3");

        var live = new ExecutionRecord("newest", ExecutionContract.Endpoint, ExecutionContract.NotImplemented,
            start.AddMinutes(1), start.AddMinutes(1), "test", "live-correlation", "<img src=x onerror=alert(1)>");
        await factory.Store.SaveAsync(live, CancellationToken.None);
        await factory.Services.GetRequiredService<IExecutionNotifier>().PublishAsync(live, CancellationToken.None);
        await Assertions.Expect(page.Locator("#execution-rows tr").First).ToHaveAttributeAsync("data-execution-id", "paged-1");
        await Assertions.Expect(page.Locator(".page-controls")).ToContainTextAsync("Page 2 of 3");
        await page.GetByRole(AriaRole.Button, new() { Name = "Next", Exact = true }).ClickAsync();
        await Assertions.Expect(page.Locator("#execution-rows tr")).ToHaveCountAsync(1);
        await Assertions.Expect(page.Locator("#execution-rows tr").First).ToHaveAttributeAsync("data-execution-id", "paged-3");
        await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { Name = "Next", Exact = true })).ToBeDisabledAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Previous" }).ClickAsync();
        await Assertions.Expect(page.Locator(".page-controls")).ToContainTextAsync("Page 2 of 3");
        await page.GetByRole(AriaRole.Button, new() { Name = "Previous" }).ClickAsync();
        await Assertions.Expect(page.Locator("#execution-rows .summary").First).ToHaveTextAsync("<img src=x onerror=alert(1)>");
        await Assertions.Expect(page.Locator("#execution-rows img")).ToHaveCountAsync(0);

        await page.GotoAsync(address + RouteConstants.LogsPage);
        await Assertions.Expect(page.Locator("[data-logs][data-v-app] .log-message")).ToHaveCountAsync(2);
        await page.GetByRole(AriaRole.Button, new() { Name = "Next", Exact = true }).ClickAsync();
        await Assertions.Expect(page.Locator(".log-message")).ToHaveTextAsync(new[] { "<script>trace-2</script>" });
        await Assertions.Expect(page.Locator("[data-logs] script")).ToHaveCountAsync(0);
        await Assertions.Expect(page.Locator(".results-count")).ToContainTextAsync("3 of 3");
        await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { Name = "Next", Exact = true })).ToBeDisabledAsync();

        factory.Insights.Fail = true;
        await page.GetByRole(AriaRole.Button, new() { Name = "Refresh", Exact = true }).ClickAsync();
        await Assertions.Expect(page.Locator("[data-logs] [role=alert]")).ToContainTextAsync("could not be loaded");
        await Assertions.Expect(page.Locator("[data-logs]")).Not.ToContainTextAsync("private telemetry details");
        factory.Insights.Fail = false;
        await page.GetByRole(AriaRole.Button, new() { Name = "Retry" }).ClickAsync();
        await Assertions.Expect(page.Locator("[data-logs] .flow-panel")).ToHaveAttributeAsync("aria-busy", "false");
        await Assertions.Expect(page.Locator("[data-logs] [role=alert]")).ToHaveCountAsync(0);
        await Assertions.Expect(page.Locator(".log-message")).ToHaveTextAsync(new[] { "<script>trace-2</script>" });
        factory.Insights.Logs = [];
        await page.GetByRole(AriaRole.Button, new() { Name = "Refresh", Exact = true }).ClickAsync();
        await Assertions.Expect(page.Locator("[data-logs] .empty-state")).ToHaveTextAsync("No traces in the configured time window.");
        await Assertions.Expect(page.Locator(".log-message")).ToHaveCountAsync(0);
        await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { Name = "Previous" })).ToBeDisabledAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { Name = "Next", Exact = true })).ToBeDisabledAsync();

        factory.Insights.Fail = true;
        await page.GotoAsync(address + RouteConstants.IndexPage);
        await Assertions.Expect(page.Locator("[data-overview][data-v-app] [role=alert]")).ToContainTextAsync("could not be loaded");
        await Assertions.Expect(page.Locator("[data-overview]")).Not.ToContainTextAsync("private telemetry details");
        await Assertions.Expect(page.Locator("[data-overview] .metric")).ToHaveCountAsync(0);
        factory.Insights.Fail = false;
        await page.GetByRole(AriaRole.Button, new() { Name = "Retry" }).ClickAsync();
        await Assertions.Expect(page.Locator("[data-overview][data-v-app] .metric").First).ToContainTextAsync("42");
        factory.Insights.IsConfigured = false;
        await page.GotoAsync(address + RouteConstants.LogsPage);
        await Assertions.Expect(page.Locator("[data-logs][data-v-app] [role=alert]")).ToContainTextAsync("Insights:ResourceId");
        errors.Should().BeEmpty("Vue must mount and update without browser runtime errors");

        foreach (var route in new[] { RouteConstants.IndexPage, RouteConstants.SchedulesPage, RouteConstants.ExecutionsPage, RouteConstants.LogsPage, RouteConstants.ErrorPage })
        {
            await page.SetViewportSizeAsync(390, 844);
            await page.GotoAsync(address + route);
            (await page.EvaluateAsync<bool>("document.documentElement.scrollWidth > window.innerWidth")).Should().BeFalse(route);
        }
    }

    private sealed class ControlledRunner : IAgentRunner
    {
        public TaskCompletionSource<AgentOutcome> Complete { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<AgentOutcome> RunAsync(CancellationToken cancellationToken) => Complete.Task.WaitAsync(cancellationToken);
    }
}
