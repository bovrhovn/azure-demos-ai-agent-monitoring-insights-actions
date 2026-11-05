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
    public const string Completed = "Completed";
    public const string NotImplemented = "NotImplemented";
    public const string Failed = "Failed";
    public const string Canceled = "Canceled";
    public const string ChangedEvent = "ExecutionChanged";
}
