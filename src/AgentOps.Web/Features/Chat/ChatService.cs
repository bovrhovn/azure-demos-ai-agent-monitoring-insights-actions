using System.Collections.Concurrent;
using AgentOps.Web.Infrastructure;
using Microsoft.Extensions.Options;

namespace AgentOps.Web.Features.Chat;

public sealed class ChatService(IChatStore store, IChatAgent agent, TimeProvider time, IOptions<ChatOptions> options)
{
    private readonly ConcurrentDictionary<(string Owner, string Session), byte> active = new();

    public async Task<ChatSession> CreateAsync(string owner, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var session = new ChatSession(Guid.NewGuid().ToString("N"), owner, "New conversation", now, now);
        await store.CreateAsync(session, cancellationToken);
        return session;
    }

    public async Task<ChatMessage?> SendAsync(string owner, string sessionId, string prompt, CancellationToken cancellationToken)
    {
        if (!active.TryAdd((owner, sessionId), 0))
            throw new ChatBusyException();
        try
        {
            var session = await store.GetAsync(owner, sessionId, cancellationToken);
            if (session is null) return null;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(options.Value.TimeoutSeconds));
            var history = await store.MessagesAsync(owner, sessionId, options.Value.HistoryLimit, null, timeout.Token);
            var message = new ChatMessage(Guid.NewGuid().ToString("N"), owner, sessionId, "user", prompt,
                time.GetUtcNow(), session.MessageCount + 1);
            session = session with
            {
                Title = session.MessageCount == 0 ? prompt[..Math.Min(prompt.Length, ChatSession.TitleMaxLength)] : session.Title,
                UpdatedAt = message.Timestamp,
                MessageCount = message.Sequence
            };
            await store.AppendAsync(session, message, timeout.Token);
            var reply = await agent.ReplyAsync(history.Items.Append(message).ToArray(), timeout.Token);
            var response = new ChatMessage(Guid.NewGuid().ToString("N"), owner, sessionId, "agent", reply.Content,
                time.GetUtcNow(), message.Sequence + 1, reply.Model);
            // A completed reply is committed even if the browser disconnects during finalization.
            using var finalization = new CancellationTokenSource(TimeSpan.FromSeconds(options.Value.FinalizationTimeoutSeconds));
            await store.AppendAsync(session with { UpdatedAt = response.Timestamp, MessageCount = response.Sequence },
                response, finalization.Token);
            return response;
        }
        finally
        {
            active.TryRemove((owner, sessionId), out _);
        }
    }
}

public sealed class ChatBusyException : Exception;
