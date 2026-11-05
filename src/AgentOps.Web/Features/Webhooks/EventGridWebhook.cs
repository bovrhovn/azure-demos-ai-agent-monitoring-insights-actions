using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentOps.Web.Infrastructure;
using AgentOps.Web.Infrastructure.Options;
using AgentOps.Web.Features.Notifications;
using Microsoft.Extensions.Options;

namespace AgentOps.Web.Features.Webhooks;

public static class EventGridWebhook
{
    private const string ValidationEvent = "Microsoft.EventGrid.SubscriptionValidationEvent";

    public static IEndpointConventionBuilder MapEventGridWebhook(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapPost(RouteConstants.EventGridWebhook, HandleAsync).AllowAnonymous();

    private static async Task<IResult> HandleAsync(HttpRequest request, IOptions<WebhookOptions> options,
        INotificationStore notifications, TimeProvider time, ILoggerFactory loggerFactory, CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger("AgentOps.Web.Features.Webhooks.EventGridWebhook");
        if (!request.Headers.TryGetValue(WebhookOptions.ApiKeyHeader, out var supplied) ||
            supplied.Count != 1 ||
            !CryptographicOperations.FixedTimeEquals(
                SHA256.HashData(Encoding.UTF8.GetBytes(supplied[0] ?? "")),
                SHA256.HashData(Encoding.UTF8.GetBytes(options.Value.ApiKey))))
        {
            logger.LogWarning("Event Grid webhook rejected an invalid API key.");
            return Results.Unauthorized();
        }

        if (!request.HasJsonContentType())
            return Results.Problem("An Event Grid JSON event array is required.", statusCode: StatusCodes.Status415UnsupportedMediaType);

        var buffer = new byte[options.Value.MaxPayloadBytes + 1];
        var length = 0;
        while (length < buffer.Length)
        {
            var read = await request.Body.ReadAsync(buffer.AsMemory(length), cancellationToken);
            if (read == 0) break;
            length += read;
        }
        if (length > options.Value.MaxPayloadBytes)
            return Results.Problem("The webhook payload exceeds the configured size limit.", statusCode: StatusCodes.Status413PayloadTooLarge);

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(buffer.AsMemory(0, length));
        }
        catch (JsonException)
        {
            return Results.BadRequest(new { error = "Invalid Event Grid JSON." });
        }

        using (document)
        {
            var events = document.RootElement;
            if (events.ValueKind != JsonValueKind.Array || events.GetArrayLength() == 0 ||
                events.EnumerateArray().Any(item => item.ValueKind != JsonValueKind.Object ||
                    !item.TryGetProperty("eventType", out var type) || type.ValueKind != JsonValueKind.String ||
                    string.IsNullOrWhiteSpace(type.GetString())))
                return Results.BadRequest(new { error = "A nonempty Event Grid event array with eventType values is required." });

            var validations = events.EnumerateArray().Where(item =>
                item.GetProperty("eventType").GetString() == ValidationEvent).ToArray();
            if (validations.Length > 0)
            {
                if (events.GetArrayLength() != 1 ||
                    !validations[0].TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object ||
                    !data.TryGetProperty("validationCode", out var code) || code.ValueKind != JsonValueKind.String ||
                    string.IsNullOrWhiteSpace(code.GetString()))
                    return Results.BadRequest(new { error = "A single subscription validation event with validationCode is required." });

                logger.LogInformation("Event Grid webhook subscription validation completed.");
                return Results.Ok(new { validationResponse = code.GetString() });
            }

            var receivedDate = time.GetUtcNow();
            foreach (var item in events.EnumerateArray())
            {
                var eventId = item.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String
                    ? id.GetString() : null;
                var topic = item.TryGetProperty("topic", out var source) && source.ValueKind == JsonValueKind.String
                    ? source.GetString() : null;
                var notificationId = string.IsNullOrWhiteSpace(eventId) ? Guid.NewGuid().ToString("N")
                    : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new[] { topic, eventId }))));
                var message = item.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object &&
                    data.TryGetProperty("message", out var text) && text.ValueKind == JsonValueKind.String ? text.GetString()! : "";
                await notifications.CreateAsync(new(notificationId, item.GetRawText(), message, receivedDate), cancellationToken);
            }
            logger.LogInformation("Event Grid webhook saved {EventCount} notifications; no actions were executed.", events.GetArrayLength());
            return Results.Ok();
        }
    }
}
