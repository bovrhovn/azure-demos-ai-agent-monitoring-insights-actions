using AgentOps.Web.Infrastructure;
using AgentOps.Web.Infrastructure.Options;
using Azure.AI.Projects;
using Azure.Core;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace AgentOps.Web.Features.Agent;

/// <summary>Factory boundary keeps deployment details out of execution handlers.</summary>
public sealed class FoundryAgentFactory(TokenCredential credential, IOptions<FoundryOptions> options,
    ILogger<FoundryAgentFactory> logger, IOptions<InsightsToolOptions> toolOptions) : IFoundryAgentFactory
{
    public AIAgent Create(string instructions, IList<AITool>? tools = null, AgentPurpose purpose = AgentPurpose.Chat)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instructions);
        var settings = options.Value;
        if (!Uri.TryCreate(settings.ProjectEndpoint, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps || string.IsNullOrWhiteSpace(settings.ModelDeployment))
        {
            logger.LogWarning("Foundry agent creation rejected because the project endpoint or model deployment is not configured");
            throw new FoundryConfigurationException();
        }
        var name = purpose == AgentPurpose.Investigation ? settings.InvestigationAgentName : settings.AgentName;
        logger.LogInformation("Creating Foundry agent {AgentName} for deployment {ModelDeployment}", name, settings.ModelDeployment);
        return AgentTelemetry.InstrumentAgent(new AIProjectClient(uri, credential).AsAIAgent(
            options: new ChatClientAgentOptions
            {
                Id = name,
                Name = name,
                ChatOptions = new() { ModelId = settings.ModelDeployment, Instructions = instructions, Tools = tools },
                UseProvidedChatClientAsIs = true
            },
            clientFactory: client => AgentTelemetry.InstrumentChatClient(client, toolOptions.Value.MaxToolCalls)));
    }
}
