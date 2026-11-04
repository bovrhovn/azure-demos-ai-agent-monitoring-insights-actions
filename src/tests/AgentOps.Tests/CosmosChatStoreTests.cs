using System.Net;
using AgentOps.Web.Features.Chat;
using AgentOps.Web.Infrastructure;
using FluentAssertions;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Options;
using Moq;

namespace AgentOps.Tests;

[TestClass]
public sealed class CosmosChatStoreTests
{
    [TestMethod]
    public async Task MessagesQueryIsOwnerPartitionedSessionScopedBoundedAndChronologicalAcrossCosmosPages()
    {
        var container = new Mock<Container>(MockBehavior.Strict);
        using var iterator = Mock.Of<FeedIterator<ChatMessage>>();
        var pages = Mock.Get(iterator);
        var messages = Enumerable.Range(1, 3).Select(sequence =>
            new ChatMessage($"message-{sequence}", "owner", "session", "user", $"text-{sequence}", DateTimeOffset.UtcNow, sequence)).ToArray();
        QueryDefinition? query = null;
        QueryRequestOptions? request = null;
        container.Setup(item => item.GetItemQueryIterator<ChatMessage>(It.IsAny<QueryDefinition>(), null, It.IsAny<QueryRequestOptions>()))
            .Callback<QueryDefinition, string, QueryRequestOptions>((definition, _, options) => { query = definition; request = options; })
            .Returns(iterator);
        pages.SetupGet(page => page.HasMoreResults).Returns(true);
        pages.SetupSequence(page => page.ReadNextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Page(messages[2])).ReturnsAsync(Page(messages[1], messages[0]));
        var result = await Store(container.Object).MessagesAsync("owner", "session", 2, 4, CancellationToken.None);
        result.Items.Should().Equal(messages[1], messages[2]);
        result.HasMore.Should().BeTrue();
        query!.QueryText.Should().Contain("c.kind = 'message'").And.Contain("c.sessionId = @sessionId")
            .And.Contain("c.sequence < @before").And.Contain("ORDER BY c.sequence DESC").And.Contain("TOP @limit");
        query.GetQueryParameters().Should().BeEquivalentTo(new (string Name, object Value)[]
        {
            ("@limit", 3), ("@sessionId", "session"), ("@before", 4)
        });
        request!.PartitionKey.Should().Be(new PartitionKey("owner"));
        request.MaxItemCount.Should().Be(3);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task SessionAndMessageAreWrittenInOneOwnerPartitionTransactionAndFailuresPropagate(bool success)
    {
        var container = new Mock<Container>(MockBehavior.Strict);
        var batch = new Mock<TransactionalBatch>(MockBehavior.Strict);
        var response = new Mock<TransactionalBatchResponse>();
        response.SetupGet(item => item.IsSuccessStatusCode).Returns(success);
        response.SetupGet(item => item.StatusCode).Returns(success ? HttpStatusCode.OK : HttpStatusCode.NotFound);
        var now = DateTimeOffset.UtcNow;
        var session = new ChatSession("session", "owner", "A conversation", now, now, 1);
        var message = new ChatMessage("message", "owner", session.Id, "user", "Hello", now, 1);
        container.Setup(item => item.CreateTransactionalBatch(new PartitionKey("owner"))).Returns(batch.Object);
        batch.Setup(item => item.ReplaceItem(session.Id, session, null)).Returns(batch.Object);
        batch.Setup(item => item.CreateItem(message, null)).Returns(batch.Object);
        batch.Setup(item => item.ExecuteAsync(It.IsAny<CancellationToken>())).ReturnsAsync(response.Object);
        var append = () => Store(container.Object).AppendAsync(session, message, CancellationToken.None);
        if (success) await append();
        else await append.Should().ThrowAsync<CosmosException>();
        batch.VerifyAll();
        container.VerifyAll();
    }

    [TestMethod]
    public async Task SessionListUsesOwnerPartitionAndCosmosContinuationToken()
    {
        var container = new Mock<Container>();
        var iterator = new Mock<FeedIterator<ChatSession>>();
        var response = new Mock<FeedResponse<ChatSession>>();
        response.Setup(item => item.GetEnumerator()).Returns(() => Enumerable.Empty<ChatSession>().GetEnumerator());
        response.SetupGet(item => item.ContinuationToken).Returns("next-page");
        iterator.Setup(item => item.ReadNextAsync(It.IsAny<CancellationToken>())).ReturnsAsync(response.Object);
        container.Setup(item => item.GetItemQueryIterator<ChatSession>(
            It.Is<QueryDefinition>(query => query.QueryText.Contains("c.kind = 'session'") && query.QueryText.EndsWith("c.updatedAt DESC")),
            "previous-page", It.Is<QueryRequestOptions>(options => options.PartitionKey == new PartitionKey("owner") && options.MaxItemCount == 3)))
            .Returns(iterator.Object);
        var result = await Store(container.Object).ListAsync("owner", 3, "previous-page", CancellationToken.None);
        result.ContinuationToken.Should().Be("next-page");
        container.VerifyAll();
    }

    [TestMethod]
    public async Task PointReadCannotTreatAMessageDocumentAsASession()
    {
        var container = new Mock<Container>();
        var response = new Mock<ItemResponse<ChatSession>>();
        response.SetupGet(item => item.Resource).Returns(new ChatSession("message", "owner", "", default, default) { Kind = "message" });
        container.Setup(item => item.ReadItemAsync<ChatSession>("message", new PartitionKey("owner"), null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(response.Object);
        (await Store(container.Object).GetAsync("owner", "message", CancellationToken.None)).Should().BeNull();
    }

    private static CosmosChatStore Store(Container container)
    {
        var client = new Mock<CosmosClient>();
        client.Setup(item => item.GetContainer("database", "chat-container")).Returns(container);
        return new(client.Object, Options.Create(new CosmosOptions { Database = "database" }),
            Options.Create(new ChatOptions { Container = "chat-container" }));
    }

    private static FeedResponse<T> Page<T>(params T[] items)
    {
        var page = new Mock<FeedResponse<T>>();
        page.Setup(item => item.GetEnumerator()).Returns(() => ((IEnumerable<T>)items).GetEnumerator());
        return page.Object;
    }
}
