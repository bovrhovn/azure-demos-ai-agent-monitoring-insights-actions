using System.Net;
using AgentOps.Web;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace AgentOps.Tests;

[TestClass]
public sealed class ForwardedHeadersTests
{
    [TestMethod]
    public async Task ContainerAppsRestoresExternalUrlBeforeSignalROriginValidation()
    {
        await using var factory = new AppFactory(new Dictionary<string, string?>
        {
            ["Hosting:BehindContainerAppsIngress"] = "true",
            ["AllowedHosts"] = "localhost;observatory.example"
        });
        using var client = factory.Client();
        var result = await factory.Server.SendAsync(context =>
        {
            context.Connection.RemoteIpAddress = IPAddress.Parse("10.0.0.5");
            context.Request.Scheme = "http";
            context.Request.Host = new HostString("localhost");
            context.Request.Method = "POST";
            context.Request.Path = "/hubs/executions/negotiate";
            context.Request.QueryString = new QueryString("?negotiateVersion=1");
            context.Request.Headers["X-Test-Role"] = "AgentOps.Reader";
            context.Request.Headers["Origin"] = "https://observatory.example";
            context.Request.Headers["X-Forwarded-Proto"] = "https";
            context.Request.Headers["X-Forwarded-Host"] = "observatory.example";
            context.Request.Headers["X-Forwarded-For"] = "203.0.113.10";
        });
        result.Response.StatusCode.Should().Be(200);
        result.Request.Scheme.Should().Be("https");
        result.Request.Host.Value.Should().Be("observatory.example");
        result.Connection.RemoteIpAddress.Should().Be(IPAddress.Parse("203.0.113.10"));
    }

    [TestMethod]
    [DataRow("10.0.0.5", "https", "observatory.example")]
    [DataRow("192.0.2.5", "http", "localhost")]
    public async Task OnlyExplicitProxiesCanForwardHeaders(string peer, string expectedScheme, string expectedHost)
    {
        await using var factory = new AppFactory(new Dictionary<string, string?>
        {
            ["Hosting:KnownProxies:0"] = "10.0.0.5",
            ["AllowedHosts"] = "localhost;observatory.example"
        });
        using var client = factory.Client();
        var result = await factory.Server.SendAsync(context =>
        {
            context.Connection.RemoteIpAddress = IPAddress.Parse(peer);
            context.Request.Scheme = "http";
            context.Request.Host = new HostString("localhost");
            context.Request.Path = "/health/live";
            context.Request.Headers["X-Forwarded-Proto"] = "https";
            context.Request.Headers["X-Forwarded-Host"] = "observatory.example";
        });
        result.Response.StatusCode.Should().Be(200);
        result.Request.Scheme.Should().Be(expectedScheme);
        result.Request.Host.Value.Should().Be(expectedHost);
    }

    [TestMethod]
    public async Task DefaultHostingRejectsForwardingFromUnknownPeer()
    {
        await using var factory = new AppFactory();
        using var client = factory.Client();
        var result = await factory.Server.SendAsync(context =>
        {
            context.Connection.RemoteIpAddress = IPAddress.Parse("192.0.2.5");
            context.Request.Scheme = "http";
            context.Request.Host = new HostString("localhost");
            context.Request.Path = "/health/live";
            context.Request.Headers["X-Forwarded-Proto"] = "https";
        });
        result.Request.Scheme.Should().Be("http");
    }

    [TestMethod]
    public async Task IngressRejectsSpoofedHostAndUsesOnlyNearestHop()
    {
        await using var factory = new AppFactory(new Dictionary<string, string?>
        {
            ["Hosting:BehindContainerAppsIngress"] = "true"
        });
        using var client = factory.Client();
        var result = await factory.Server.SendAsync(context =>
        {
            context.Connection.RemoteIpAddress = IPAddress.Parse("10.0.0.5");
            context.Request.Scheme = "http";
            context.Request.Host = new HostString("localhost");
            context.Request.Path = "/health/live";
            context.Request.Headers["X-Forwarded-Proto"] = "http,https";
            context.Request.Headers["X-Forwarded-Host"] = "attacker.example";
            context.Request.Headers["X-Forwarded-For"] = "192.0.2.9,203.0.113.10";
        });
        result.Request.Host.Value.Should().Be("localhost");
        result.Request.Scheme.Should().Be("https");
        result.Connection.RemoteIpAddress.Should().Be(IPAddress.Parse("203.0.113.10"));
    }

    [TestMethod]
    public async Task KnownNetworkAndForwardLimitAreBoundFromOptions()
    {
        await using var factory = new AppFactory(new Dictionary<string, string?>
        {
            ["Hosting:KnownNetworks:0"] = "10.0.0.0/24",
            ["Hosting:ForwardLimit"] = "2"
        });
        using var client = factory.Client();
        var options = factory.Services.GetRequiredService<IOptions<ForwardedHeadersOptions>>().Value;
        options.ForwardLimit.Should().Be(2);
        options.KnownIPNetworks.Should().ContainSingle(x => x.Contains(IPAddress.Parse("10.0.0.5")));
        options.KnownProxies.Should().BeEmpty();
    }

    [TestMethod]
    [DataRow("10.0.0.5", 200)]
    [DataRow("192.0.2.5", 307)]
    public async Task ProductionAvoidsRedirectLoopsOnlyForTrustedHttpsForwarding(string peer, int expectedStatus)
    {
        await using var factory = new AppFactory(new Dictionary<string, string?>
        {
            ["Hosting:KnownProxies:0"] = "10.0.0.5",
            ["Cosmos:ConnectionString"] = "",
            ["Cosmos:Endpoint"] = "https://example.documents.azure.com",
            ["HTTPS_PORT"] = "443"
        }, "Production");
        using var client = factory.Client();
        var result = await factory.Server.SendAsync(context =>
        {
            context.Connection.RemoteIpAddress = IPAddress.Parse(peer);
            context.Request.Scheme = "http";
            context.Request.Host = new HostString("localhost");
            context.Request.Path = RouteConstants.Root;
            context.Request.Headers["X-Test-Role"] = "AgentOps.Reader";
            context.Request.Headers["X-Forwarded-Proto"] = "https";
        });
        result.Response.StatusCode.Should().Be(expectedStatus);
        if (expectedStatus == 307) result.Response.Headers.Location.ToString().Should().Be("https://localhost/");
    }

    [TestMethod]
    [DataRow(RouteConstants.Health)]
    [DataRow(RouteConstants.Liveness)]
    [DataRow(RouteConstants.Readiness)]
    public async Task ProductionHttpProbesAreAnonymousAndDoNotRedirect(string route)
    {
        await using var factory = new AppFactory(new Dictionary<string, string?>
        {
            ["Cosmos:ConnectionString"] = "",
            ["Cosmos:Endpoint"] = "https://example.documents.azure.com",
            ["HTTPS_PORT"] = "443"
        }, "Production");
        using var client = factory.Client();
        var result = await factory.Server.SendAsync(context =>
        {
            context.Request.Scheme = "http";
            context.Request.Host = new HostString("localhost");
            context.Request.Path = route;
        });
        result.Response.StatusCode.Should().Be(200);
        result.Response.Headers.Location.Should().BeEmpty();
    }

    [TestMethod]
    [DataRow("Hosting:KnownProxies:0", "not-an-address")]
    [DataRow("Hosting:KnownNetworks:0", "not-a-network")]
    [DataRow("Hosting:ForwardLimit", "0")]
    public async Task InvalidTrustConfigurationFailsStartup(string key, string value)
    {
        await using var factory = new AppFactory(new Dictionary<string, string?> { [key] = value });
        var start = () => factory.Client();
        start.Should().Throw<OptionsValidationException>();
    }
}
