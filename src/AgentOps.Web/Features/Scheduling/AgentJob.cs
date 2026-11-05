using AgentOps.Web.Features.Executions;
using Hangfire;
using Hangfire.Server;

namespace AgentOps.Web.Features.Scheduling;

/// <summary>Scheduled calls use exactly the same execution pipeline as the API.</summary>
public sealed class AgentJob(ExecuteAgent handler)
{
    [AutomaticRetry(Attempts = 0)]
    public Task<ExecutionRecord> RunAsync(CancellationToken cancellationToken, PerformContext? context = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var executionId = context?.GetJobParameter<string>("AgentOpsExecutionId");
        if (context is not null && executionId is null)
        {
            // The parameter survives requeue, without relying on volatile IDs or timestamp serialization precision.
            executionId = "hangfire-" + Guid.NewGuid().ToString("N");
            context.SetJobParameter("AgentOpsExecutionId", executionId);
        }
        return handler.RunAsync(ExecutionContract.Schedule, "hangfire", cancellationToken, executionId);
    }
}
