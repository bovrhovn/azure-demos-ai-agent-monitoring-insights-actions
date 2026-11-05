namespace AgentOps.Web.Infrastructure.Options;

/// <summary>Foundry model inputs shared by the factory and conversational runner.</summary>
public sealed class FoundryOptions
{
    public const string Section = "Foundry";
    public string ProjectEndpoint { get; set; } = "";
    public string ModelDeployment { get; set; } = "";
    public string AgentName { get; set; } = "insights-agent";
}
