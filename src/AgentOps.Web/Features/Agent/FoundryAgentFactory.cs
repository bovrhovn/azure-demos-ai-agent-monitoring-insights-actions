using AgentOps.Web.Infrastructure;
using AgentOps.Web.Features.Executions;
using Azure.AI.Projects;
using Azure.Core;
using Microsoft.Agents.AI;
using Microsoft.Extensions.Options;

namespace AgentOps.Web.Features.Agent;

/// <summary>Creates a Foundry-backed Microsoft Agent Framework agent when behavior is supplied.</summary>
public interface IFoundryAgentFactory
{
    AIAgent Create(string instructions);
}

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

/// <summary>Outcome returned by the agent implementation, independently of its trigger.</summary>
public sealed record AgentOutcome(string Status, string Summary);

public interface IAgentRunner
{
    Task<AgentOutcome> RunAsync(CancellationToken cancellationToken);
}

/// <summary>Intentional scaffold. Does not create resources or invoke a billable model.</summary>
public sealed class PendingAgentRunner(ILogger<PendingAgentRunner> logger) : IAgentRunner
{
    public Task<AgentOutcome> RunAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // TODO: Define the agent instructions, tools, input selection and safe action policy.
        // TODO: Create the agent with IFoundryAgentFactory and implement its run/session lifecycle.
        logger.LogWarning("Agent behavior is not implemented; no Foundry invocation was made");
        return Task.FromResult(new AgentOutcome(ExecutionContract.NotImplemented,
            "Agent behavior is pending implementation. No model or action was invoked."));
    }
}
