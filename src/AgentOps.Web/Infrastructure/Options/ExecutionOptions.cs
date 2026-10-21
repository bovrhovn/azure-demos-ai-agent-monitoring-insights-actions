using System.ComponentModel.DataAnnotations;

namespace AgentOps.Web.Infrastructure;

/// <summary>Bounded query and execution settings.</summary>
public sealed class ExecutionOptions
{
    public const string Section = "Executions";
    [Range(1, 500)] public int HistoryLimit { get; set; } = 50;
    [Range(1, 3600)] public int TimeoutSeconds { get; set; } = 120;
    [Range(1, 120)] public int FinalizationTimeoutSeconds { get; set; } = 15;
    [Range(1, 100)] public int RequestsPerMinute { get; set; } = 10;
}
