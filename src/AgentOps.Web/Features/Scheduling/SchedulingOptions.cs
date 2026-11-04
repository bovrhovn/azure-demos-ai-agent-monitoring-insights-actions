using System.ComponentModel.DataAnnotations;

namespace AgentOps.Web.Features.Scheduling;

/// <summary>Validated recurring UTC execution settings, reapplied on each startup.</summary>
public sealed class SchedulingOptions
{
    public const string Section = "Scheduling";
    public bool Enabled { get; set; } = true;
    [Required] public string RecurringJobId { get; set; } = "insights-agent";
    [Required] public string Cron { get; set; } = "*/15 * * * *";
}
