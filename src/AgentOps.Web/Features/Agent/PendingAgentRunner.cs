using AgentOps.Web.Features.Executions;

namespace AgentOps.Web.Features.Agent;

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
