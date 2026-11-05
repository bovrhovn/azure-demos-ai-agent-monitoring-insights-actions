using System.Collections.Concurrent;
using AgentOps.Web.Features.Chat;

namespace AgentOps.Web.Features.Notifications;

public sealed class NotificationService(INotificationStore notifications, IChatStore chats, ChatService chat,
    ILogger<NotificationService> logger)
{
    private readonly ConcurrentDictionary<string, byte> active = new();

    public async Task<ChatSession?> StartChatAsync(string id, string owner, CancellationToken cancellationToken)
    {
        if (!active.TryAdd(id, 0)) throw new ChatBusyException();
        try
        {
            // Reserve the association first so a failed session creation can be retried without orphaning chats.
            var notification = await notifications.AssignSessionAsync(id, owner, Guid.NewGuid().ToString("N"), cancellationToken);
            if (notification is null) return null;
            if (notification.ChatOwnerId != owner) throw new NotificationOwnershipException();
            var sessionId = notification.ChatSessionId!;
            var session = await chats.GetAsync(owner, sessionId, cancellationToken);
            if (session?.IsDeleting == true) throw new ChatBusyException();
            return session ?? await chat.CreateAsync(owner, cancellationToken, notification.Message, sessionId);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Opening a chat for notification {NotificationId} failed", id);
            throw;
        }
        finally { active.TryRemove(id, out _); }
    }
}

public sealed class NotificationOwnershipException : Exception;
