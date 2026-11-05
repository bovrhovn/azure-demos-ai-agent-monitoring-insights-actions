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
    public AIAgent Create(string instructions, IList<AITool>? tools = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instructions);
        var settings = options.Value;
        if (!Uri.TryCreate(settings.ProjectEndpoint, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps || string.IsNullOrWhiteSpace(settings.ModelDeployment))
        {
            logger.LogWarning("Foundry agent creation rejected because the project endpoint or model deployment is not configured");
            throw new FoundryConfigurationException();
        }
        logger.LogInformation("Creating Foundry agent {AgentName} for deployment {ModelDeployment}", settings.AgentName, settings.ModelDeployment);
        return new AIProjectClient(uri, credential).AsAIAgent(
            model: settings.ModelDeployment, name: settings.AgentName, instructions: instructions, tools: tools,
            clientFactory: client => client.AsBuilder().UseFunctionInvocation(configure: invocation =>
            {
                invocation.AllowConcurrentInvocation = false;
                invocation.IncludeDetailedErrors = false;
                invocation.MaximumIterationsPerRequest = toolOptions.Value.MaxToolCalls;
            }).Build());
    }
}
