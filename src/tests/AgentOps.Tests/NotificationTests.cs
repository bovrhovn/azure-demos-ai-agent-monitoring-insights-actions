using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AgentOps.Web;
using AgentOps.Web.Features.Chat;
using AgentOps.Web.Features.Notifications;
using FluentAssertions;

namespace AgentOps.Tests;

[TestClass]
public sealed class NotificationTests
{
    [TestMethod]
    public async Task WebhookPersistsEveryRawEventAndExtractsMessageWithoutInvokingAgents()
    {
        await using var factory = new AppFactory();
        using var client = factory.Client();
        const string first = """{"id":"one","topic":"demo","eventType":"Demo.Requested","data":{"message":"<script>inspect this</script>","priority":2}}""";
        const string second = """{"id":"two","eventType":"Demo.Ready","data":{"count":5}}""";
        var before = DateTimeOffset.UtcNow;
        using var response = await Receive(client, $"[{first},{second}]");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var saved = factory.Notifications.Items.Values.ToArray();
        saved.Should().HaveCount(2).And.OnlyContain(item => !item.IsHandled && item.ChatSessionId == null && item.Comment == null);
        saved.Should().OnlyContain(item => item.ReceivedDate >= before && item.ReceivedDate <= DateTimeOffset.UtcNow);
        saved.Single(item => item.Message.Length > 0).RawData.Should().Be(first);
        saved.Single(item => item.RawData == second).Message.Should().BeEmpty();
        factory.ChatAgent.Calls.Should().BeEmpty();
        factory.Chats.Sessions.Should().BeEmpty();
        factory.Store.Writes.Should().BeEmpty();
    }

    [TestMethod]
    public async Task DuplicateDeliveriesDoNotResetHandlingCommentsSessionOrReceivedDate()
    {
        await using var factory = new AppFactory();
        using var client = factory.Client("AgentOps.Operator");
        await ChatTests.AuthorizePosts(client);
        const string payload = """[{"id":"retry","topic":"topic","eventType":"Demo.Requested","data":{"message":"Investigate latency"}}]""";
        (await Receive(client, payload)).EnsureSuccessStatusCode();
        var first = factory.Notifications.Items.Values.Single();
        (await client.PostAsJsonAsync($"{RouteConstants.Notifications}/{first.Id}/handled",
            new { isHandled = true, confirmed = true, comment = "Reviewed" })).EnsureSuccessStatusCode();
        (await client.PostAsync($"{RouteConstants.Notifications}/{first.Id}/session", null)).EnsureSuccessStatusCode();
        var updated = factory.Notifications.Items[first.Id];
        (await Receive(client, payload)).EnsureSuccessStatusCode();
        factory.Notifications.Items.Should().ContainSingle().Which.Value.Should().Be(updated);
        updated.ReceivedDate.Should().Be(first.ReceivedDate);
        updated.Comment.Should().Be("Reviewed");
        updated.ChatSessionId.Should().NotBeNull();
        updated.IsHandled.Should().BeTrue();
    }

    [TestMethod]
    public async Task WebhookRejectsOversizeAndPropagatesPersistenceFailuresInsteadOfAcknowledging()
    {
        await using var factory = new AppFactory(new Dictionary<string, string?> { ["Webhook:MaxPayloadBytes"] = "1024" });
        using var client = factory.Client();
        var tooLarge = JsonSerializer.Serialize(new[] { new { eventType = "Demo.Requested", data = new { message = new string('x', 1024) } } });
        (await Receive(client, tooLarge)).StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
        factory.Notifications.Items.Should().BeEmpty();
        factory.Notifications.FailCreate = true;
        var response = await Receive(client, """[{"eventType":"Demo.Requested","data":{"message":"Do not lose this"}}]""");
        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        (await response.Content.ReadAsStringAsync()).Should().NotContain("private test failure");
    }

    [TestMethod]
    public async Task InboxDefaultsToAllNewestFirstAndSupportsSearchStatusSortingAndContinuationPages()
    {
        await using var factory = new AppFactory(new Dictionary<string, string?> { ["Notifications:PageSize"] = "2" });
        var now = DateTimeOffset.UtcNow;
        foreach (var item in new[]
        {
            new NotificationRecord("old", "{}", "Latency first", now.AddMinutes(-2)),
            new NotificationRecord("middle", "{}", "Other issue", now.AddMinutes(-1)) { IsHandled = true },
            new NotificationRecord("new", "{}", "LATENCY latest", now)
        }) await factory.Notifications.CreateAsync(item, CancellationToken.None);
        using var client = factory.Client("AgentOps.Reader");
        var first = await Inbox(client);
        first.Items.Select(item => item.Id).Should().Equal("new", "middle");
        first.ContinuationToken.Should().NotBeNull();
        var next = await Inbox(client, "?continuationToken=" + first.ContinuationToken);
        next.Items.Should().ContainSingle().Which.Id.Should().Be("old");
        next.ContinuationToken.Should().BeNull();
        (await Inbox(client, "?search=latency&handled=unhandled&sort=asc")).Items.Select(item => item.Id).Should().Equal("old", "new");
        (await Inbox(client, "?handled=handled")).Items.Should().ContainSingle().Which.Id.Should().Be("middle");
        (await Inbox(client, "?search=missing")).Items.Should().BeEmpty();
        foreach (var query in new[] { "?handled=invalid", "?sort=invalid", "?search=" + new string('a', 201) })
            (await client.GetAsync(RouteConstants.Notifications + query)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [TestMethod]
    public async Task CompletionRequiresConfirmationSupportsOptionalCommentsAndCanBeReversedWithoutLosingNote()
    {
        await using var factory = new AppFactory(new Dictionary<string, string?> { ["Notifications:MaxCommentLength"] = "20" });
        await factory.Notifications.CreateAsync(new("message", "{}", "Check errors", DateTimeOffset.UtcNow), CancellationToken.None);
        using var client = factory.Client("AgentOps.Operator");
        await ChatTests.AuthorizePosts(client);
        var url = $"{RouteConstants.Notifications}/message/handled";
        (await client.PostAsJsonAsync(url, new { isHandled = true, comment = "No confirmation" })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await client.PostAsJsonAsync(url, new { isHandled = true, confirmed = true, comment = new string('a', 21) })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        factory.Notifications.Items["message"].IsHandled.Should().BeFalse();
        (await client.PostAsJsonAsync(url, new { isHandled = true, confirmed = true, comment = "  Investigated  " })).StatusCode.Should().Be(HttpStatusCode.NoContent);
        factory.Notifications.Items["message"].Should().Match<NotificationRecord>(item => item.IsHandled && item.Comment == "Investigated");
        (await client.PostAsJsonAsync(url, new { isHandled = false })).StatusCode.Should().Be(HttpStatusCode.NoContent);
        factory.Notifications.Items["message"].Should().Match<NotificationRecord>(item => !item.IsHandled && item.Comment == "Investigated");
        (await client.PostAsJsonAsync(url, new { isHandled = true, confirmed = true, comment = " " })).StatusCode.Should().Be(HttpStatusCode.NoContent);
        factory.Notifications.Items["message"].Comment.Should().BeNull();
        (await client.PostAsJsonAsync($"{RouteConstants.Notifications}/missing/handled", new { isHandled = false })).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [TestMethod]
    public async Task SessionCreationIsRetryablePrivateAndPrefillsDraftWithoutSendingOrMarkingHandled()
    {
        await using var factory = new AppFactory(new Dictionary<string, string?> { ["Chat:SessionPageSize"] = "1" });
        await factory.Notifications.CreateAsync(new("message", "{}", "<img src=x onerror=alert(1)> Investigate", DateTimeOffset.UtcNow), CancellationToken.None);
        using var client = factory.Client("AgentOps.Operator");
        await ChatTests.AuthorizePosts(client);
        var url = $"{RouteConstants.Notifications}/message/session";
        factory.Chats.FailCreate = true;
        (await client.PostAsync(url, null)).StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        var reserved = factory.Notifications.Items["message"].ChatSessionId;
        reserved.Should().NotBeNull();
        factory.Chats.Sessions.Should().BeEmpty();
        factory.Chats.FailCreate = false;
        (await client.PostAsync(url, null)).EnsureSuccessStatusCode();
        var session = factory.Chats.Sessions.Values.Single();
        session.Id.Should().Be(reserved);
        session.InitialDraft.Should().Be(factory.Notifications.Items["message"].Message);
        session.MessageCount.Should().Be(0);
        factory.Notifications.Items["message"].IsHandled.Should().BeFalse();
        (await client.PostAsync(url, null)).EnsureSuccessStatusCode();
        factory.Chats.Sessions.Should().ContainSingle();
        (await client.GetFromJsonAsync<ChatSession>($"{RouteConstants.ChatSessions}/{session.Id}"))!.InitialDraft.Should().Be(session.InitialDraft);
        (await Inbox(client)).Items.Should().ContainSingle().Which.CanOpenSession.Should().BeTrue();
        using var other = factory.Client("AgentOps.Operator");
        other.DefaultRequestHeaders.Add("X-Test-User", "different-operator");
        await ChatTests.AuthorizePosts(other);
        (await Inbox(other)).Items.Should().ContainSingle().Which.CanOpenSession.Should().BeFalse();
        (await other.PostAsync(url, null)).StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await other.GetAsync($"{RouteConstants.ChatSessions}/{session.Id}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        factory.ChatAgent.Calls.Should().BeEmpty();
        factory.Chats.Messages.Should().BeEmpty();
    }

    [TestMethod]
    public async Task InboxAndMutationsEnforceAuthenticationOperatorRoleAndAntiforgery()
    {
        await using var factory = new AppFactory();
        using var anonymous = factory.Client();
        (await anonymous.GetAsync(RouteConstants.NotificationsPage)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await anonymous.GetAsync(RouteConstants.Notifications)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        using var reader = factory.Client("AgentOps.Reader");
        var html = await reader.GetStringAsync(RouteConstants.NotificationsPage);
        html.Should().Contain("Webhook inbox").And.Contain("data-can-operate=\"false\"")
            .And.Contain($"href=\"{RouteConstants.NotificationsPage}\"");
        using var noToken = factory.Client("AgentOps.Operator");
        foreach (var (suffix, body) in new (string, object?)[]
        {
            ("handled", new { isHandled = true, confirmed = true }), ("session", null)
        })
        {
            var path = $"{RouteConstants.Notifications}/message/{suffix}";
            (await anonymous.PostAsJsonAsync(path, body)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
            (await reader.PostAsJsonAsync(path, body)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
            (await noToken.PostAsJsonAsync(path, body)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        }
        factory.Chats.Sessions.Should().BeEmpty();
        factory.Notifications.Items.Should().BeEmpty();
    }

    internal static async Task<HttpResponseMessage> Receive(HttpClient client, string json)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, RouteConstants.EventGridWebhook);
        request.Headers.Add("X-Api-Key", "test-only-webhook-key");
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        return await client.SendAsync(request);
    }

    private static async Task<InboxPage> Inbox(HttpClient client, string query = "") =>
        (await client.GetFromJsonAsync<InboxPage>(RouteConstants.Notifications + query))!;

    public sealed record InboxPage(IReadOnlyList<InboxItem> Items, string? ContinuationToken);
    public sealed record InboxItem(string Id, bool CanOpenSession);
}

internal sealed class MemoryNotificationStore : INotificationStore
{
    private readonly object gate = new();
    public ConcurrentDictionary<string, NotificationRecord> Items { get; } = new();
    public bool FailCreate { get; set; }
    public bool FailUpdate { get; set; }

    public Task CreateAsync(NotificationRecord notification, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (FailCreate) throw new InvalidOperationException("private test failure");
        Items.TryAdd(notification.Id, notification);
        return Task.CompletedTask;
    }

    public Task<NotificationRecord?> GetAsync(string id, CancellationToken cancellationToken) =>
        Task.FromResult(Items.GetValueOrDefault(id));

    public Task<NotificationPage> ListAsync(string? search, bool? isHandled, bool newestFirst, int limit,
        string? continuationToken, CancellationToken cancellationToken)
    {
        var query = Items.Values.Where(item => (string.IsNullOrEmpty(search) || item.Message.Contains(search, StringComparison.OrdinalIgnoreCase)) &&
            (!isHandled.HasValue || item.IsHandled == isHandled));
        var sorted = (newestFirst ? query.OrderByDescending(item => item.ReceivedDate) : query.OrderBy(item => item.ReceivedDate)).ToArray();
        var offset = continuationToken is null ? 0 : int.Parse(continuationToken);
        return Task.FromResult(new NotificationPage(sorted.Skip(offset).Take(limit).ToArray(),
            offset + limit < sorted.Length ? (offset + limit).ToString() : null));
    }

    public Task<NotificationRecord?> SetHandledAsync(string id, bool isHandled, string? comment, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            if (FailUpdate) throw new InvalidOperationException("private update failure");
            if (!Items.TryGetValue(id, out var item)) return Task.FromResult<NotificationRecord?>(null);
            return Task.FromResult<NotificationRecord?>(Items[id] = item with { IsHandled = isHandled, Comment = isHandled ? comment : item.Comment });
        }
    }

    public Task<NotificationRecord?> AssignSessionAsync(string id, string ownerId, string sessionId, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            if (FailUpdate) throw new InvalidOperationException("private update failure");
            if (!Items.TryGetValue(id, out var item)) return Task.FromResult<NotificationRecord?>(null);
            return Task.FromResult<NotificationRecord?>(item.ChatSessionId is not null ? item :
                Items[id] = item with { ChatSessionId = sessionId, ChatOwnerId = ownerId });
        }
    }
}
