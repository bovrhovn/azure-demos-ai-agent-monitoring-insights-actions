namespace AgentOps.Web.Infrastructure;

/// <summary>Foundry factory inputs; agent behavior remains deferred.</summary>
public sealed class FoundryOptions
{
    public const string Section = "Foundry";
    public string ProjectEndpoint { get; set; } = "";
    public string ModelDeployment { get; set; } = "";
    public string AgentName { get; set; } = "insights-agent";
}
