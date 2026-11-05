using System.Net;
using AgentOps.Web.Infrastructure;
using AgentOps.Web.Infrastructure.Options;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Options;

namespace AgentOps.Web.Features.Executions;

/// <summary>Uses trigger partitions to keep bounded history queries single-partition.</summary>
public sealed class CosmosExecutionStore(CosmosClient client, IOptions<CosmosOptions> options) : IExecutionStore
{
    private readonly Container container = client.GetContainer(options.Value.Database, options.Value.Container);

    public async Task SaveAsync(ExecutionRecord execution, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(execution);
        await container.UpsertItemAsync(execution, new PartitionKey(execution.Trigger), cancellationToken: cancellationToken);
    }

    public async Task<IReadOnlyList<ExecutionRecord>> ListAsync(string trigger, int limit, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        var query = ExecutionQueries.History(trigger, limit);
        using var iterator = container.GetItemQueryIterator<ExecutionRecord>(query,
            requestOptions: new QueryRequestOptions { PartitionKey = new PartitionKey(trigger), MaxItemCount = limit });
        List<ExecutionRecord> records = [];
        while (iterator.HasMoreResults && records.Count < limit)
            records.AddRange(await iterator.ReadNextAsync(cancellationToken));
        return records;
    }

    public async Task<ExecutionRecord?> GetAsync(string id, string trigger, CancellationToken cancellationToken)
    {
        try
        {
            return (await container.ReadItemAsync<ExecutionRecord>(id, new PartitionKey(trigger),
                cancellationToken: cancellationToken)).Resource;
        }
        catch (CosmosException exception) when (exception.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }
}
