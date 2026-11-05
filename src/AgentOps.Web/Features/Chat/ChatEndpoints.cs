using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using AgentOps.Web.Features.Identity;
using AgentOps.Web.Infrastructure;
using AgentOps.Web.Infrastructure.Options;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.Extensions.Options;

namespace AgentOps.Web.Features.Chat;

public static class ChatEndpoints
{
    public static IEndpointRouteBuilder MapChatEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup(RouteConstants.ChatSessions).RequireAuthorization(Policies.View);
        group.AddEndpointFilter(async (context, next) =>
        {
            var http = context.HttpContext;
            if (http.User.FindFirstValue("oid") is null && http.User.FindFirstValue(ClaimTypes.NameIdentifier) is null)
                return Results.Forbid();
            if (HttpMethods.IsPost(http.Request.Method) || HttpMethods.IsDelete(http.Request.Method))
            {
                try { await http.RequestServices.GetRequiredService<IAntiforgery>().ValidateRequestAsync(http); }
                catch (AntiforgeryValidationException) { return Results.BadRequest(new { title = "Reload the page before sending a message." }); }
            }
            return await next(context);
        });
        group.MapGet("", (HttpContext http, IChatStore store, IOptions<ChatOptions> options, string? continuationToken) =>
            store.ListAsync(Owner(http.User), options.Value.SessionPageSize, continuationToken, http.RequestAborted));
        group.MapPost("", async (HttpContext http, ChatService chat) =>
            Results.Ok(await chat.CreateAsync(Owner(http.User), http.RequestAborted)))
            .RequireAuthorization(Policies.Operate).RequireRateLimiting("chat");
        group.MapDelete("/{sessionId}", async (string sessionId, HttpContext http, ChatService chat) =>
        {
            try
            {
                return await chat.DeleteAsync(Owner(http.User), sessionId, http.RequestAborted)
                    ? Results.NoContent() : Results.NotFound();
            }
            catch (ChatBusyException)
            {
                return Results.Conflict(new { title = "This conversation already has an operation in progress. Please wait." });
            }
        }).RequireAuthorization(Policies.Operate).RequireRateLimiting("chat");
        group.MapGet("/{sessionId}/messages", async (string sessionId, int? before, HttpContext http,
            IChatStore store, IOptions<ChatOptions> options) =>
        {
            if (before is <= 0) return Results.BadRequest();
            var owner = Owner(http.User);
            var session = await store.GetAsync(owner, sessionId, http.RequestAborted);
            if (session is null) return Results.NotFound();
            if (session.IsDeleting) return Results.Conflict(new { title = "This conversation is being deleted." });
            return Results.Ok(await store.MessagesAsync(owner, sessionId, options.Value.MessagePageSize, before, http.RequestAborted));
        });
        group.MapPost("/{sessionId}/messages", async (string sessionId, SendMessage input, HttpContext http,
            ChatService chat, IOptions<ChatOptions> options) =>
        {
            if (string.IsNullOrWhiteSpace(input.Content) || input.Content.Length > options.Value.MaxPromptLength)
                return Results.BadRequest(new { title = $"Enter a message of at most {options.Value.MaxPromptLength} characters." });
            try
            {
                var response = await chat.SendAsync(Owner(http.User), sessionId, input.Content.Trim(), http.RequestAborted);
                return response is null ? Results.NotFound() : Results.Ok(response);
            }
            catch (ChatBusyException)
            {
                return Results.Conflict(new { title = "This conversation already has an operation in progress or is being deleted. Please wait." });
            }
        }).RequireAuthorization(Policies.Operate).RequireRateLimiting("chat");
        return endpoints;
    }

    private static string Owner(ClaimsPrincipal user) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        $"{user.FindFirstValue("tid") ?? user.FindFirstValue("http://schemas.microsoft.com/identity/claims/tenantid")}\n" +
        $"{user.FindFirstValue("oid") ?? user.FindFirstValue(ClaimTypes.NameIdentifier)}")));

    public sealed record SendMessage(string? Content);
}
