using AgentOps.Web;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace AgentOps.Tests;

[TestClass]
public sealed class RouteTests
{
    [TestMethod]
    [DataRow(RouteConstants.Health)]
    [DataRow(RouteConstants.Liveness)]
    [DataRow(RouteConstants.Readiness)]
    public async Task HealthRoutesAreAnonymousDespiteAuthenticationFallback(string route)
    {
        await using var factory = new AppFactory();
        using var client = factory.Client();
        using var response = await client.GetAsync(route);
        response.IsSuccessStatusCode.Should().BeTrue();
        (await response.Content.ReadAsStringAsync()).Should().Be("Healthy");
        response.Headers.Location.Should().BeNull();
    }

    [TestMethod]
    public async Task BrowserRoutesAreRenderedFromServerConstants()
    {
        await using var factory = new AppFactory();
        using var client = factory.Client("AgentOps.Reader");
        var html = await client.GetStringAsync(RouteConstants.ExecutionsPage);
        html.Should().Contain($"data-execution-hub=\"{RouteConstants.ExecutionHub}\"")
            .And.Contain($"data-execution-history=\"{RouteConstants.ExecutionHistory}\"")
            .And.Contain($"data-analytics-executions=\"{RouteConstants.AnalyticsExecutions}\"")
            .And.Contain($"data-analytics-overview=\"{RouteConstants.AnalyticsOverview}\"")
            .And.Contain($"data-analytics-logs=\"{RouteConstants.AnalyticsLogs}\"")
            .And.Contain($"data-analytics-schedule=\"{RouteConstants.AnalyticsSchedule}\"")
            .And.Contain($"href=\"{RouteConstants.SchedulesPage}\"")
            .And.Contain($"href=\"{RouteConstants.ExecutionsPage}\"")
            .And.Contain($"href=\"{RouteConstants.LogsPage}\"");
        var endpoints = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>();
        endpoints.Should().Contain(x => x.RoutePattern.RawText == RouteConstants.ExecutionHub);
        endpoints.Should().Contain(x => x.RoutePattern.RawText == RouteConstants.ExecutionApi + RouteConstants.Root);
    }

    [TestMethod]
    [DataRow("/", "/Pages/Info/Index.cshtml", "Overview")]
    [DataRow("/Index", "/Pages/Info/Index.cshtml", "Overview")]
    [DataRow("/Info/Index", "/Pages/Info/Index.cshtml", "Overview")]
    [DataRow("/Error", "/Pages/Info/Error.cshtml", "Something went wrong.")]
    [DataRow("/Info/Error", "/Pages/Info/Error.cshtml", "Something went wrong.")]
    [DataRow("/Schedules", "/Pages/Analytics/Schedules.cshtml", "Schedules")]
    [DataRow("/Analytics/Schedules", "/Pages/Analytics/Schedules.cshtml", "Schedules")]
    [DataRow("/Executions", "/Pages/Analytics/Executions.cshtml", "Endpoint runs")]
    [DataRow("/Analytics/Executions", "/Pages/Analytics/Executions.cshtml", "Endpoint runs")]
    [DataRow("/Logs", "/Pages/Analytics/Logs.cshtml", "Application logs")]
    [DataRow("/Analytics/Logs", "/Pages/Analytics/Logs.cshtml", "Application logs")]
    public async Task NewAndLegacyRoutesResolveToRelocatedRazorPages(string route, string file, string heading)
    {
        await using var factory = new AppFactory();
        using var client = factory.Client("AgentOps.Reader");
        var html = await client.GetStringAsync(route);
        html.Should().Contain(heading).And.Contain("Agent Observatory");
        html.Should().NotContain("publication-bar")
            .And.NotContain("AZURE / AGENT OPERATIONS")
            .And.NotContain("Observe. Understand. Act.")
            .And.MatchRegex("<div class=\"workspace\">\\s*<main", "the removed banner must not leave an empty row");
        var endpoints = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>();
        endpoints.Should().Contain(endpoint => endpoint.RoutePattern.RawText!.TrimStart('/') == route.TrimStart('/') &&
            endpoint.Metadata.GetMetadata<PageActionDescriptor>()!.RelativePath == file);
    }
}
