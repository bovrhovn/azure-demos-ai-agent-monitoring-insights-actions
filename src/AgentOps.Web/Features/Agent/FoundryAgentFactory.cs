using AgentOps.Web.Infrastructure;
using Azure.AI.Projects;
using Azure.Core;
using Microsoft.Agents.AI;
using Microsoft.Extensions.Options;

namespace AgentOps.Web.Features.Agent;

/// <summary>Factory boundary keeps deployment details out of execution handlers.</summary>
public sealed class FoundryAgentFactory(TokenCredential credential, IOptions<FoundryOptions> options) : IFoundryAgentFactory
{
    public AIAgent Create(string instructions)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instructions);
        var settings = options.Value;
        if (!Uri.TryCreate(settings.ProjectEndpoint, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps || string.IsNullOrWhiteSpace(settings.ModelDeployment))
            throw new InvalidOperationException("Configure Foundry:ProjectEndpoint and Foundry:ModelDeployment before implementing the agent.");
        return new AIProjectClient(uri, credential).AsAIAgent(
            model: settings.ModelDeployment, name: settings.AgentName, instructions: instructions);
    }
}
