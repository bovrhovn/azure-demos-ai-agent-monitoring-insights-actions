using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentOps.Web.Infrastructure;
using AgentOps.Web.Infrastructure.Options;
using Microsoft.Extensions.Options;

namespace AgentOps.Web.Features.Webhooks;

public static class EventGridWebhook
{
    private const string ValidationEvent = "Microsoft.EventGrid.SubscriptionValidationEvent";

    public static IEndpointConventionBuilder MapEventGridWebhook(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapPost(RouteConstants.EventGridWebhook, HandleAsync).AllowAnonymous();

    private static async Task<IResult> HandleAsync(HttpRequest request, IOptions<WebhookOptions> options,
        ILoggerFactory loggerFactory, CancellationToken cancellationToken)
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

        JsonDocument document;
        try
        {
            document = await JsonDocument.ParseAsync(request.Body, cancellationToken: cancellationToken);
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

            logger.LogInformation("Event Grid webhook acknowledged {EventCount} events; no actions were executed.", events.GetArrayLength());
            return Results.Ok();
        }
    }
}
