using System.ComponentModel.DataAnnotations;

namespace AgentOps.Web.Infrastructure.Options;

/// <summary>Page sizes for the bounded analytics views.</summary>
public sealed class AnalyticsOptions
{
    public const string Section = "Analytics";
    [Range(1, 200)] public int PageSize { get; set; } = 10;
    [Range(1, 200)] public int MaxPageSize { get; set; } = 50;
}
