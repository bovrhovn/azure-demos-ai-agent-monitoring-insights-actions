namespace AgentOps.Web.Features.Agent;

public sealed class FoundryConfigurationException() : InvalidOperationException(
    "Configure Foundry:ProjectEndpoint with an HTTPS project endpoint and Foundry:ModelDeployment with an existing model deployment before using chat.");
