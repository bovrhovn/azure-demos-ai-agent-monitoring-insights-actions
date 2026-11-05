using System.Net;
using AgentOps.Web.Features.Notifications;
using AgentOps.Web.Infrastructure.Options;
using FluentAssertions;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace AgentOps.Tests;

[TestClass]
public sealed class CosmosNotificationStoreTests
{
    [TestMethod]
    [DataRow(null, null, true)]
    [DataRow("latency' OR true", false, false)]
    [DataRow(null, true, true)]
    public async Task InboxQueriesAreParameterizedSortedAndUseBoundedCosmosContinuationPages(string? search, bool? handled, bool newestFirst)
    {
        var container = new Mock<Container>(MockBehavior.Strict);
        var iterator = new Mock<FeedIterator<NotificationRecord>>();
        var response = new Mock<FeedResponse<NotificationRecord>>();
        var notification = new NotificationRecord("notification", "{}", "Latency issue", DateTimeOffset.UtcNow);
        response.Setup(item => item.GetEnumerator()).Returns(() => ((IEnumerable<NotificationRecord>)[notification]).GetEnumerator());
        response.SetupGet(item => item.ContinuationToken).Returns("next-page");
        iterator.Setup(item => item.ReadNextAsync(It.IsAny<CancellationToken>())).ReturnsAsync(response.Object);
        container.Setup(item => item.GetItemQueryIterator<NotificationRecord>(It.IsAny<QueryDefinition>(), "previous-page",
            It.IsAny<QueryRequestOptions>()))
            .Callback<QueryDefinition, string, QueryRequestOptions>((query, _, options) =>
            {
                query.QueryText.Should().EndWith("ORDER BY c.receivedDate " + (newestFirst ? "DESC" : "ASC"));
                query.QueryText.Contains("CONTAINS(c.message, @search, true)").Should().Be(search is not null);
                query.QueryText.Contains("c.isHandled = @isHandled").Should().Be(handled.HasValue);
                var parameters = query.GetQueryParameters().ToDictionary(parameter => parameter.Name, parameter => parameter.Value);
                if (search is not null)
                {
                    query.QueryText.Should().NotContain(search);
                    parameters["@search"].Should().Be(search);
                }
                if (handled.HasValue) parameters["@isHandled"].Should().Be(handled);
                options.MaxItemCount.Should().Be(3);
                options.PartitionKey.Should().BeNull("the shared inbox spans notification-ID partitions");
            }).Returns(iterator.Object);
        var page = await Store(container.Object).ListAsync(search, handled, newestFirst, 3, "previous-page", CancellationToken.None);
        page.Items.Should().Equal(notification);
        page.ContinuationToken.Should().Be("next-page");
        container.VerifyAll();
    }

    [TestMethod]
    public async Task DuplicateReceiptUsesCreateNotUpsertAndOtherCosmosFailuresPropagate()
    {
        var container = new Mock<Container>(MockBehavior.Strict);
        var notification = new NotificationRecord("event", "{}", "Message", DateTimeOffset.UtcNow);
        container.Setup(item => item.CreateItemAsync(notification, new PartitionKey(notification.Id), null, It.IsAny<CancellationToken>()))
            .ThrowsAsync(Failure(HttpStatusCode.Conflict));
        var store = Store(container.Object);
        await store.CreateAsync(notification, CancellationToken.None);
        container.Setup(item => item.CreateItemAsync(notification, new PartitionKey(notification.Id), null, It.IsAny<CancellationToken>()))
            .ThrowsAsync(Failure(HttpStatusCode.ServiceUnavailable));
        var create = () => store.CreateAsync(notification, CancellationToken.None);
        await create.Should().ThrowAsync<CosmosException>();
    }

    [TestMethod]
    public async Task HandlingUsesTargetedPatchesAndNeverReplacesSessionOrRawEventFields()
    {
        var container = new Mock<Container>(MockBehavior.Strict);
        var notification = new NotificationRecord("event", """{"data":{"message":"Message"}}""", "Message", DateTimeOffset.UtcNow)
        { ChatSessionId = "session", ChatOwnerId = "owner", Comment = "Previous note" };
        var response = new Mock<ItemResponse<NotificationRecord>>();
        response.SetupGet(item => item.Resource).Returns(notification);
        var paths = new List<string[]>();
        container.Setup(item => item.PatchItemAsync<NotificationRecord>(notification.Id, new PartitionKey(notification.Id),
                It.IsAny<IReadOnlyList<PatchOperation>>(), null, It.IsAny<CancellationToken>()))
            .Callback<string, PartitionKey, IReadOnlyList<PatchOperation>, PatchItemRequestOptions, CancellationToken>(
                (_, _, patches, _, _) => paths.Add(patches.Select(patch => patch.Path).ToArray()))
            .ReturnsAsync(response.Object);
        var store = Store(container.Object);
        await store.SetHandledAsync(notification.Id, true, "Reviewed", CancellationToken.None);
        await store.SetHandledAsync(notification.Id, false, null, CancellationToken.None);
        paths[0].Should().Equal("/isHandled", "/comment");
        paths[1].Should().Equal("/isHandled");
    }

    [TestMethod]
    public async Task SessionReservationIsConditionalAndReturnsTheWinningAssociationOnConflict()
    {
        var container = new Mock<Container>(MockBehavior.Strict);
        var existing = new NotificationRecord("event", "{}", "Message", DateTimeOffset.UtcNow)
        { ChatSessionId = "winning-session", ChatOwnerId = "another-owner" };
        var read = new Mock<ItemResponse<NotificationRecord>>();
        read.SetupGet(item => item.Resource).Returns(existing);
        container.Setup(item => item.PatchItemAsync<NotificationRecord>(existing.Id, new PartitionKey(existing.Id),
                It.IsAny<IReadOnlyList<PatchOperation>>(), It.IsAny<PatchItemRequestOptions>(), It.IsAny<CancellationToken>()))
            .Callback<string, PartitionKey, IReadOnlyList<PatchOperation>, PatchItemRequestOptions, CancellationToken>(
                (_, _, patches, options, _) =>
                {
                    patches.Select(patch => patch.Path).Should().Equal("/chatSessionId", "/chatOwnerId");
                    options.FilterPredicate.Should().Be("FROM c WHERE IS_NULL(c.chatSessionId) OR NOT IS_DEFINED(c.chatSessionId)");
                })
            .ThrowsAsync(Failure(HttpStatusCode.PreconditionFailed));
        container.Setup(item => item.ReadItemAsync<NotificationRecord>(existing.Id, new PartitionKey(existing.Id), null,
            It.IsAny<CancellationToken>())).ReturnsAsync(read.Object);
        (await Store(container.Object).AssignSessionAsync(existing.Id, "owner", "losing-session", CancellationToken.None)).Should().Be(existing);
    }

    private static CosmosException Failure(HttpStatusCode status) => new("Test Cosmos failure", status, 0, "test", 0);

    private static CosmosNotificationStore Store(Container container)
    {
        var client = new Mock<CosmosClient>();
        client.Setup(item => item.GetContainer("database", "notifications")).Returns(container);
        return new(client.Object, Options.Create(new CosmosOptions { Database = "database" }),
            Options.Create(new NotificationOptions()), NullLogger<CosmosNotificationStore>.Instance);
    }
}
