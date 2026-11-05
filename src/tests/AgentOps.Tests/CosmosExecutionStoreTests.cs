using AgentOps.Web.Features.Executions;
using AgentOps.Web.Infrastructure;
using AgentOps.Web.Infrastructure.Options;
using FluentAssertions;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Options;
using Moq;

namespace AgentOps.Tests;

[TestClass]
public sealed class CosmosExecutionStoreTests
{
    [TestMethod]
    [DataRow(ExecutionContract.Endpoint)]
    [DataRow(ExecutionContract.Schedule)]
    public async Task HistoryUsesParameterizedBoundedQueryInTheRequestedPartition(string trigger)
    {
        var options = new CosmosOptions { Database = "history", Container = "runs" };
        var client = new Mock<CosmosClient>(MockBehavior.Strict);
        var container = new Mock<Container>(MockBehavior.Strict);
        var iterator = new Mock<FeedIterator<ExecutionRecord>>();
        using var cancellation = new CancellationTokenSource();
        var now = DateTimeOffset.UtcNow;
        var newest = new ExecutionRecord("newest", trigger, ExecutionContract.Running, now, null,
            "private caller", "correlation", "private summary");
        var older = newest with { Id = "older", StartedAt = now.AddMinutes(-1) };
        QueryDefinition? actualQuery = null;
        QueryRequestOptions? actualRequest = null;
        client.Setup(x => x.GetContainer(options.Database, options.Container)).Returns(container.Object);
        container.Setup(x => x.GetItemQueryIterator<ExecutionRecord>(
                It.IsAny<QueryDefinition>(), null, It.IsAny<QueryRequestOptions>()))
            .Callback<QueryDefinition, string, QueryRequestOptions>((query, _, request) =>
            {
                actualQuery = query;
                actualRequest = request;
            }).Returns(iterator.Object);
        iterator.SetupGet(x => x.HasMoreResults).Returns(true);
        iterator.SetupSequence(x => x.ReadNextAsync(cancellation.Token))
            .ReturnsAsync(Page(newest)).ReturnsAsync(Page(older));

        var result = await new CosmosExecutionStore(client.Object, Options.Create(options))
            .ListAsync(trigger, 2, cancellation.Token);

        result.Should().Equal(newest, older);
        actualQuery!.QueryText.Should().Be("SELECT TOP @limit * FROM c WHERE c.trigger = @trigger ORDER BY c.startedAt DESC");
        actualQuery.GetQueryParameters().Should().BeEquivalentTo(new (string Name, object Value)[]
        {
            ("@limit", 2), ("@trigger", trigger)
        });
        actualRequest!.PartitionKey.Should().Be(new PartitionKey(trigger));
        actualRequest.MaxItemCount.Should().Be(2);
        iterator.Verify(x => x.ReadNextAsync(cancellation.Token), Times.Exactly(2));
        client.VerifyAll();
        container.VerifyAll();
    }

    private static FeedResponse<ExecutionRecord> Page(ExecutionRecord record)
    {
        var page = new Mock<FeedResponse<ExecutionRecord>>();
        page.Setup(x => x.GetEnumerator()).Returns(() => ((IEnumerable<ExecutionRecord>)[record]).GetEnumerator());
        return page.Object;
    }
}
