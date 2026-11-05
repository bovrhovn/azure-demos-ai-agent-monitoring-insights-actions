using System.ComponentModel.DataAnnotations;

namespace AgentOps.Web.Infrastructure.Options;

/// <summary>Application Insights querying, distinct from telemetry ingestion.</summary>
public sealed class InsightsOptions
{
    public const string Section = "Insights";
    public string ResourceId { get; set; } = "";
    [Range(1, 10080)] public int LookbackMinutes { get; set; } = 60;
    [Range(1, 200)] public int LogLimit { get; set; } = 50;
}
