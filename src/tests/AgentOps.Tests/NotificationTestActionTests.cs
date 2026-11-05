using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AgentOps.Web;
using AgentOps.Web.Features.Notifications;
using FluentAssertions;
using Microsoft.ApplicationInsights.Channel;
using Microsoft.ApplicationInsights.DataContracts;
using Microsoft.ApplicationInsights.Extensibility;
using Microsoft.Extensions.DependencyInjection;

namespace AgentOps.Tests;

[TestClass]
public sealed class NotificationTestActionTests
{
    [TestMethod]
    public async Task TelemetryButtonQueuesOneRealExceptionAndWarningWithSharedRandomIdAndUtcTimestamp()
    {
        await using var factory = new AppFactory();
        using var client = factory.Client("AgentOps.Operator");
        await ChatTests.AuthorizePosts(client);
        var channel = CaptureTelemetry(factory.Services);
        var before = DateTimeOffset.UtcNow;
        var response = await client.PostAsync(RouteConstants.Notifications + RouteConstants.NotificationTelemetryTest, null);
        response.EnsureSuccessStatusCode();
        var receipt = (await response.Content.ReadFromJsonAsync<TelemetryTestReceipt>())!;
        receipt.OccurredAt.Should().BeOnOrAfter(before).And.BeOnOrBefore(DateTimeOffset.UtcNow);
        receipt.OccurredAt.Offset.Should().Be(TimeSpan.Zero);
        var sent = channel.TestItems;
        sent.Should().HaveCount(2);
        var exception = sent.OfType<ExceptionTelemetry>().Single();
        exception.Exception.Should().BeOfType<InvalidOperationException>();
        exception.Exception.Message.Should().Be(receipt.ExceptionMessage);
        exception.Exception.StackTrace.Should().Contain(nameof(NotificationTestService.EmitTelemetry));
        exception.SeverityLevel.Should().Be(SeverityLevel.Error);
        var trace = sent.OfType<TraceTelemetry>().Single();
        trace.Message.Should().Be(receipt.TraceMessage);
        trace.SeverityLevel.Should().Be(SeverityLevel.Warning);
        foreach (var item in sent)
        {
            item.Timestamp.Should().Be(receipt.OccurredAt);
            var properties = ((ISupportProperties)item).Properties;
            properties.Should().Contain("TestId", receipt.TestId).And.Contain("Synthetic", "true");
        }
        receipt.ExceptionMessage.Should().Contain(receipt.TestId).And.Contain(receipt.OccurredAt.ToString("O"));
        receipt.TraceMessage.Should().Contain(receipt.TestId).And.Contain("[Inbox test]");
        var second = await client.PostAsync(RouteConstants.Notifications + RouteConstants.NotificationTelemetryTest, null);
        second.EnsureSuccessStatusCode();
        var next = (await second.Content.ReadFromJsonAsync<TelemetryTestReceipt>())!;
        next.TestId.Should().NotBe(receipt.TestId);
        next.ExceptionMessage.Should().NotBe(receipt.ExceptionMessage);
        factory.Services.GetRequiredService<TelemetryConfiguration>().DisableTelemetry = true;
        (await client.PostAsync(RouteConstants.Notifications + RouteConstants.NotificationTelemetryTest, null))
            .StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        channel.TestItems.Should().HaveCount(4);
        factory.Notifications.Items.Should().BeEmpty();
        factory.ChatAgent.Calls.Should().BeEmpty();
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task LocalTestPostsToTheActualWebhookWithServerSideKeyAndSurfacesPersistenceFailures(bool behindIngress)
    {
        var settings = new Dictionary<string, string?> { ["AllowedHosts"] = "*" };
        if (behindIngress)
        {
            settings["Cosmos:ConnectionString"] = "";
            settings["Cosmos:Endpoint"] = "https://example.documents.azure.com";
            settings["Hosting:BehindContainerAppsIngress"] = "true";
            settings["AllowedHosts"] = "localhost;127.0.0.1;observatory.example";
            settings["HTTPS_PORT"] = "443";
        }
        await using var factory = new AppFactory(settings, behindIngress ? "Production" : "Development");
        factory.UseKestrel();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-Role", "AgentOps.Operator");
        client.DefaultRequestHeaders.Host = behindIngress ? "observatory.example" : "must-not-receive-the-key.invalid";
        if (behindIngress) client.DefaultRequestHeaders.Add("X-Forwarded-Proto", "https");
        await ChatTests.AuthorizePosts(client);
        const string message = "<img src=x onerror=alert(1)> Pipeline check";
        var response = await client.PostAsJsonAsync(RouteConstants.Notifications + RouteConstants.NotificationWebhookTest,
            new { message = "  " + message + "  " });
        response.EnsureSuccessStatusCode();
        var receipt = (await response.Content.ReadFromJsonAsync<WebhookTestReceipt>())!;
        var saved = factory.Notifications.Items.Should().ContainSingle().Which.Value;
        saved.Message.Should().Be(message).And.Be(receipt.Message);
        saved.ReceivedDate.Should().BeOnOrAfter(receipt.OccurredAt).And.BeOnOrBefore(DateTimeOffset.UtcNow);
        saved.IsHandled.Should().BeFalse();
        saved.ChatSessionId.Should().BeNull();
        using var raw = JsonDocument.Parse(saved.RawData);
        raw.RootElement.GetProperty("id").GetString().Should().Be(receipt.TestId);
        raw.RootElement.GetProperty("eventTime").GetDateTimeOffset().Should().Be(receipt.OccurredAt);
        raw.RootElement.GetProperty("eventType").GetString().Should().Be("AgentOps.WebhookTest");
        raw.RootElement.GetProperty("data").GetProperty("isTest").GetBoolean().Should().BeTrue();
        saved.RawData.Should().NotContain("test-only-webhook-key");
        (await response.Content.ReadAsStringAsync()).Should().NotContain("test-only-webhook-key");
        factory.Notifications.FailCreate = true;
        var failure = await client.PostAsJsonAsync(RouteConstants.Notifications + RouteConstants.NotificationWebhookTest, new { message });
        failure.StatusCode.Should().Be(HttpStatusCode.BadGateway);
        (await failure.Content.ReadAsStringAsync()).Should().Contain("did not acknowledge").And.NotContain("private test failure");
        factory.Notifications.Items.Should().ContainSingle();
        factory.Chats.Sessions.Should().BeEmpty();
        factory.ChatAgent.Calls.Should().BeEmpty();
        factory.Store.Writes.Should().BeEmpty();
    }

    [TestMethod]
    [DataRow(RouteConstants.NotificationTelemetryTest)]
    [DataRow(RouteConstants.NotificationWebhookTest)]
    public async Task TestActionsEnforceOperatorAuthenticationAndAntiforgery(string action)
    {
        await using var factory = new AppFactory();
        var channel = CaptureTelemetry(factory.Services);
        using var anonymous = factory.Client();
        using var reader = factory.Client("AgentOps.Reader");
        using var noToken = factory.Client("AgentOps.Operator");
        var path = RouteConstants.Notifications + action;
        (await anonymous.PostAsJsonAsync(path, new { message = "test" })).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await reader.PostAsJsonAsync(path, new { message = "test" })).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await noToken.PostAsJsonAsync(path, new { message = "test" })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var html = await noToken.GetStringAsync(RouteConstants.NotificationsPage);
        html.Should().Contain("data-telemetry-test-endpoint").And.Contain("data-webhook-test-endpoint")
            .And.NotContain("test-only-webhook-key");
        channel.TestItems.Should().BeEmpty();
        factory.Notifications.Items.Should().BeEmpty();
    }

    [TestMethod]
    public async Task TestsReportUnavailableTelemetryAndInvalidMessagesInsteadOfClaimingSuccessAndAreRateLimited()
    {
        await using var factory = new AppFactory(new Dictionary<string, string?>
        {
            ["Notifications:TestRequestsPerMinute"] = "5",
            ["Notifications:MaxTestMessageLength"] = "10"
        });
        using var client = factory.Client("AgentOps.Operator");
        await ChatTests.AuthorizePosts(client);
        var response = await client.PostAsync(RouteConstants.Notifications + RouteConstants.NotificationTelemetryTest, null);
        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        (await response.Content.ReadAsStringAsync()).Should().Contain("not configured");
        foreach (var message in new string?[] { null, " ", new('x', 11) })
            (await client.PostAsJsonAsync(RouteConstants.Notifications + RouteConstants.NotificationWebhookTest, new { message }))
                .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var noListener = await client.PostAsJsonAsync(RouteConstants.Notifications + RouteConstants.NotificationWebhookTest, new { message = "test" });
        noListener.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        (await noListener.Content.ReadAsStringAsync()).Should().Contain("listening HTTP");
        (await client.PostAsync(RouteConstants.Notifications + RouteConstants.NotificationTelemetryTest, null))
            .StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        factory.Notifications.Items.Should().BeEmpty();
    }

    internal static RecordingTelemetryChannel CaptureTelemetry(IServiceProvider services)
    {
        var configuration = services.GetRequiredService<TelemetryConfiguration>();
        configuration.ConnectionString = "InstrumentationKey=00000000-0000-0000-0000-000000000001";
        configuration.DisableTelemetry = false;
        var channel = new RecordingTelemetryChannel();
        configuration.TelemetryChannel = channel;
        return channel;
    }
}

internal sealed class RecordingTelemetryChannel : ITelemetryChannel
{
    private readonly ConcurrentQueue<ITelemetry> items = new();
    public bool? DeveloperMode { get; set; }
    public string EndpointAddress { get; set; } = "";
    public ITelemetry[] TestItems => items.Where(item => item is ISupportProperties properties &&
        properties.Properties.TryGetValue("Source", out var source) && source == "WebhookInboxTest").ToArray();
    public void Send(ITelemetry item) => items.Enqueue(item);
    public void Flush() { }
    public void Dispose() { }
}
