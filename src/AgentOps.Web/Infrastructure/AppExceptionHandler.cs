using AgentOps.Web.Features.Agent;
using Microsoft.AspNetCore.Diagnostics;

namespace AgentOps.Web.Infrastructure;

/// <summary>Returns safe errors while preserving exception detail in structured logs.</summary>
public sealed class AppExceptionHandler(ILogger<AppExceptionHandler> logger, IProblemDetailsService problems) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken cancellationToken)
    {
        logger.LogError(exception, "Request failed with trace {TraceId}", context.TraceIdentifier);
        var status = exception switch
        {
            BadHttpRequestException badRequest => badRequest.StatusCode,
            FoundryConfigurationException or FoundryRateLimitException => StatusCodes.Status503ServiceUnavailable,
            _ => StatusCodes.Status500InternalServerError
        };
        context.Response.StatusCode = status;
        return await problems.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = context,
            ProblemDetails = new()
            {
                Status = status,
                Title = exception switch
                {
                    BadHttpRequestException => "The request was not valid.",
                    FoundryConfigurationException => "Foundry is not configured.",
                    FoundryRateLimitException => "Foundry is temporarily rate limited.",
                    _ => "The operation could not be completed."
                },
                Detail = exception is FoundryConfigurationException or FoundryRateLimitException ? exception.Message : null,
                Extensions = { ["traceId"] = context.TraceIdentifier }
            }
        });
    }
}
