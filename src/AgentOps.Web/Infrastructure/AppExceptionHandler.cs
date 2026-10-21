using Microsoft.AspNetCore.Diagnostics;

namespace AgentOps.Web.Infrastructure;

/// <summary>Returns safe errors while preserving exception detail in structured logs.</summary>
public sealed class AppExceptionHandler(ILogger<AppExceptionHandler> logger, IProblemDetailsService problems) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken cancellationToken)
    {
        logger.LogError(exception, "Request failed with trace {TraceId}", context.TraceIdentifier);
        var status = exception is BadHttpRequestException badRequest
            ? badRequest.StatusCode : StatusCodes.Status500InternalServerError;
        context.Response.StatusCode = status;
        return await problems.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = context,
            ProblemDetails = new()
            {
                Status = status,
                Title = exception is BadHttpRequestException ? "The request was not valid." : "The operation could not be completed.",
                Extensions = { ["traceId"] = context.TraceIdentifier }
            }
        });
    }
}
