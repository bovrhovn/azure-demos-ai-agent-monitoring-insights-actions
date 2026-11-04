using System.Security.Claims;
using AgentOps.Web.Features.Identity;
using AgentOps.Web.Infrastructure;
using Microsoft.Extensions.Options;

namespace AgentOps.Web.Features.Executions;

public static class ExecutionEndpoints
{
    /// <summary>WebSocket upgrades need an origin check in addition to ordinary CORS controls.</summary>
    public static IApplicationBuilder UseExecutionHubOriginGuard(this IApplicationBuilder app) => app.Use(async (context, next) =>
    {
        var request = context.Request;
        if (request.Path.StartsWithSegments(RouteConstants.ExecutionHub) && request.Headers.TryGetValue("Origin", out var origin))
        {
            var expected = new Uri($"{request.Scheme}://{request.Host}").GetLeftPart(UriPartial.Authority);
            if (!Uri.TryCreate(origin.ToString(), UriKind.Absolute, out var supplied) ||
                !string.Equals(supplied.GetLeftPart(UriPartial.Authority), expected, StringComparison.OrdinalIgnoreCase))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }
        }
        await next(context);
    });

    public static IEndpointRouteBuilder MapExecutionEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(RouteConstants.ExecutionHistory, async (string trigger, IExecutionStore store,
            IOptions<ExecutionOptions> options, ILoggerFactory loggerFactory, CancellationToken cancellationToken) =>
        {
            if (trigger is not (ExecutionContract.Endpoint or ExecutionContract.Schedule))
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["trigger"] = ["Use Endpoint or Schedule."] });
            var records = await store.ListAsync(trigger, options.Value.HistoryLimit, cancellationToken);
            LogHistory(loggerFactory, trigger, records.Count, options.Value.HistoryLimit);
            return Results.Ok(records);
        }).RequireAuthorization(Policies.View);
        var api = endpoints.MapGroup(RouteConstants.ExecutionApi).RequireAuthorization(Policies.RunApi);
        api.MapPost(RouteConstants.Root, async (ExecuteAgent handler, ClaimsPrincipal user, CancellationToken cancellationToken) =>
        {
            var caller = user.FindFirstValue("oid") ??
                user.FindFirstValue("http://schemas.microsoft.com/identity/claims/objectidentifier") ??
                user.FindFirstValue(ClaimTypes.NameIdentifier) ?? "authenticated-caller";
            var result = await handler.RunAsync(ExecutionContract.Endpoint, caller, cancellationToken);
            return Results.Ok(result);
        }).RequireRateLimiting("executions");
        api.MapGet(RouteConstants.Root, async (IExecutionStore store, IOptions<ExecutionOptions> options,
            ILoggerFactory loggerFactory, CancellationToken cancellationToken) =>
        {
            var records = await store.ListAsync(ExecutionContract.Endpoint, options.Value.HistoryLimit, cancellationToken);
            LogHistory(loggerFactory, ExecutionContract.Endpoint, records.Count, options.Value.HistoryLimit);
            return Results.Ok(records);
        });
        api.MapGet(RouteConstants.ExecutionById, async (string id, IExecutionStore store,
            ILoggerFactory loggerFactory, CancellationToken cancellationToken) =>
        {
            var execution = await store.GetAsync(id, ExecutionContract.Endpoint, cancellationToken);
            loggerFactory.CreateLogger(typeof(ExecutionEndpoints)).LogInformation(
                "Execution lookup completed for {Trigger}; found {ExecutionFound}", ExecutionContract.Endpoint, execution is not null);
            return execution is null ? Results.NotFound() : Results.Ok(execution);
        });
        return endpoints;
    }

    private static void LogHistory(ILoggerFactory loggerFactory, string trigger, int count, int limit) =>
        loggerFactory.CreateLogger(typeof(ExecutionEndpoints)).LogInformation(
            "Execution history loaded for {Trigger} with {ExecutionCount} executions and limit {HistoryLimit}", trigger, count, limit);
}
