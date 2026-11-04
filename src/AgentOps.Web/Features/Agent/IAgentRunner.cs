namespace AgentOps.Web.Features.Agent;

public interface IAgentRunner
{
    Task<AgentOutcome> RunAsync(CancellationToken cancellationToken);
}
