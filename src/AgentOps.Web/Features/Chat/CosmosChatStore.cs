using System.Net;
using AgentOps.Web.Infrastructure;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Options;

namespace AgentOps.Web.Features.Chat;

public sealed class CosmosChatStore(CosmosClient client, IOptions<CosmosOptions> cosmos, IOptions<ChatOptions> options) : IChatStore
{
    private readonly Container container = client.GetContainer(cosmos.Value.Database, options.Value.Container);

    public async Task CreateAsync(ChatSession session, CancellationToken cancellationToken) =>
        await container.CreateItemAsync(session, new PartitionKey(session.OwnerId), cancellationToken: cancellationToken);

    public async Task<ChatSession?> GetAsync(string ownerId, string sessionId, CancellationToken cancellationToken)
    {
        try
        {
            var session = (await container.ReadItemAsync<ChatSession>(sessionId, new PartitionKey(ownerId),
                cancellationToken: cancellationToken)).Resource;
            return session.Kind == "session" ? session : null;
        }
        catch (CosmosException exception) when (exception.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task<ChatPage<ChatSession>> ListAsync(string ownerId, int limit, string? continuationToken,
        CancellationToken cancellationToken)
    {
        var query = new QueryDefinition("SELECT * FROM c WHERE c.kind = 'session' ORDER BY c.updatedAt DESC");
        using var iterator = container.GetItemQueryIterator<ChatSession>(query, continuationToken,
            new QueryRequestOptions { PartitionKey = new PartitionKey(ownerId), MaxItemCount = limit });
        var page = await iterator.ReadNextAsync(cancellationToken);
        return new(page.ToArray(), page.ContinuationToken);
    }

    public async Task<ChatHistory> MessagesAsync(string ownerId, string sessionId, int limit, int? before,
        CancellationToken cancellationToken)
    {
        var query = new QueryDefinition(
            "SELECT TOP @limit * FROM c WHERE c.kind = 'message' AND c.sessionId = @sessionId AND c.sequence < @before ORDER BY c.sequence DESC")
            .WithParameter("@limit", limit + 1).WithParameter("@sessionId", sessionId)
            .WithParameter("@before", before ?? int.MaxValue);
        using var iterator = container.GetItemQueryIterator<ChatMessage>(query,
            requestOptions: new QueryRequestOptions { PartitionKey = new PartitionKey(ownerId), MaxItemCount = limit + 1 });
        List<ChatMessage> messages = [];
        while (iterator.HasMoreResults && messages.Count <= limit)
            messages.AddRange(await iterator.ReadNextAsync(cancellationToken));
        return new(messages.Take(limit).Reverse().ToArray(), messages.Count > limit);
    }

    public async Task AppendAsync(ChatSession session, ChatMessage message, CancellationToken cancellationToken)
    {
        using var response = await container.CreateTransactionalBatch(new PartitionKey(session.OwnerId))
            .ReplaceItem(session.Id, session).CreateItem(message).ExecuteAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new CosmosException("The chat message and session could not be saved.", response.StatusCode,
                0, response.ActivityId, response.RequestCharge);
    }
}
