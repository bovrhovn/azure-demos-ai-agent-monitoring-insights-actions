using System.Security.Claims;
using AgentOps.Web.Features.Chat;
using AgentOps.Web.Features.Identity;
using AgentOps.Web.Infrastructure.Options;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.Extensions.Options;

namespace AgentOps.Web.Features.Notifications;

public static class NotificationEndpoints
{
    public static IEndpointRouteBuilder MapNotificationEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup(RouteConstants.Notifications).RequireAuthorization(Policies.View);
        group.AddEndpointFilter(async (context, next) =>
        {
            var http = context.HttpContext;
            if (!HttpMethods.IsGet(http.Request.Method))
            {
                if (http.User.FindFirstValue("oid") is null && http.User.FindFirstValue(ClaimTypes.NameIdentifier) is null)
                    return Results.Forbid();
                try { await http.RequestServices.GetRequiredService<IAntiforgery>().ValidateRequestAsync(http); }
                catch (AntiforgeryValidationException) { return Results.BadRequest(new { title = "Reload the page before updating notifications." }); }
            }
            return await next(context);
        });
        group.MapGet("", async (string? search, string? handled, string? sort, string? continuationToken,
            HttpContext http, INotificationStore store, IOptions<NotificationOptions> options) =>
        {
            if ((search?.Length ?? 0) > options.Value.MaxSearchLength ||
                handled is not (null or "all" or "handled" or "unhandled") || sort is not (null or "desc" or "asc"))
                return Results.BadRequest(new { title = "Invalid notification search, status or sort order." });
            bool? isHandled = handled switch { "handled" => true, "unhandled" => false, _ => null };
            var page = await store.ListAsync(search?.Trim(), isHandled, sort != "asc", options.Value.PageSize,
                continuationToken, http.RequestAborted);
            var owner = ChatEndpoints.Owner(http.User);
            return Results.Ok(new
            {
                items = page.Items.Select(item => new
                {
                    item.Id,
                    item.RawData,
                    item.Message,
                    item.ReceivedDate,
                    item.IsHandled,
                    item.Comment,
                    item.ChatSessionId,
                    canOpenSession = item.ChatSessionId is not null && item.ChatOwnerId == owner,
                    canStartChat = item.ChatSessionId is null || item.ChatOwnerId == owner
                }),
                page.ContinuationToken
            });
        });
        group.MapPost("/{id}/handled", async (string id, SetHandled input, HttpContext http,
            INotificationStore store, IOptions<NotificationOptions> options) =>
        {
            if ((input.Comment?.Length ?? 0) > options.Value.MaxCommentLength || (input.IsHandled && !input.Confirmed))
                return Results.BadRequest(new { title = "Confirm completion and keep the optional comment within the allowed length." });
            var notification = await store.SetHandledAsync(id, input.IsHandled,
                string.IsNullOrWhiteSpace(input.Comment) ? null : input.Comment.Trim(), http.RequestAborted);
            return notification is null ? Results.NotFound() : Results.NoContent();
        }).RequireAuthorization(Policies.Operate);
        group.MapPost("/{id}/session", async (string id, HttpContext http, INotificationStore store,
            NotificationService service, IOptions<ChatOptions> options) =>
        {
            var notification = await store.GetAsync(id, http.RequestAborted);
            if (notification is null) return Results.NotFound();
            if (string.IsNullOrWhiteSpace(notification.Message) || notification.Message.Length > options.Value.MaxPromptLength)
                return Results.BadRequest(new { title = $"A data.message value of at most {options.Value.MaxPromptLength} characters is required to start a chat." });
            try
            {
                var session = await service.StartChatAsync(id, ChatEndpoints.Owner(http.User), http.RequestAborted);
                return session is null ? Results.NotFound() : Results.Ok(new { sessionId = session.Id });
            }
            catch (NotificationOwnershipException)
            {
                return Results.Conflict(new { title = "This notification is linked to another operator's private conversation." });
            }
            catch (ChatBusyException)
            {
                return Results.Conflict(new { title = "This notification or conversation has an operation in progress. Please retry." });
            }
        }).RequireAuthorization(Policies.Operate).RequireRateLimiting("chat");
        group.MapPost(RouteConstants.NotificationTelemetryTest, (NotificationTestService service) =>
        {
            try { return Results.Ok(service.EmitTelemetry()); }
            catch (NotificationTestUnavailableException exception)
            {
                return Results.Problem(title: exception.Message, statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        }).RequireAuthorization(Policies.Operate).RequireRateLimiting("notification-tests");
        group.MapPost(RouteConstants.NotificationWebhookTest, async (TestWebhook input, HttpContext http,
            NotificationTestService service, IOptions<NotificationOptions> options, ILogger<NotificationTestService> logger) =>
        {
            if (string.IsNullOrWhiteSpace(input.Message) || input.Message.Length > options.Value.MaxTestMessageLength)
                return Results.BadRequest(new { title = $"Enter a test message of at most {options.Value.MaxTestMessageLength} characters." });
            try { return Results.Ok(await service.SendWebhookAsync(http.Request, input.Message.Trim(), http.RequestAborted)); }
            catch (NotificationTestUnavailableException exception)
            {
                return Results.Problem(title: exception.Message, statusCode: StatusCodes.Status503ServiceUnavailable);
            }
            catch (HttpRequestException exception)
            {
                logger.LogWarning(exception, "Local webhook test was not acknowledged.");
                return Results.Problem(title: "The local webhook did not acknowledge the test. Refresh the inbox before retrying.",
                    statusCode: StatusCodes.Status502BadGateway);
            }
            catch (OperationCanceledException exception) when (!http.RequestAborted.IsCancellationRequested)
            {
                logger.LogWarning(exception, "Local webhook test timed out.");
                return Results.Problem(title: "The local webhook test timed out. It may have been saved; refresh the inbox before retrying.",
                    statusCode: StatusCodes.Status504GatewayTimeout);
            }
        }).RequireAuthorization(Policies.Operate).RequireRateLimiting("notification-tests");
        return endpoints;
    }

    public sealed record SetHandled(bool IsHandled, string? Comment, bool Confirmed = false);
    public sealed record TestWebhook(string? Message);
}
