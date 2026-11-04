using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using AgentOps.Web;
using AgentOps.Web.Features.Chat;
using AgentOps.Web.Infrastructure;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AgentOps.Tests;

[TestClass]
public sealed class ChatTests
{
    [TestMethod]
    public async Task DeletingSessionRemovesItsMessagesButPreservesOtherConversations()
    {
        await using var factory = new AppFactory();
        using var client = factory.Client("AgentOps.Operator");
        await AuthorizePosts(client);
        var removed = await Create(client);
        await Send(client, removed.Id, "Remove this conversation");
        var retained = await Create(client);
        await Send(client, retained.Id, "Keep this conversation");
        var response = await client.DeleteAsync($"{RouteConstants.ChatSessions}/{removed.Id}");
        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await response.Content.ReadAsStringAsync()).Should().BeEmpty();
        (await client.GetAsync($"{RouteConstants.ChatSessions}/{removed.Id}/messages")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await client.GetFromJsonAsync<ChatPage<ChatSession>>(RouteConstants.ChatSessions))!.Items
            .Should().ContainSingle().Which.Id.Should().Be(retained.Id);
        factory.Chats.Messages.Should().HaveCount(2).And.OnlyContain(message => message.SessionId == retained.Id);
        (await client.DeleteAsync($"{RouteConstants.ChatSessions}/{removed.Id}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [TestMethod]
    public async Task DeletingRequiresOperatorAntiforgeryAndSessionOwnership()
    {
        await using var factory = new AppFactory();
        using var owner = factory.Client("AgentOps.Operator");
        await AuthorizePosts(owner);
        var session = await Create(owner);
        await Send(owner, session.Id, "Private saved message");
        var path = $"{RouteConstants.ChatSessions}/{session.Id}";
        using var anonymous = factory.Client();
        using var reader = factory.Client("AgentOps.Reader");
        using var noToken = factory.Client("AgentOps.Operator");
        using var other = factory.Client("AgentOps.Operator");
        other.DefaultRequestHeaders.Add("X-Test-User", "another-account");
        await AuthorizePosts(other);
        (await anonymous.DeleteAsync(path)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await reader.DeleteAsync(path)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await noToken.DeleteAsync(path)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await other.DeleteAsync(path)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        factory.Chats.Sessions.Should().ContainSingle();
        factory.Chats.Messages.Should().HaveCount(2);
    }

    [TestMethod]
    public async Task FailedDeletionRemainsRetryableAndBlocksNewMessages()
    {
        await using var factory = new AppFactory();
        using var client = factory.Client("AgentOps.Operator");
        await AuthorizePosts(client);
        var session = await Create(client);
        await Send(client, session.Id, "Existing message");
        factory.Chats.FailDelete = true;
        var path = $"{RouteConstants.ChatSessions}/{session.Id}";
        (await client.DeleteAsync(path)).StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        var sessions = await client.GetFromJsonAsync<ChatPage<ChatSession>>(RouteConstants.ChatSessions);
        sessions!.Items.Should().ContainSingle().Which.IsDeleting.Should().BeTrue();
        (await client.PostAsJsonAsync(path + "/messages", new { content = "Must not run" })).StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await client.GetAsync(path + "/messages")).StatusCode.Should().Be(HttpStatusCode.Conflict);
        factory.ChatAgent.Calls.Should().ContainSingle();
        factory.Chats.FailDelete = false;
        (await client.DeleteAsync(path)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        factory.Chats.Sessions.Should().BeEmpty();
        factory.Chats.Messages.Should().BeEmpty();
    }

    [TestMethod]
    public async Task DeletionIsRejectedWhileAgentReplyIsInFlightAndCanRunAfterwards()
    {
        var store = new MemoryChatStore();
        var agent = new FakeChatAgent { Wait = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        var service = new ChatService(store, agent, TimeProvider.System, Options.Create(new ChatOptions()), NullLogger<ChatService>.Instance);
        var session = await service.CreateAsync("owner", CancellationToken.None);
        var pending = service.SendAsync("owner", session.Id, "In flight", CancellationToken.None);
        await agent.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var delete = () => service.DeleteAsync("owner", session.Id, CancellationToken.None);
        try { await delete.Should().ThrowAsync<ChatBusyException>(); }
        finally { agent.Wait.TrySetResult(); }
        await pending;
        (await service.DeleteAsync("owner", session.Id, CancellationToken.None)).Should().BeTrue();
        store.Sessions.Should().BeEmpty();
        store.Messages.Should().BeEmpty();
        (await service.SendAsync("owner", session.Id, "Deleted", CancellationToken.None)).Should().BeNull();
    }

    [TestMethod]
    public async Task ChatPersistsBothRolesAndSuppliesOrderedHistoryOnSubsequentPrompts()
    {
        await using var factory = new AppFactory();
        using var client = factory.Client("AgentOps.Operator");
        await AuthorizePosts(client);
        var session = await Create(client);
        var first = await Send(client, session.Id, "Remember the colour teal.");
        var second = await Send(client, session.Id, "Which colour?");
        var history = await client.GetFromJsonAsync<ChatHistory>($"{RouteConstants.ChatSessions}/{session.Id}/messages");
        history!.Items.Select(message => message.Role).Should().Equal("user", "agent", "user", "agent");
        history.Items.Select(message => message.Sequence).Should().Equal(1, 2, 3, 4);
        history.Items.Select(message => message.Content).Should().Equal("Remember the colour teal.", first.Content, "Which colour?", second.Content);
        history.Items.Should().OnlyContain(message => message.OwnerId == session.OwnerId && message.SessionId == session.Id);
        history.Items.Should().BeInAscendingOrder(message => message.Timestamp);
        first.Model.Should().Be("test-deployment");
        first.Timestamp.Should().BeOnOrAfter(session.CreatedAt);
        factory.ChatAgent.Calls.Last().Select(message => message.Content)
            .Should().Equal("Remember the colour teal.", first.Content, "Which colour?");
        var sessions = await client.GetFromJsonAsync<ChatPage<ChatSession>>(RouteConstants.ChatSessions);
        sessions!.Items.Should().ContainSingle().Which.Title.Should().Be("Remember the colour teal.");
        sessions.Items[0].MessageCount.Should().Be(4);
    }

    [TestMethod]
    public async Task ChatRejectsAnonymousReaderWritesMissingAntiforgeryAndCrossAccountReadsOrWrites()
    {
        await using var factory = new AppFactory();
        using var anonymous = factory.Client();
        (await anonymous.GetAsync(RouteConstants.ChatPage)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await anonymous.GetAsync(RouteConstants.ChatSessions)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        using var reader = factory.Client("AgentOps.Reader");
        (await reader.PostAsync(RouteConstants.ChatSessions, null)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        using var client = factory.Client("AgentOps.Operator");
        (await client.PostAsync(RouteConstants.ChatSessions, null)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        await AuthorizePosts(client);
        var session = await Create(client);
        await Send(client, session.Id, "private prompt");
        using var other = factory.Client("AgentOps.Operator");
        other.DefaultRequestHeaders.Add("X-Test-User", "different-account");
        await AuthorizePosts(other);
        var list = await other.GetFromJsonAsync<ChatPage<ChatSession>>(RouteConstants.ChatSessions);
        list!.Items.Should().BeEmpty();
        (await other.GetAsync($"{RouteConstants.ChatSessions}/{session.Id}/messages")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await other.PostAsJsonAsync($"{RouteConstants.ChatSessions}/{session.Id}/messages", new { content = "intrusion" }))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
        factory.ChatAgent.Calls.Should().ContainSingle();
    }

    [TestMethod]
    public async Task ChatBoundsHistoryAndSupportsLoadingEarlierMessagesAndSessions()
    {
        await using var factory = new AppFactory(new Dictionary<string, string?>
        {
            ["Chat:HistoryLimit"] = "2", ["Chat:MessagePageSize"] = "2", ["Chat:SessionPageSize"] = "1",
            ["Executions:RequestsPerMinute"] = "20"
        });
        using var client = factory.Client("AgentOps.Operator");
        await AuthorizePosts(client);
        var session = await Create(client);
        await Send(client, session.Id, "First");
        await Send(client, session.Id, "Second");
        await Send(client, session.Id, "Third");
        factory.ChatAgent.Calls.Last().Select(message => message.Sequence).Should().Equal(3, 4, 5);
        var latest = await client.GetFromJsonAsync<ChatHistory>($"{RouteConstants.ChatSessions}/{session.Id}/messages");
        latest!.HasMore.Should().BeTrue();
        latest.Items.Select(message => message.Sequence).Should().Equal(5, 6);
        var earlier = await client.GetFromJsonAsync<ChatHistory>($"{RouteConstants.ChatSessions}/{session.Id}/messages?before=5");
        earlier!.Items.Select(message => message.Sequence).Should().Equal(3, 4);
        await Create(client);
        var firstPage = await client.GetFromJsonAsync<ChatPage<ChatSession>>(RouteConstants.ChatSessions);
        firstPage!.ContinuationToken.Should().NotBeNull();
        var secondPage = await client.GetFromJsonAsync<ChatPage<ChatSession>>($"{RouteConstants.ChatSessions}?continuationToken={firstPage.ContinuationToken}");
        secondPage!.Items.Should().ContainSingle().Which.Id.Should().Be(session.Id);
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("   ")]
    [DataRow("longer than configured limit")]
    public async Task InvalidPromptsAreRejectedWithoutPersistingOrCallingTheAgent(string prompt)
    {
        await using var factory = new AppFactory(new Dictionary<string, string?> { ["Chat:MaxPromptLength"] = "10" });
        using var client = factory.Client("AgentOps.Operator");
        await AuthorizePosts(client);
        var session = await Create(client);
        var response = await client.PostAsJsonAsync($"{RouteConstants.ChatSessions}/{session.Id}/messages", new { content = prompt });
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        factory.Chats.Messages.Should().BeEmpty();
        factory.ChatAgent.Calls.Should().BeEmpty();
    }

    [TestMethod]
    public async Task AgentFailurePreservesPromptSurfacesSafeErrorAndAllowsAnotherTurn()
    {
        await using var factory = new AppFactory();
        factory.ChatAgent.Fail = true;
        using var client = factory.Client("AgentOps.Operator");
        await AuthorizePosts(client);
        var session = await Create(client);
        var response = await client.PostAsJsonAsync($"{RouteConstants.ChatSessions}/{session.Id}/messages", new { content = "Saved before calling" });
        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        (await response.Content.ReadAsStringAsync()).Should().NotContain("private model failure");
        factory.Chats.Messages.Should().ContainSingle().Which.Role.Should().Be("user");
        factory.ChatAgent.Fail = false;
        await Send(client, session.Id, "Continue");
        factory.ChatAgent.Calls.Last().Select(message => message.Content).Should().Equal("Saved before calling", "Continue");
    }

    [TestMethod]
    public async Task ConcurrentSendIsRejectedAndReplyFinalizesAfterCallerCancellation()
    {
        var store = new MemoryChatStore();
        var agent = new FakeChatAgent { Wait = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        var service = new ChatService(store, agent, TimeProvider.System, Options.Create(new ChatOptions()), NullLogger<ChatService>.Instance);
        using var cancellation = new CancellationTokenSource();
        var session = await service.CreateAsync("owner", CancellationToken.None);
        var pending = service.SendAsync("owner", session.Id, "First", cancellation.Token);
        await agent.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var duplicate = () => service.SendAsync("owner", session.Id, "Duplicate", CancellationToken.None);
        await duplicate.Should().ThrowAsync<ChatBusyException>();
        cancellation.Cancel();
        agent.Wait.SetResult();
        var reply = await pending;
        reply!.Role.Should().Be("agent");
        store.Messages.Select(message => message.Role).Should().Equal("user", "agent");
        await service.SendAsync("owner", session.Id, "Next", CancellationToken.None);
        store.Messages.Should().HaveCount(4);
    }

    [TestMethod]
    public async Task FailedPromptPersistenceDoesNotInvokeTheModel()
    {
        var store = new MemoryChatStore { FailAppend = true };
        var agent = new FakeChatAgent();
        var service = new ChatService(store, agent, TimeProvider.System, Options.Create(new ChatOptions()), NullLogger<ChatService>.Instance);
        var session = await service.CreateAsync("owner", CancellationToken.None);
        var send = () => service.SendAsync("owner", session.Id, "Do not lose this", CancellationToken.None);
        await send.Should().ThrowAsync<InvalidOperationException>();
        agent.Calls.Should().BeEmpty();
    }

    internal static async Task AuthorizePosts(HttpClient client)
    {
        var html = await client.GetStringAsync(RouteConstants.ChatPage);
        html.Should().Contain("data-chat").And.Contain($"href=\"{RouteConstants.ChatPage}\"");
        var token = Regex.Match(html, "data-token=\"([^\"]+)\"").Groups[1].Value;
        token.Should().NotBeEmpty();
        client.DefaultRequestHeaders.Remove("RequestVerificationToken");
        client.DefaultRequestHeaders.Add("RequestVerificationToken", WebUtility.HtmlDecode(token));
    }

    private static async Task<ChatSession> Create(HttpClient client)
    {
        var response = await client.PostAsync(RouteConstants.ChatSessions, null);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ChatSession>())!;
    }

    private static async Task<ChatMessage> Send(HttpClient client, string session, string prompt)
    {
        var response = await client.PostAsJsonAsync($"{RouteConstants.ChatSessions}/{session}/messages", new { content = prompt });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ChatMessage>())!;
    }
}

internal sealed class MemoryChatStore : IChatStore
{
    public ConcurrentDictionary<(string Owner, string Id), ChatSession> Sessions { get; } = new();
    public ConcurrentQueue<ChatMessage> Messages { get; } = new();
    public bool FailAppend { get; set; }
    public bool FailDelete { get; set; }
    public Task CreateAsync(ChatSession session, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Sessions[(session.OwnerId, session.Id)] = session;
        return Task.CompletedTask;
    }
    public Task<ChatSession?> GetAsync(string ownerId, string sessionId, CancellationToken cancellationToken) =>
        Task.FromResult(Sessions.GetValueOrDefault((ownerId, sessionId)));
    public Task<ChatPage<ChatSession>> ListAsync(string ownerId, int limit, string? continuationToken, CancellationToken cancellationToken)
    {
        var offset = continuationToken is null ? 0 : int.Parse(continuationToken, System.Globalization.CultureInfo.InvariantCulture);
        var sessions = Sessions.Values.Where(session => session.OwnerId == ownerId).OrderByDescending(session => session.UpdatedAt).ToArray();
        return Task.FromResult(new ChatPage<ChatSession>(sessions.Skip(offset).Take(limit).ToArray(),
            offset + limit < sessions.Length ? (offset + limit).ToString(System.Globalization.CultureInfo.InvariantCulture) : null));
    }
    public Task<ChatHistory> MessagesAsync(string ownerId, string sessionId, int limit, int? before, CancellationToken cancellationToken)
    {
        var messages = Messages.Where(message => message.OwnerId == ownerId && message.SessionId == sessionId &&
            message.Sequence < (before ?? int.MaxValue)).OrderByDescending(message => message.Sequence).ToArray();
        return Task.FromResult(new ChatHistory(messages.Take(limit).Reverse().ToArray(), messages.Length > limit));
    }
    public Task AppendAsync(ChatSession session, ChatMessage message, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (FailAppend) throw new InvalidOperationException("test persistence failure");
        Sessions[(session.OwnerId, session.Id)] = session;
        Messages.Enqueue(message);
        return Task.CompletedTask;
    }

    public Task DeleteAsync(ChatSession session, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Sessions[(session.OwnerId, session.Id)] = session with { IsDeleting = true };
        if (FailDelete) throw new InvalidOperationException("test deletion failure");
        var retained = Messages.Where(message => message.OwnerId != session.OwnerId || message.SessionId != session.Id).ToArray();
        Messages.Clear();
        foreach (var message in retained) Messages.Enqueue(message);
        Sessions.TryRemove((session.OwnerId, session.Id), out _);
        return Task.CompletedTask;
    }
}

internal sealed class FakeChatAgent : IChatAgent
{
    public ConcurrentQueue<IReadOnlyList<ChatMessage>> Calls { get; } = new();
    public bool Fail { get; set; }
    public Exception? Failure { get; set; }
    public TaskCompletionSource? Wait { get; set; }
    public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public async Task<ChatReply> ReplyAsync(IReadOnlyList<ChatMessage> history, CancellationToken cancellationToken)
    {
        Calls.Enqueue(history);
        Started.TrySetResult();
        if (Wait is not null) await Wait.Task;
        if (Failure is not null) throw Failure;
        if (Fail) throw new InvalidOperationException("private model failure");
        return new($"Reply to: {history.Last().Content}", "test-deployment");
    }
}
