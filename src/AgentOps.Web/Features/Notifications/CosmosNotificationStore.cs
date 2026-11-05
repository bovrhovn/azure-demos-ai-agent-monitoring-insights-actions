using System.Net;
using AgentOps.Web.Infrastructure.Options;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Options;

namespace AgentOps.Web.Features.Notifications;

public sealed class CosmosNotificationStore(CosmosClient client, IOptions<CosmosOptions> cosmos,
    IOptions<NotificationOptions> options, ILogger<CosmosNotificationStore> logger) : INotificationStore
{
    private readonly Container container = client.GetContainer(cosmos.Value.Database, options.Value.Container);

    public async Task CreateAsync(NotificationRecord notification, CancellationToken cancellationToken)
    {
        try
        {
            await container.CreateItemAsync(notification, new PartitionKey(notification.Id), cancellationToken: cancellationToken);
        }
        catch (CosmosException exception) when (exception.StatusCode == HttpStatusCode.Conflict)
        {
            // Event Grid retries must not reset handled state, comments or the linked session.
            logger.LogInformation("Notification {NotificationId} was already received", notification.Id);
        }
    }

    public async Task<NotificationRecord?> GetAsync(string id, CancellationToken cancellationToken)
    {
        try
        {
            return (await container.ReadItemAsync<NotificationRecord>(id, new PartitionKey(id),
                cancellationToken: cancellationToken)).Resource;
        }
        catch (CosmosException exception) when (exception.StatusCode == HttpStatusCode.NotFound)
        {
            logger.LogDebug("Notification {NotificationId} was not found", id);
            return null;
        }
    }

    public async Task<NotificationPage> ListAsync(string? search, bool? isHandled, bool newestFirst, int limit,
        string? continuationToken, CancellationToken cancellationToken)
    {
        var predicates = new List<string>();
        if (!string.IsNullOrEmpty(search)) predicates.Add("CONTAINS(c.message, @search, true)");
        if (isHandled.HasValue) predicates.Add("c.isHandled = @isHandled");
        var where = predicates.Count == 0 ? "" : " WHERE " + string.Join(" AND ", predicates);
        var query = new QueryDefinition($"SELECT * FROM c{where} ORDER BY c.receivedDate {(newestFirst ? "DESC" : "ASC")}");
        if (!string.IsNullOrEmpty(search)) query.WithParameter("@search", search);
        if (isHandled.HasValue) query.WithParameter("@isHandled", isHandled.Value);
        using var iterator = container.GetItemQueryIterator<NotificationRecord>(query,
            string.IsNullOrEmpty(continuationToken) ? null : continuationToken, new QueryRequestOptions { MaxItemCount = limit });
        var page = await iterator.ReadNextAsync(cancellationToken);
        return new(page.ToArray(), page.ContinuationToken);
    }

    public Task<NotificationRecord?> SetHandledAsync(string id, bool isHandled, string? comment, CancellationToken cancellationToken)
    {
        List<PatchOperation> operations = [PatchOperation.Set("/isHandled", isHandled)];
        if (isHandled) operations.Add(PatchOperation.Set("/comment", comment));
        return PatchAsync(id, operations, null, cancellationToken);
    }

    public async Task<NotificationRecord?> AssignSessionAsync(string id, string ownerId, string sessionId,
        CancellationToken cancellationToken)
    {
        try
        {
            return await PatchAsync(id,
                [PatchOperation.Set("/chatSessionId", sessionId), PatchOperation.Set("/chatOwnerId", ownerId)],
                new PatchItemRequestOptions { FilterPredicate = "FROM c WHERE IS_NULL(c.chatSessionId) OR NOT IS_DEFINED(c.chatSessionId)" },
                cancellationToken);
        }
        catch (CosmosException exception) when (exception.StatusCode == HttpStatusCode.PreconditionFailed)
        {
            logger.LogInformation("Notification {NotificationId} already has a linked session", id);
            return await GetAsync(id, cancellationToken);
        }
    }

    private async Task<NotificationRecord?> PatchAsync(string id, IReadOnlyList<PatchOperation> operations,
        PatchItemRequestOptions? options, CancellationToken cancellationToken)
    {
        try
        {
            return (await container.PatchItemAsync<NotificationRecord>(id, new PartitionKey(id), operations,
                options, cancellationToken)).Resource;
        }
        catch (CosmosException exception) when (exception.StatusCode == HttpStatusCode.NotFound)
        {
            logger.LogDebug("Notification {NotificationId} was not found for update", id);
            return null;
        }
    }
}
