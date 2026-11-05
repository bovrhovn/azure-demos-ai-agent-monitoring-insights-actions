using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AgentOps.Web;
using AgentOps.Web.Infrastructure;
using AgentOps.Web.Infrastructure.Options;
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
        factory.Notifications.Items.Should().BeEmpty();
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
        factory.Notifications.Items.Should().BeEmpty();
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

    public static IEnumerable<object[]> HttpCallSamples()
    {
        foreach (var file in Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "HttpCalls"), "*.http").Order())
        {
            foreach (var block in Regex.Split(File.ReadAllText(file).ReplaceLineEndings("\n"), @"(?m)^### ").Skip(1))
                yield return [Path.GetFileName(file) + ": " + block.Split('\n')[0], block];
        }
    }

    [TestMethod]
    [DynamicData(nameof(HttpCallSamples))]
    public async Task HttpCallSampleReturnsDocumentedResponseWithoutActions(string sample, string block)
    {
        await using var factory = new AppFactory();
        using var client = factory.Client();
        var expectedStatus = Regex.Match(block, @"(?m)^# Expected status: (\d+)$");
        expectedStatus.Success.Should().BeTrue(sample);
        var requestStart = block.IndexOf("\nPOST ", StringComparison.Ordinal);
        requestStart.Should().BeGreaterThanOrEqualTo(0, sample);
        var parts = block[(requestStart + 1)..].Split("\n\n", 2);
        parts.Should().HaveCount(2, sample);
        var headers = parts[0].Split('\n');
        var origin = client.BaseAddress!.GetLeftPart(UriPartial.Authority);
        var url = headers[0]["POST ".Length..].Replace("{{baseUrl}}", origin).Replace("{{url}}", origin);
        url.Should().NotContain("{{", sample);
        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        var contentType = headers.Single(line => line.StartsWith("Content-Type: ", StringComparison.Ordinal))["Content-Type: ".Length..];
        request.Content = new StringContent(parts[1].Trim(), Encoding.UTF8, contentType);
        foreach (var header in headers.Skip(1).Where(line => !line.StartsWith("Content-Type:", StringComparison.Ordinal)))
        {
            var pair = header.Split(':', 2);
            var value = pair[1].Trim().Replace("{{apiKey}}", TestKey).Replace("{{myapiKey}}", TestKey);
            value.Should().NotContain("{{", sample);
            request.Headers.Add(pair[0], value);
        }

        using var response = await client.SendAsync(request);
        ((int)response.StatusCode).Should().Be(int.Parse(expectedStatus.Groups[1].Value), sample);
        if (response.IsSuccessStatusCode)
        {
            using var events = JsonDocument.Parse(parts[1]);
            var first = events.RootElement[0];
            if (first.GetProperty("eventType").GetString() == "Microsoft.EventGrid.SubscriptionValidationEvent")
            {
                var result = await response.Content.ReadFromJsonAsync<Dictionary<string, string>>();
                result.Should().ContainSingle().Which.Should().Be(new KeyValuePair<string, string>(
                    "validationResponse", first.GetProperty("data").GetProperty("validationCode").GetString()!));
                factory.Notifications.Items.Should().BeEmpty(sample);
            }
            else
            {
                (await response.Content.ReadAsStringAsync()).Should().BeEmpty(sample);
                factory.Notifications.Items.Should().HaveCount(events.RootElement.GetArrayLength(), sample);
            }
        }
        else factory.Notifications.Items.Should().BeEmpty(sample);
        factory.Store.Writes.Should().BeEmpty(sample);
    }

    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");
}
