using System.Text.Json;
using AgentOps.Web.Infrastructure.Options;
using Microsoft.ApplicationInsights;
using Microsoft.ApplicationInsights.DataContracts;
using Microsoft.ApplicationInsights.Extensibility;
using Microsoft.Extensions.Options;

namespace AgentOps.Web.Features.Notifications;

public sealed class NotificationTestService(LocalWebhookDelivery delivery, IOptions<WebhookOptions> webhook,
    TimeProvider time, TelemetryClient telemetry, TelemetryConfiguration telemetryConfiguration,
    ILogger<NotificationTestService> logger)
{
    public TelemetryTestReceipt EmitTelemetry()
    {
        if (string.IsNullOrWhiteSpace(telemetryConfiguration.ConnectionString) || !telemetry.IsEnabled())
            throw Unavailable("Application Insights is not configured or telemetry is disabled.");
        var id = Guid.NewGuid().ToString("N");
        var now = time.GetUtcNow();
        var exceptionMessage = $"[Inbox test] Synthetic exception {id} at {now:O}";
        var traceMessage = $"[Inbox test] Synthetic warning trace {id} at {now:O}";
        ExceptionTelemetry exception;
        try { throw new InvalidOperationException(exceptionMessage); }
        catch (InvalidOperationException failure) { exception = new(failure) { SeverityLevel = SeverityLevel.Error, Timestamp = now }; }
        var trace = new TraceTelemetry(traceMessage, SeverityLevel.Warning) { Timestamp = now };
        foreach (var item in new ISupportProperties[] { exception, trace })
        {
            item.Properties["TestId"] = id;
            item.Properties["Source"] = "WebhookInboxTest";
            item.Properties["Synthetic"] = "true";
        }
        telemetry.TrackException(exception);
        telemetry.TrackTrace(trace);
        return new(id, now, exceptionMessage, traceMessage);
    }

    public async Task<WebhookTestReceipt> SendWebhookAsync(HttpRequest original, string message, CancellationToken cancellationToken)
    {
        var id = Guid.NewGuid().ToString("N");
        var now = time.GetUtcNow();
        var body = JsonSerializer.SerializeToUtf8Bytes(new[]
        {
            new
            {
                id,
                subject = "/diagnostics/webhook-inbox",
                eventType = "AgentOps.WebhookTest",
                eventTime = now,
                data = new { message, testId = id, isTest = true },
                dataVersion = "1.0"
            }
        });
        if (body.Length > webhook.Value.MaxPayloadBytes)
            throw Unavailable("The test event exceeds Webhook:MaxPayloadBytes. Shorten the message before retrying.");
        await delivery.SendAsync(body, cancellationToken, original);
        return new(id, now, message);
    }

    private NotificationTestUnavailableException Unavailable(string message)
    {
        logger.LogWarning("Inbox test unavailable: {Reason}", message);
        return new(message);
    }
}

public sealed record TelemetryTestReceipt(string TestId, DateTimeOffset OccurredAt, string ExceptionMessage, string TraceMessage);
public sealed record WebhookTestReceipt(string TestId, DateTimeOffset OccurredAt, string Message);
public sealed class NotificationTestUnavailableException(string message) : Exception(message);
