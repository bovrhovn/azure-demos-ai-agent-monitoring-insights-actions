namespace AgentOps.Web.Features.Notifications;

public sealed record NotificationRecord(string Id, string RawData, string Message, DateTimeOffset ReceivedDate)
{
    public bool IsHandled { get; init; }
    public string? Comment { get; init; }
    public string? ChatSessionId { get; init; }
    public string? ChatOwnerId { get; init; }
}

public sealed record NotificationPage(IReadOnlyList<NotificationRecord> Items, string? ContinuationToken);

public interface INotificationStore
{
    Task CreateAsync(NotificationRecord notification, CancellationToken cancellationToken);
    Task<NotificationRecord?> GetAsync(string id, CancellationToken cancellationToken);
    Task<NotificationPage> ListAsync(string? search, bool? isHandled, bool newestFirst, int limit,
        string? continuationToken, CancellationToken cancellationToken);
    Task<NotificationRecord?> SetHandledAsync(string id, bool isHandled, string? comment, CancellationToken cancellationToken);
    Task<NotificationRecord?> AssignSessionAsync(string id, string ownerId, string sessionId, CancellationToken cancellationToken);
}
