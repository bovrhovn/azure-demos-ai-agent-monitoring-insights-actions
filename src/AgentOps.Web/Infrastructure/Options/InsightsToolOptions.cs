using System.ComponentModel.DataAnnotations;

namespace AgentOps.Web.Infrastructure;

public sealed class InsightsToolOptions
{
    public const string Section = "InsightsTools";
    public bool Enabled { get; set; } = true;
    [Range(1, 10080)] public int DefaultLookbackMinutes { get; set; } = 60;
    [Range(1, 10080)] public int MaxLookbackMinutes { get; set; } = 1440;
    [Range(1, 100)] public int DefaultCount { get; set; } = 5;
    [Range(1, 100)] public int MaxRows { get; set; } = 20;
    [Range(1, 120)] public int QueryTimeoutSeconds { get; set; } = 30;
    [Range(1, 12)] public int MaxToolCalls { get; set; } = 6;
    [Range(1024, 64000)] public int MaxResultCharacters { get; set; } = 16000;
    [Range(128, 8000)] public int MaxFieldCharacters { get; set; } = 4000;
    [Range(1, 200)] public int MaxEvidenceReferences { get; set; } = 100;
}
