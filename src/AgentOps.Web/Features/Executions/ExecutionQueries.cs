using Microsoft.Azure.Cosmos;

namespace AgentOps.Web.Features.Executions;

internal static class ExecutionQueries
{
    public static QueryDefinition History(string trigger, int limit) =>
        new QueryDefinition("SELECT TOP @limit * FROM c WHERE c.trigger = @trigger ORDER BY c.startedAt DESC")
            .WithParameter("@limit", limit).WithParameter("@trigger", trigger);
}
