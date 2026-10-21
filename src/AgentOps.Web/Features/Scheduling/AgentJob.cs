using AgentOps.Web.Features.Executions;
using Hangfire;

namespace AgentOps.Web.Features.Scheduling;

/// <summary>Scheduled calls use exactly the same execution pipeline as the API.</summary>
public sealed class AgentJob(ExecuteAgent handler)
{
    [AutomaticRetry(Attempts = 0)]
    public Task<ExecutionRecord> RunAsync(CancellationToken cancellationToken) =>
        handler.RunAsync(ExecutionContract.Schedule, "hangfire", cancellationToken);
}
