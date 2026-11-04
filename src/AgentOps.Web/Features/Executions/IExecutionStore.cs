namespace AgentOps.Web.Features.Executions;

/// <summary>Durable history abstraction. Implementations must surface write failures.</summary>
public interface IExecutionStore
{
    Task SaveAsync(ExecutionRecord execution, CancellationToken cancellationToken);
    Task<IReadOnlyList<ExecutionRecord>> ListAsync(string trigger, int limit, CancellationToken cancellationToken);
    Task<ExecutionRecord?> GetAsync(string id, string trigger, CancellationToken cancellationToken);
}
