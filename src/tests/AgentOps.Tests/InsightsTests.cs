using AgentOps.Web.Features.Insights;
using AgentOps.Web.Infrastructure;
using AgentOps.Web.Infrastructure.Options;
using Azure;
using Azure.Core;
using Azure.Monitor.Query.Logs;
using Azure.Monitor.Query.Logs.Models;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Moq;

namespace AgentOps.Tests;

[TestClass]
public sealed class InsightsTests
{
    private readonly InsightsOptions settings = new()
    {
        ResourceId = "/subscriptions/00000000-0000-0000-0000-000000000001/resourceGroups/test/providers/Microsoft.Insights/components/test",
        LookbackMinutes = 37,
        LogLimit = 7
    };

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task LogsUseApplicationInsightsSchemaAndConfiguredBounds(bool empty)
    {
        var timestamp = new DateTimeOffset(2026, 10, 21, 10, 0, 0, TimeSpan.Zero);
        LogsTableColumn[] columns =
        [
            LogsQueryModelFactory.LogsTableColumn("timestamp", LogsColumnType.Datetime),
            LogsQueryModelFactory.LogsTableColumn("severityLevel", LogsColumnType.Int),
            LogsQueryModelFactory.LogsTableColumn("message", LogsColumnType.String),
            LogsQueryModelFactory.LogsTableColumn("operation_Id", LogsColumnType.String)
        ];
        var row = LogsQueryModelFactory.LogsTableRow(columns, [timestamp, 2, "Agent completed", "operation-1"]);
        var result = Result(columns, empty ? [] : [row]);
        using var cancellation = new CancellationTokenSource();
        var client = Client(result, "traces | top 7 by timestamp desc | project timestamp, severityLevel, message, operation_Id",
            cancellation.Token);

        var logs = await new InsightsReader(client.Object, Options.Create(settings)).LogsAsync(cancellation.Token);

        if (empty)
            logs.Should().BeEmpty();
        else
            logs.Should().Equal(new InsightLog(timestamp, "2", "Agent completed", "operation-1"));
        client.VerifyAll();
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task OverviewUsesApplicationInsightsSchemaWithDurationInMilliseconds(bool empty)
    {
        LogsTableColumn[] columns =
        [
            LogsQueryModelFactory.LogsTableColumn("Requests", LogsColumnType.Long),
            LogsQueryModelFactory.LogsTableColumn("FailedRequests", LogsColumnType.Long),
            LogsQueryModelFactory.LogsTableColumn("AverageDurationMs", LogsColumnType.Real),
            LogsQueryModelFactory.LogsTableColumn("Exceptions", LogsColumnType.Long)
        ];
        object?[] values = empty ? [0L, 0L, null, 0L] : [42L, 3L, 125.5, 2L];
        var result = Result(columns, [LogsQueryModelFactory.LogsTableRow(columns, values)]);
        using var cancellation = new CancellationTokenSource();
        var client = Client(result, """
            let req = requests | summarize Requests=count(), FailedRequests=countif(success == false), AverageDurationMs=avg(duration);
            let exc = exceptions | summarize Exceptions=count();
            req | extend JoinKey=1 | join kind=inner (exc | extend JoinKey=1) on JoinKey
            | project Requests, FailedRequests, AverageDurationMs, Exceptions
            """, cancellation.Token);

        var overview = await new InsightsReader(client.Object, Options.Create(settings)).OverviewAsync(cancellation.Token);

        overview.Should().Be(empty ? new InsightsOverview(0, 0, 0, 0) : new InsightsOverview(42, 3, 125.5, 2));
        client.VerifyAll();
    }

    private Mock<LogsQueryClient> Client(LogsQueryResult result, string query, CancellationToken cancellationToken)
    {
        var client = new Mock<LogsQueryClient>(MockBehavior.Strict);
        client.Setup(x => x.QueryResourceAsync(
                It.Is<ResourceIdentifier>(id => id.ToString() == settings.ResourceId),
                query, new LogsQueryTimeRange(TimeSpan.FromMinutes(settings.LookbackMinutes)),
                It.IsAny<LogsQueryOptions>(), cancellationToken))
            .ReturnsAsync(Response.FromValue(result, Mock.Of<Response>()));
        return client;
    }

    private static LogsQueryResult Result(LogsTableColumn[] columns, LogsTableRow[] rows) =>
        LogsQueryModelFactory.LogsQueryResult(
            [LogsQueryModelFactory.LogsTable("PrimaryResult", columns, rows)],
            BinaryData.FromString("null"), BinaryData.FromString("null"), BinaryData.FromString("null"));
}
