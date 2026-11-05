using AgentOps.Web.Features.Insights;

namespace AgentOps.Web.Features.Chat;

public sealed record ChatSession(string Id, string OwnerId, string Title, DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt, int MessageCount = 0)
{
    public const int TitleMaxLength = 60;
    public string Kind { get; init; } = "session";
    public bool IsDeleting { get; init; }
    public string? InitialDraft { get; init; }
}

public sealed record ChatMessage(string Id, string OwnerId, string SessionId, string Role, string Content,
    DateTimeOffset Timestamp, int Sequence, string? Model = null)
{
    public string Kind { get; init; } = "message";
    public IReadOnlyList<TelemetryEvidence> Evidence { get; init; } = [];
}

public sealed record ChatPage<T>(IReadOnlyList<T> Items, string? ContinuationToken);
public sealed record ChatHistory(IReadOnlyList<ChatMessage> Items, bool HasMore);
public sealed record ChatReply(string Content, string Model)
{
    public IReadOnlyList<TelemetryEvidence> Evidence { get; init; } = [];
}

public interface IChatStore
{
    Task CreateAsync(ChatSession session, CancellationToken cancellationToken);
    Task<ChatSession?> GetAsync(string ownerId, string sessionId, CancellationToken cancellationToken);
    Task<ChatPage<ChatSession>> ListAsync(string ownerId, int limit, string? continuationToken, CancellationToken cancellationToken);
    Task<ChatHistory> MessagesAsync(string ownerId, string sessionId, int limit, int? before, CancellationToken cancellationToken);
    Task AppendAsync(ChatSession session, ChatMessage message, CancellationToken cancellationToken);
    Task DeleteAsync(ChatSession session, CancellationToken cancellationToken);
}

public interface IChatAgent
{
    Task<ChatReply> ReplyAsync(IReadOnlyList<ChatMessage> history, CancellationToken cancellationToken);
}
