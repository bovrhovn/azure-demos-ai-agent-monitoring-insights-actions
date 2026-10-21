using System.Net;
using System.Net.Http.Json;
using System.Text;
using AgentOps.Web;
using AgentOps.Web.Infrastructure;
using FluentAssertions;
using Microsoft.Extensions.Options;

namespace AgentOps.Tests;

[TestClass]
public sealed class WebhookTests
{
    private const string TestKey = "test-only-webhook-key";
    private const string Notification = """[{"id":"test-event","eventType":"Demo.Requested","subject":"demo","data":{},"dataVersion":"1.0"}]""";

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("wrong-key")]
    public async Task MissingOrWrongKeyIsRejectedWithoutActions(string? key)
    {
        await using var factory = new AppFactory();
        using var client = factory.Client("AgentOps.Operator");
        if (key is not null) client.DefaultRequestHeaders.Add(WebhookOptions.ApiKeyHeader, key);
        using var response = await client.PostAsync(RouteConstants.EventGridWebhook, Json(Notification));
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        factory.Store.Writes.Should().BeEmpty();
    }

    [TestMethod]
    public async Task ValidKeyAcknowledgesNotificationWithoutSessionOrActions()
    {
        await using var factory = new AppFactory();
        using var client = factory.Client();
        client.DefaultRequestHeaders.Add(WebhookOptions.ApiKeyHeader, TestKey);
        using var response = await client.PostAsync(RouteConstants.EventGridWebhook, Json(Notification));
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().BeEmpty();
        factory.Store.Writes.Should().BeEmpty();
    }

    [TestMethod]
    public async Task ValidationEchoesExactCodeWithoutFollowingValidationUrl()
    {
        await using var factory = new AppFactory();
        using var client = factory.Client();
        client.DefaultRequestHeaders.Add(WebhookOptions.ApiKeyHeader, TestKey);
        using var response = await client.PostAsJsonAsync(RouteConstants.EventGridWebhook, new[]
        {
            new { eventType = "Microsoft.EventGrid.SubscriptionValidationEvent",
                data = new { validationCode = "validation-code-123", validationUrl = "http://127.0.0.1/never-follow" } }
        });
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<Dictionary<string, string>>();
        body.Should().ContainSingle().Which.Should().Be(new KeyValuePair<string, string>("validationResponse", "validation-code-123"));
        factory.Store.Writes.Should().BeEmpty();
    }

    [TestMethod]
    [DataRow("{")]
    [DataRow("{}")]
    [DataRow("[]")]
    [DataRow("[null]")]
    [DataRow("[{}]")]
    [DataRow("""[{"eventType":42}]""")]
    [DataRow("""[{"eventType":"Microsoft.EventGrid.SubscriptionValidationEvent","data":{}}]""")]
    [DataRow("""[{"eventType":"Microsoft.EventGrid.SubscriptionValidationEvent","data":{"validationCode":5}}]""")]
    [DataRow("""[{"eventType":"Microsoft.EventGrid.SubscriptionValidationEvent","data":{"validationCode":"code"}},{"eventType":"Demo.Requested"}]""")]
    public async Task InvalidPayloadIsRejected(string body)
    {
        await using var factory = new AppFactory();
        using var client = factory.Client();
        client.DefaultRequestHeaders.Add(WebhookOptions.ApiKeyHeader, TestKey);
        using var response = await client.PostAsync(RouteConstants.EventGridWebhook, Json(body));
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        factory.Store.Writes.Should().BeEmpty();
    }

    [TestMethod]
    public async Task DuplicateKeysAndQueryStringKeysAreNotAccepted()
    {
        await using var factory = new AppFactory();
        using var client = factory.Client();
        using var queryResponse = await client.PostAsync($"{RouteConstants.EventGridWebhook}?apiKey={TestKey}", Json(Notification));
        queryResponse.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        client.DefaultRequestHeaders.Add(WebhookOptions.ApiKeyHeader, new[] { TestKey, TestKey });
        using var duplicateResponse = await client.PostAsync(RouteConstants.EventGridWebhook, Json(Notification));
        duplicateResponse.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [TestMethod]
    public async Task ConfiguredKeyIsUsedAndNonJsonIsRejected()
    {
        await using var factory = new AppFactory(new Dictionary<string, string?> { ["Webhook:ApiKey"] = "different-test-key" });
        using var client = factory.Client();
        client.DefaultRequestHeaders.Add(WebhookOptions.ApiKeyHeader, TestKey);
        (await client.PostAsync(RouteConstants.EventGridWebhook, Json(Notification))).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        client.DefaultRequestHeaders.Remove(WebhookOptions.ApiKeyHeader);
        client.DefaultRequestHeaders.Add(WebhookOptions.ApiKeyHeader, "different-test-key");
        (await client.PostAsync(RouteConstants.EventGridWebhook, Json(Notification))).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.PostAsync(RouteConstants.EventGridWebhook, new StringContent(Notification))).StatusCode.Should().Be(HttpStatusCode.UnsupportedMediaType);
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("   ")]
    public async Task MissingConfigurationFailsStartup(string key)
    {
        await using var factory = new AppFactory(new Dictionary<string, string?> { ["Webhook:ApiKey"] = key });
        var start = () => factory.Client();
        start.Should().Throw<OptionsValidationException>().WithMessage("*ApiKey*");
    }

    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");
}
