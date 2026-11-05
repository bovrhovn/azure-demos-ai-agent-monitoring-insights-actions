using System.Collections.Concurrent;
using AgentOps.Web.Infrastructure;
using AgentOps.Web.Infrastructure.Options;
using Microsoft.Extensions.Options;

namespace AgentOps.Web.Features.Chat;

public sealed class ChatService(IChatStore store, IChatAgent agent, TimeProvider time, IOptions<ChatOptions> options,
    ILogger<ChatService> logger)
{
    private readonly ConcurrentDictionary<(string Owner, string Session), byte> active = new();

    public async Task<ChatSession> CreateAsync(string owner, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var session = new ChatSession(Guid.NewGuid().ToString("N"), owner, "New conversation", now, now);
        logger.LogInformation("Creating chat session {SessionId}", session.Id);
        try
        {
            await store.CreateAsync(session, cancellationToken);
            logger.LogInformation("Chat session {SessionId} created", session.Id);
            return session;
        }
        catch (OperationCanceledException exception)
        {
            logger.LogWarning(exception, "Creating chat session {SessionId} was canceled", session.Id);
            throw;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Creating chat session {SessionId} failed", session.Id);
            throw;
        }
    }

    public async Task<ChatMessage?> SendAsync(string owner, string sessionId, string prompt, CancellationToken cancellationToken)
    {
        if (!active.TryAdd((owner, sessionId), 0))
        {
            logger.LogWarning("Chat session {SessionId} already has an operation in progress", sessionId);
            throw new ChatBusyException();
        }
        try
        {
            logger.LogInformation("Sending chat message in session {SessionId}", sessionId);
            var session = await store.GetAsync(owner, sessionId, cancellationToken);
            if (session is null)
            {
                logger.LogWarning("Chat session {SessionId} was not found for sending", sessionId);
                return null;
            }
            if (session.IsDeleting) throw new ChatBusyException();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(options.Value.TimeoutSeconds));
            var history = await store.MessagesAsync(owner, sessionId, options.Value.HistoryLimit, null, timeout.Token);
            logger.LogInformation("Loaded {HistoryCount} context messages for chat session {SessionId}", history.Items.Count, sessionId);
            var message = new ChatMessage(Guid.NewGuid().ToString("N"), owner, sessionId, "user", prompt,
                time.GetUtcNow(), session.MessageCount + 1);
            session = session with
            {
                Title = session.MessageCount == 0 ? prompt[..Math.Min(prompt.Length, ChatSession.TitleMaxLength)] : session.Title,
                UpdatedAt = message.Timestamp,
                MessageCount = message.Sequence
            };
            await store.AppendAsync(session, message, timeout.Token);
            logger.LogInformation("User message {MessageId} saved in chat session {SessionId}; invoking agent", message.Id, sessionId);
            var reply = await agent.ReplyAsync(history.Items.Append(message).ToArray(), timeout.Token);
            var response = new ChatMessage(Guid.NewGuid().ToString("N"), owner, sessionId, "agent", reply.Content,
                time.GetUtcNow(), message.Sequence + 1, reply.Model) { Evidence = reply.Evidence };
            // A completed reply is committed even if the browser disconnects during finalization.
            using var finalization = new CancellationTokenSource(TimeSpan.FromSeconds(options.Value.FinalizationTimeoutSeconds));
            await store.AppendAsync(session with { UpdatedAt = response.Timestamp, MessageCount = response.Sequence },
                response, finalization.Token);
            logger.LogInformation("Agent message {MessageId} saved in chat session {SessionId} using model {Model}",
                response.Id, sessionId, response.Model);
            return response;
        }
        catch (ChatBusyException)
        {
            logger.LogWarning("Chat session {SessionId} is being deleted; sending was rejected", sessionId);
            throw;
        }
        catch (OperationCanceledException exception)
        {
            logger.LogWarning(exception, "Sending a message in chat session {SessionId} was canceled or timed out", sessionId);
            throw;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Sending a message in chat session {SessionId} failed", sessionId);
            throw;
        }
        finally
        {
            active.TryRemove((owner, sessionId), out _);
        }
    }

    public async Task<bool> DeleteAsync(string owner, string sessionId, CancellationToken cancellationToken)
    {
        if (!active.TryAdd((owner, sessionId), 0))
        {
            logger.LogWarning("Chat session {SessionId} already has an operation in progress; deletion was rejected", sessionId);
            throw new ChatBusyException();
        }
        try
        {
            logger.LogInformation("Deleting chat session {SessionId} and its messages", sessionId);
            var session = await store.GetAsync(owner, sessionId, cancellationToken);
            if (session is null)
            {
                logger.LogWarning("Chat session {SessionId} was not found for deletion", sessionId);
                return false;
            }
            await store.DeleteAsync(session, cancellationToken);
            logger.LogInformation("Chat session {SessionId} and its messages deleted", sessionId);
            return true;
        }
        catch (OperationCanceledException exception)
        {
            logger.LogWarning(exception, "Deleting chat session {SessionId} was canceled; deletion can be retried", sessionId);
            throw;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Deleting chat session {SessionId} failed; deletion can be retried", sessionId);
            throw;
        }
        finally
        {
            active.TryRemove((owner, sessionId), out _);
        }
    }
}

public sealed class ChatBusyException : Exception;
