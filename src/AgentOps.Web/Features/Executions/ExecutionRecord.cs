namespace AgentOps.Web.Features.Executions;

/// <summary>Persisted operational history, not application log output.</summary>
public sealed record ExecutionRecord(
    string Id,
    string Trigger,
    string Status,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt,
    string RequestedBy,
    string CorrelationId,
    string? Summary);

/// <summary>Stable partition and event names shared by both entry points.</summary>
public static class ExecutionContract
{
    public const string Endpoint = "Endpoint";
    public const string Schedule = "Schedule";
    public const string Running = "Running";
    public const string NotImplemented = "NotImplemented";
    public const string Failed = "Failed";
    public const string Canceled = "Canceled";
    public const string ChangedEvent = "ExecutionChanged";
}

/// <summary>Durable history abstraction. Implementations must surface write failures.</summary>
public interface IExecutionStore
{
    Task SaveAsync(ExecutionRecord execution, CancellationToken cancellationToken);
    Task<IReadOnlyList<ExecutionRecord>> ListAsync(string trigger, int limit, CancellationToken cancellationToken);
    Task<ExecutionRecord?> GetAsync(string id, string trigger, CancellationToken cancellationToken);
}

/// <summary>Live notifications are separate from durable history and telemetry.</summary>
public interface IExecutionNotifier
{
    Task PublishAsync(ExecutionRecord execution, CancellationToken cancellationToken);
}
