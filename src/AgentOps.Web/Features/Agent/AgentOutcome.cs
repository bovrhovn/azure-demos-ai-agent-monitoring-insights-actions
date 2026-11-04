namespace AgentOps.Web.Features.Agent;

/// <summary>Outcome returned by the agent implementation, independently of its trigger.</summary>
public sealed record AgentOutcome(string Status, string Summary);
