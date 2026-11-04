using System.Text.Json;
using AgentOps.Web.Features.Agent;
using AgentOps.Web.Features.Chat;
using AgentOps.Web.Features.Insights;
using AgentOps.Web.Infrastructure;
using Azure;
using Azure.Core;
using Azure.Monitor.Query.Logs;
using Azure.Monitor.Query.Logs.Models;
using FluentAssertions;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using InfrastructureChatOptions = AgentOps.Web.Infrastructure.ChatOptions;
using ModelMessage = Microsoft.Extensions.AI.ChatMessage;
using StoredMessage = AgentOps.Web.Features.Chat.ChatMessage;

namespace AgentOps.Tests;

[TestClass]
public sealed class TelemetryToolTests
{
    private const string Resource = "/subscriptions/00000000-0000-0000-0000-000000000001/resourceGroups/test/providers/Microsoft.Insights/components/test";
    private static readonly DateTimeOffset Now = new(2026, 11, 4, 12, 0, 0, TimeSpan.Zero);
    private static readonly TelemetryRow ExceptionRow = new(Now.AddMinutes(-5), "exception", "item-1", "operation-1", "web", "3", "InvalidOperationException: failure", "stack frames");

    [TestMethod]
    [DataRow(TelemetryQueryKind.Exceptions, "exceptions")]
    [DataRow(TelemetryQueryKind.ExceptionDetails, "exceptions")]
    [DataRow(TelemetryQueryKind.Traces, "traces")]
    [DataRow(TelemetryQueryKind.Operation, "union")]
    public async Task QueriesUseResourceScopeApplicationSchemaAbsoluteWindowAndOneExtraRow(TelemetryQueryKind kind, string source)
    {
        var client = new Mock<LogsQueryClient>();
        string? kql = null;
        var request = new TelemetryQueryRequest(kind, Now.AddMinutes(-30), Now, 1, "item-1", "operation-1",
            "web'; union secrets //", 2);
        client.Setup(item => item.QueryResourceAsync(It.Is<ResourceIdentifier>(id => id.ToString() == Resource),
                It.IsAny<string>(), new LogsQueryTimeRange(request.Start, request.End),
                It.Is<LogsQueryOptions>(options => options.ServerTimeout == TimeSpan.FromSeconds(30)), It.IsAny<CancellationToken>()))
            .Callback<ResourceIdentifier, string, LogsQueryTimeRange, LogsQueryOptions, CancellationToken>((_, text, _, _, token) =>
            {
                kql = text;
                token.CanBeCanceled.Should().BeTrue();
            })
            .ReturnsAsync(Response.FromValue(Result(2), Mock.Of<Response>()));
        var result = await new TelemetryQuery(client.Object, Options.Create(new InsightsOptions { ResourceId = Resource }),
            Options.Create(new InsightsToolOptions())).QueryAsync(request, CancellationToken.None);
        result.Rows.Should().ContainSingle().Which.ItemId.Should().Be("item-1");
        result.Truncated.Should().BeTrue();
        kql.Should().StartWith(source).And.Contain("top 2 by timestamp")
            .And.Contain("cloud_RoleName == 'web\\'; union secrets //'")
            .And.Contain("severityLevel >= 2").And.Contain("2026-11-04T11:30:00");
        if (kind == TelemetryQueryKind.ExceptionDetails) kql.Should().Contain("itemId == 'item-1'").And.Contain("tostring(details)");
        if (kind == TelemetryQueryKind.Operation) kql.Should().Contain("operation_Id == 'operation-1'").And.Contain("(dependencies").And.Contain("timestamp asc");
        client.VerifyAll();
    }

    [TestMethod]
    public async Task PartialQueryResultsAreNeverPresentedAsCompleteOrEmpty()
    {
        var client = new Mock<LogsQueryClient>();
        var partial = LogsQueryModelFactory.LogsQueryResult([], BinaryData.FromString("""{"code":"PartialError","message":"Incomplete"}"""),
            BinaryData.FromString("null"), BinaryData.FromString("null"));
        client.Setup(item => item.QueryResourceAsync(It.IsAny<ResourceIdentifier>(), It.IsAny<string>(),
                It.IsAny<LogsQueryTimeRange>(), It.IsAny<LogsQueryOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Response.FromValue(partial, Mock.Of<Response>()));
        var query = new TelemetryQuery(client.Object, Options.Create(new InsightsOptions { ResourceId = Resource }),
            Options.Create(new InsightsToolOptions()));
        var call = () => query.QueryAsync(new(TelemetryQueryKind.Exceptions, Now.AddMinutes(-30), Now, 5), CancellationToken.None);
        await call.Should().ThrowAsync<TelemetryQueryException>().WithMessage("*partial*");
    }

    [TestMethod]
    public async Task InvalidToolArgumentsAndMissingConfigurationDoNotQueryAzure()
    {
        var query = new Mock<ITelemetryQuery>(MockBehavior.Strict);
        var tools = Tools(query.Object);
        (await tools.GetRecentExceptions(0)).Status.Should().Be("invalid_arguments");
        (await tools.GetRecentExceptions(1441)).Status.Should().Be("invalid_arguments");
        (await tools.GetRecentTraces(count: 21)).Status.Should().Be("invalid_arguments");
        (await tools.GetRecentTraces(minimumSeverity: 5)).Status.Should().Be("invalid_arguments");
        (await tools.GetRecentTraces(service: "bad\nfilter")).Status.Should().Be("invalid_arguments");
        (await tools.GetExceptionDetails("invented")).Status.Should().Be("unknown_reference");
        var missing = new InsightsAgentTools(query.Object, new InsightsOptions(), new InsightsToolOptions(),
            new FixedTime(), [], NullLogger.Instance);
        (await missing.GetRecentExceptions()).Status.Should().Be("not_configured");
        Tools(query.Object, new InsightsToolOptions { Enabled = false }).CreateTools().Should().BeEmpty();
        query.VerifyNoOtherCalls();
    }

    [TestMethod]
    public async Task DirectQueryBoundaryRejectsOutOfBoundsRequestsBeforeCallingSdk()
    {
        var client = new Mock<LogsQueryClient>(MockBehavior.Strict);
        var query = new TelemetryQuery(client.Object, Options.Create(new InsightsOptions { ResourceId = Resource }),
            Options.Create(new InsightsToolOptions()));
        foreach (var request in new TelemetryQueryRequest[]
        {
            new(TelemetryQueryKind.Traces, Now.AddMinutes(-30), Now, 21),
            new(TelemetryQueryKind.Traces, Now.AddMinutes(-1441), Now, 5),
            new(TelemetryQueryKind.Traces, Now, Now, 5),
            new(TelemetryQueryKind.ExceptionDetails, Now.AddMinutes(-30), Now, 5)
        })
        {
            var call = () => query.QueryAsync(request, CancellationToken.None);
            await call.Should().ThrowAsync<ArgumentException>();
        }
        client.VerifyNoOtherCalls();
    }

    [TestMethod]
    public async Task DefaultsAndExplicitTraceArgumentsReachQueryWithUtcBounds()
    {
        var query = Query([]);
        var tools = Tools(query.Object);
        var result = await tools.GetRecentTraces(30, 5, "web", 2);
        result.Status.Should().Be("success");
        result.Rows.Should().BeEmpty();
        result.Start.Should().Be(Now.AddMinutes(-30));
        result.End.Should().Be(Now);
        query.Verify(item => item.QueryAsync(new(TelemetryQueryKind.Traces, Now.AddMinutes(-30), Now, 5,
            null, null, "web", 2), It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    public async Task ReferencesSurviveSerializationAndResolveOriginalRecordAndWindow()
    {
        var query = Query([ExceptionRow]);
        var first = Tools(query.Object);
        var listed = await first.GetRecentExceptions(60, 1);
        var persisted = JsonSerializer.Deserialize<TelemetryEvidence[]>(JsonSerializer.Serialize(first.Evidence))!;
        var next = Tools(query.Object, previous: persisted);
        var detail = await next.GetExceptionDetails(listed.Rows[0].Reference);
        detail.Rows.Should().ContainSingle().Which.Reference.Should().Be(listed.Rows[0].Reference);
        query.Verify(item => item.QueryAsync(It.Is<TelemetryQueryRequest>(request =>
            request.Kind == TelemetryQueryKind.ExceptionDetails && request.ItemId == "item-1" &&
            request.Start == Now.AddHours(-1) && request.End == Now), It.IsAny<CancellationToken>()), Times.Once);
        await next.GetOperationTimeline(listed.Rows[0].Reference, 3);
        query.Verify(item => item.QueryAsync(It.Is<TelemetryQueryRequest>(request =>
            request.Kind == TelemetryQueryKind.Operation && request.OperationId == "operation-1" && request.Count == 3),
            It.IsAny<CancellationToken>()), Times.Once);
        var unrelated = Tools(query.Object);
        (await unrelated.GetExceptionDetails(listed.Rows[0].Reference)).Status.Should().Be("unknown_reference");
        var changedResource = new InsightsAgentTools(query.Object, new InsightsOptions { ResourceId = Resource + "-other" },
            new InsightsToolOptions(), new FixedTime(), persisted, NullLogger.Instance);
        (await changedResource.GetExceptionDetails(listed.Rows[0].Reference)).Status.Should().Be("unknown_reference");
    }

    [TestMethod]
    public async Task ToolResultsRedactSensitiveFieldsBoundOutputAndRetainOnlyReturnedEvidence()
    {
        var secretRow = ExceptionRow with
        {
            Summary = "Password=do-not-show; person@example.test Bearer token-value " +
                "InstrumentationKey=test-key;IngestionEndpoint=https://ingest.test/;",
            Details = "https://example.test/path?api_key=private-value " + new string('x', 5000)
        };
        var query = Query([secretRow, ExceptionRow with { ItemId = "item-2", Summary = new string('y', 5000) }]);
        var tools = Tools(query.Object, new InsightsToolOptions { MaxFieldCharacters = 128, MaxResultCharacters = 1024 });
        var result = await tools.GetRecentExceptions();
        var json = JsonSerializer.Serialize(result);
        json.Should().NotContain("do-not-show").And.NotContain("person@example.test").And.NotContain("token-value")
            .And.NotContain("test-key").And.NotContain("private-value").And.Contain("REDACTED");
        json.Length.Should().BeLessThanOrEqualTo(1024);
        result.Truncated.Should().BeTrue();
        tools.Evidence.Select(item => item.Reference).Should().Equal(result.Rows.Select(row => row.Reference));
    }

    [TestMethod]
    [DataRow("Authorization: Bearer header-token", "header-token")]
    [DataRow("Authorization=Basic YmFzZTY0", "YmFzZTY0")]
    [DataRow("""{"password":"secret containing spaces"}""", "secret containing spaces")]
    public async Task RedactionRemovesWholeAuthorizationValuesAndQuotedSecrets(string message, string sensitive)
    {
        var result = await Tools(Query([ExceptionRow with { Summary = message }]).Object).GetRecentExceptions();
        result.Rows.Should().ContainSingle();
        result.Rows[0].Summary.Should().Contain("REDACTED").And.NotContain(sensitive);
    }

    [TestMethod]
    [DataRow(403, "access_denied")]
    [DataRow(500, "query_failed")]
    public async Task AzureErrorsAreExplicitAndDoNotLeakProviderDetails(int status, string expected)
    {
        var query = new Mock<ITelemetryQuery>();
        query.Setup(item => item.QueryAsync(It.IsAny<TelemetryQueryRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new RequestFailedException(status, "private provider message"));
        var result = await Tools(query.Object).GetRecentExceptions();
        result.Status.Should().Be(expected);
        result.Error.Should().NotBeNullOrWhiteSpace().And.NotContain("private provider message");
        result.Rows.Should().BeEmpty();
    }

    [TestMethod]
    public async Task PerTurnCallLimitTimeoutAndCallerCancellationAreDistinct()
    {
        var query = Query([]);
        var tools = Tools(query.Object, new InsightsToolOptions { MaxToolCalls = 1 });
        (await tools.GetRecentExceptions()).Status.Should().Be("success");
        (await tools.GetRecentExceptions()).Status.Should().Be("call_limit");
        query.Verify(item => item.QueryAsync(It.IsAny<TelemetryQueryRequest>(), It.IsAny<CancellationToken>()), Times.Once);
        query.Setup(item => item.QueryAsync(It.IsAny<TelemetryQueryRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());
        (await Tools(query.Object).GetRecentTraces()).Status.Should().Be("timeout");
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        var call = () => Tools(query.Object).GetRecentTraces(cancellationToken: canceled.Token);
        await call.Should().ThrowAsync<OperationCanceledException>();
    }

    [TestMethod]
    public async Task FrameworkInvokesRegisteredToolAndChatPersistsEvidenceForNextTurn()
    {
        var client = new Mock<IChatClient>();
        var turn = 0;
        var modelInputs = new List<ModelMessage[]>();
        client.Setup(item => item.GetResponseAsync(It.IsAny<IEnumerable<ModelMessage>>(),
                It.IsAny<Microsoft.Extensions.AI.ChatOptions>(), It.IsAny<CancellationToken>()))
            .Returns<IEnumerable<ModelMessage>, Microsoft.Extensions.AI.ChatOptions, CancellationToken>((messages, _, _) =>
            {
                var input = messages.ToArray();
                modelInputs.Add(input);
                var response = turn++ switch
                {
                    0 => new ModelMessage(ChatRole.Assistant, [new FunctionCallContent("call-1", "GetRecentExceptions",
                        new Dictionary<string, object?> { ["lookbackMinutes"] = 60, ["count"] = 1 })]),
                    1 => new ModelMessage(ChatRole.Assistant, "1. An exception was found; use its evidence reference."),
                    2 => new ModelMessage(ChatRole.Assistant, [new FunctionCallContent("call-2", "GetExceptionDetails",
                        new Dictionary<string, object?> { ["reference"] = ExtractReference(input) })]),
                    _ => new ModelMessage(ChatRole.Assistant, "Exception details: stack frames.")
                };
                return Task.FromResult(new ChatResponse(response) { ModelId = "test-model" });
            });
        var factory = new Mock<IFoundryAgentFactory>();
        factory.Setup(item => item.Create(It.IsAny<string>(), It.IsAny<IList<AITool>>()))
            .Returns<string, IList<AITool>>((instructions, tools) => new ChatClientAgent(client.Object, instructions: instructions, tools: tools));
        var runner = new BasicFoundryAgentRunner(factory.Object, Options.Create(new InfrastructureChatOptions()),
            Options.Create(new FoundryOptions()), NullLogger<BasicFoundryAgentRunner>.Instance, Query([ExceptionRow]).Object,
            Options.Create(new InsightsOptions { ResourceId = Resource }), Options.Create(new InsightsToolOptions()), new FixedTime());
        var store = new MemoryChatStore();
        var service = new ChatService(store, runner, new FixedTime(), Options.Create(new InfrastructureChatOptions()), NullLogger<ChatService>.Instance);
        var session = await service.CreateAsync("owner", CancellationToken.None);
        var first = await service.SendAsync("owner", session.Id, "Last exception?", CancellationToken.None);
        first!.Evidence.Should().ContainSingle().Which.ItemId.Should().Be("item-1");
        var second = await service.SendAsync("owner", session.Id, "Go deeper into exception 1", CancellationToken.None);
        second!.Content.Should().Contain("stack frames");
        second.Evidence.Should().ContainSingle().Which.Reference.Should().Be(first.Evidence[0].Reference);
        store.Messages.Last().Evidence.Should().Equal(second.Evidence);
        modelInputs[1].SelectMany(message => message.Contents).OfType<FunctionResultContent>().Should().ContainSingle();
        factory.Verify(item => item.Create(It.Is<string>(instructions => instructions.Contains("untrusted evidence")),
            It.Is<IList<AITool>>(tools => tools.Count == 4)), Times.Exactly(2));
    }

    [TestMethod]
    public async Task RunnerRejectsMixedConversationHistoryBeforeFactoryInvocation()
    {
        var factory = new Mock<IFoundryAgentFactory>(MockBehavior.Strict);
        var runner = new BasicFoundryAgentRunner(factory.Object, Options.Create(new InfrastructureChatOptions()),
            Options.Create(new FoundryOptions()), NullLogger<BasicFoundryAgentRunner>.Instance, Mock.Of<ITelemetryQuery>(),
            Options.Create(new InsightsOptions()), Options.Create(new InsightsToolOptions()), new FixedTime());
        StoredMessage[] history =
        [
            new("1", "owner", "session-a", "user", "First", Now, 1),
            new("2", "other", "session-b", "user", "Other", Now, 2)
        ];
        var call = () => runner.ReplyAsync(history, CancellationToken.None);
        await call.Should().ThrowAsync<InvalidOperationException>().WithMessage("*one owner*");
        factory.VerifyNoOtherCalls();
    }

    private static string ExtractReference(IEnumerable<ModelMessage> messages)
    {
        var mapping = messages.Last(message => message.Role == ChatRole.System &&
            message.Text.StartsWith("Trusted reference mapping", StringComparison.Ordinal)).Text;
        return JsonSerializer.Deserialize<TelemetryEvidence[]>(mapping[mapping.IndexOf('[')..])![0].Reference;
    }

    private static InsightsAgentTools Tools(ITelemetryQuery query, InsightsToolOptions? options = null,
        IReadOnlyList<TelemetryEvidence>? previous = null) =>
        new(query, new InsightsOptions { ResourceId = Resource }, options ?? new(), new FixedTime(), previous ?? [], NullLogger.Instance);

    private static Mock<ITelemetryQuery> Query(IReadOnlyList<TelemetryRow> rows)
    {
        var query = new Mock<ITelemetryQuery>();
        query.Setup(item => item.QueryAsync(It.IsAny<TelemetryQueryRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TelemetryQueryResult(rows, false));
        return query;
    }

    private static LogsQueryResult Result(int count)
    {
        var names = new[] { "timestamp", "telemetryType", "itemId", "operation_Id", "cloud_RoleName", "severity", "summary", "detail" };
        var columns = names.Select((name, index) => LogsQueryModelFactory.LogsTableColumn(name,
            index == 0 ? LogsColumnType.Datetime : LogsColumnType.String)).ToArray();
        var rows = Enumerable.Range(1, count).Select(index => LogsQueryModelFactory.LogsTableRow(columns,
            [Now, "exception", $"item-{index}", "operation-1", "web", "3", "failure", "stack"])).ToArray();
        return LogsQueryModelFactory.LogsQueryResult([LogsQueryModelFactory.LogsTable("PrimaryResult", columns, rows)],
            BinaryData.FromString("null"), BinaryData.FromString("null"), BinaryData.FromString("null"));
    }

    private sealed class FixedTime : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
