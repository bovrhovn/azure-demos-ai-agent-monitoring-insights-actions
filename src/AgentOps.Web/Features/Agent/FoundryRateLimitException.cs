namespace AgentOps.Web.Features.Agent;

public sealed class FoundryRateLimitException(Exception innerException) : Exception(
    "The Foundry model deployment is temporarily rate limited. Wait before trying again, or configure " +
    "Foundry:ModelDeployment with a deployment that has sufficient request and token quota.", innerException);
