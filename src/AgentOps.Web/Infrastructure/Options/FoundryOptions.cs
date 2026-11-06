using System.ComponentModel.DataAnnotations;

namespace AgentOps.Web.Infrastructure.Options;

/// <summary>Foundry model inputs shared by the factory and conversational runner.</summary>
public sealed class FoundryOptions
{
    public const string Section = "Foundry";
    public string ProjectEndpoint { get; set; } = "";
    public string ModelDeployment { get; set; } = "";
    [Required, StringLength(128)]
    public string AgentName { get; set; } = "insights-agent";
    [Required, StringLength(128)]
    public string InvestigationAgentName { get; set; } = "insights-investigation";
}
