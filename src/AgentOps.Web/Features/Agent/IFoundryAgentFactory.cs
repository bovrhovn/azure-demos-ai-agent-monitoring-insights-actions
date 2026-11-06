using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace AgentOps.Web.Features.Agent;

/// <summary>Creates a Foundry-backed Microsoft Agent Framework agent when behavior is supplied.</summary>
public interface IFoundryAgentFactory
{
    AIAgent Create(string instructions, IList<AITool>? tools = null, AgentPurpose purpose = AgentPurpose.Chat);
}

public enum AgentPurpose { Chat, Investigation }
