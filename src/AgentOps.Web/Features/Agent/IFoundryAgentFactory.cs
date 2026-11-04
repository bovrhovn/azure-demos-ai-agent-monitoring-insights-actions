using Microsoft.Agents.AI;

namespace AgentOps.Web.Features.Agent;

/// <summary>Creates a Foundry-backed Microsoft Agent Framework agent when behavior is supplied.</summary>
public interface IFoundryAgentFactory
{
    AIAgent Create(string instructions);
}
