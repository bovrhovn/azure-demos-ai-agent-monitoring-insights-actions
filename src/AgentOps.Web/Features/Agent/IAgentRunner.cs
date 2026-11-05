using AgentOps.Web.Features.Executions;

namespace AgentOps.Web.Features.Agent;

public interface IAgentRunner
{
    Task<AgentOutcome> RunAsync(ExecutionRecord execution, CancellationToken cancellationToken);
}
