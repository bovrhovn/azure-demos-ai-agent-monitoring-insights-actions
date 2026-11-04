using System.Net;
using AgentOps.Web.Infrastructure;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Options;

namespace AgentOps.Web.Features.Chat;

public sealed class CosmosChatStore(CosmosClient client, IOptions<CosmosOptions> cosmos, IOptions<ChatOptions> options,
    ILogger<CosmosChatStore> logger) : IChatStore
{
    private const int MaxBatchOperations = 100;
    private readonly Container container = client.GetContainer(cosmos.Value.Database, options.Value.Container);

    public async Task CreateAsync(ChatSession session, CancellationToken cancellationToken)
    {
        await container.CreateItemAsync(session, new PartitionKey(session.OwnerId), cancellationToken: cancellationToken);
        logger.LogDebug("Created Cosmos chat session document {SessionId}", session.Id);
    }

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
            logger.LogDebug("Cosmos chat session {SessionId} was not found", sessionId);
            return null;
        }
    }

    public async Task<ChatPage<ChatSession>> ListAsync(string ownerId, int limit, string? continuationToken,
        CancellationToken cancellationToken)
    {
        try
        {
            var query = new QueryDefinition("SELECT * FROM c WHERE c.kind = 'session' ORDER BY c.updatedAt DESC");
            using var iterator = container.GetItemQueryIterator<ChatSession>(query, continuationToken,
                new QueryRequestOptions { PartitionKey = new PartitionKey(ownerId), MaxItemCount = limit });
            var page = await iterator.ReadNextAsync(cancellationToken);
            logger.LogInformation("Loaded {SessionCount} Cosmos chat sessions; more pages: {HasMore}", page.Count, page.ContinuationToken is not null);
            return new(page.ToArray(), page.ContinuationToken);
        }
        catch (CosmosException exception)
        {
            logger.LogError(exception, "Loading Cosmos chat sessions failed with status {StatusCode}", exception.StatusCode);
            throw;
        }
    }

    public async Task<ChatHistory> MessagesAsync(string ownerId, string sessionId, int limit, int? before,
        CancellationToken cancellationToken)
    {
        try
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
            logger.LogInformation("Loaded {MessageCount} Cosmos messages for chat session {SessionId}", Math.Min(messages.Count, limit), sessionId);
            return new(messages.Take(limit).Reverse().ToArray(), messages.Count > limit);
        }
        catch (CosmosException exception)
        {
            logger.LogError(exception, "Loading messages for Cosmos chat session {SessionId} failed with status {StatusCode}", sessionId, exception.StatusCode);
            throw;
        }
    }

    public async Task AppendAsync(ChatSession session, ChatMessage message, CancellationToken cancellationToken)
    {
        using var response = await container.CreateTransactionalBatch(new PartitionKey(session.OwnerId))
            .ReplaceItem(session.Id, session).CreateItem(message).ExecuteAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new CosmosException("The chat message and session could not be saved.", response.StatusCode,
                0, response.ActivityId, response.RequestCharge);
        logger.LogDebug("Saved Cosmos chat message {MessageId} with role {Role} in session {SessionId}",
            message.Id, message.Role, session.Id);
    }

    public async Task DeleteAsync(ChatSession session, CancellationToken cancellationToken)
    {
        var partition = new PartitionKey(session.OwnerId);
        if (!session.IsDeleting)
            await container.ReplaceItemAsync(session with { IsDeleting = true }, session.Id, partition, cancellationToken: cancellationToken);
        var deletedCount = 0;
        while (true)
        {
            var query = new QueryDefinition(
                "SELECT TOP @limit VALUE c.id FROM c WHERE c.kind = 'message' AND c.sessionId = @sessionId")
                .WithParameter("@limit", MaxBatchOperations).WithParameter("@sessionId", session.Id);
            List<string> ids = [];
            using (var iterator = container.GetItemQueryIterator<string>(query,
                requestOptions: new QueryRequestOptions { PartitionKey = partition, MaxItemCount = MaxBatchOperations }))
            {
                while (iterator.HasMoreResults && ids.Count < MaxBatchOperations)
                    ids.AddRange(await iterator.ReadNextAsync(cancellationToken));
            }
            if (ids.Count == 0) break;
            var batch = container.CreateTransactionalBatch(partition);
            foreach (var id in ids) batch.DeleteItem(id);
            using var response = await batch.ExecuteAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
                throw new CosmosException("The chat messages could not be deleted.", response.StatusCode,
                    0, response.ActivityId, response.RequestCharge);
            deletedCount += ids.Count;
            logger.LogInformation("Deleted {BatchCount} Cosmos messages for chat session {SessionId}; total removed: {DeletedCount}",
                ids.Count, session.Id, deletedCount);
        }
        await container.DeleteItemAsync<ChatSession>(session.Id, partition, cancellationToken: cancellationToken);
        logger.LogInformation("Deleted Cosmos chat session {SessionId} after removing {DeletedCount} messages", session.Id, deletedCount);
    }
}
