namespace AgentOps.Web.Features.Executions;

/// <summary>Live notifications are separate from durable history and telemetry.</summary>
public interface IExecutionNotifier
{
    Task PublishAsync(ExecutionRecord execution, CancellationToken cancellationToken);
}
