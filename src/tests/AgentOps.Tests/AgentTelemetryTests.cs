using System.Diagnostics;
using AgentOps.Web.Infrastructure;
using AgentOps.Web.Features.Agent;
using AgentOps.Web.Infrastructure.Options;
using Azure.Core;
using FluentAssertions;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using OpenTelemetry;
using OpenTelemetry.Trace;
using ChatOptions = Microsoft.Extensions.AI.ChatOptions;

namespace AgentOps.Tests;

[TestClass]
public sealed class AgentTelemetryTests
{
    [TestMethod]
    public void FactoryUsesStableDistinctConfiguredIdentitiesAcrossNewAgentInstances()
    {
        var factory = new FoundryAgentFactory(Mock.Of<TokenCredential>(),
            Options.Create(new FoundryOptions
            {
                ProjectEndpoint = "https://test.services.ai.azure.com/api/projects/test",
                ModelDeployment = "test-model",
                AgentName = "configured-chat",
                InvestigationAgentName = "configured-investigation"
            }), NullLogger<FoundryAgentFactory>.Instance, Options.Create(new InsightsToolOptions()));

        var first = factory.Create("instructions");
        var second = factory.Create("instructions");
        var investigation = factory.Create("instructions", purpose: AgentPurpose.Investigation);
        first.Id.Should().Be("configured-chat");
        second.Id.Should().Be(first.Id);
        investigation.Id.Should().Be("configured-investigation");
        investigation.Name.Should().Be("configured-investigation");
    }

    [TestMethod]
    public async Task FrameworkExportsAgentModelToolAndTokensWithoutMessageContent()
    {
        var exporter = new ActivityExporter();
        using var provider = Sdk.CreateTracerProviderBuilder().AddSource(AgentTelemetry.SourceName)
            .AddProcessor(new SimpleActivityExportProcessor(exporter)).Build();
        var model = new Mock<IChatClient>();
        var calls = 0;
        model.Setup(client => client.GetResponseAsync(It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => ++calls == 1
                ? new ChatResponse(new ChatMessage(ChatRole.Assistant,
                    [new FunctionCallContent("call-1", "ReadEvidence", new Dictionary<string, object?>())]))
                { ModelId = "test-model", Usage = new() { InputTokenCount = 11, OutputTokenCount = 3 } }
                : new ChatResponse(new ChatMessage(ChatRole.Assistant, "private-model-output"))
                { ModelId = "test-model", Usage = new() { InputTokenCount = 17, OutputTokenCount = 5 } });
        var toolCalls = 0;
        var tool = AIFunctionFactory.Create(() =>
        {
            toolCalls++;
            return "private-tool-result";
        }, "ReadEvidence");
        var agent = AgentTelemetry.InstrumentAgent(new ChatClientAgent(
            AgentTelemetry.InstrumentChatClient(model.Object, 3), name: "test-investigation",
            instructions: "private-instructions", tools: [tool]));

        await agent.RunAsync("private-user-input");

        calls.Should().Be(2);
        toolCalls.Should().Be(1);
        var agentSpan = exporter.Activities.Single(activity => Equals(activity.GetTagItem("gen_ai.operation.name"), "invoke_agent"));
        agentSpan.GetTagItem("gen_ai.agent.name").Should().Be("test-investigation");
        var modelSpans = exporter.Activities.Where(activity => Equals(activity.GetTagItem("gen_ai.operation.name"), "chat")).ToArray();
        modelSpans.Should().HaveCount(2);
        modelSpans.Select(span => span.GetTagItem("gen_ai.usage.input_tokens")).Should().Equal(11L, 17L);
        modelSpans.Select(span => span.GetTagItem("gen_ai.usage.output_tokens")).Should().Equal(3L, 5L);
        var toolSpan = exporter.Activities.Single(activity => Equals(activity.GetTagItem("gen_ai.operation.name"), "execute_tool"));
        toolSpan.GetTagItem("gen_ai.tool.name").Should().Be("ReadEvidence");
        exporter.Activities.Should().OnlyContain(span => span.TraceId == agentSpan.TraceId);
        var payload = string.Join('|', exporter.Activities.SelectMany(span =>
            span.TagObjects.Select(tag => $"{tag.Key}={tag.Value}").Concat(span.Events.SelectMany(ev =>
                ev.Tags.Select(tag => $"{tag.Key}={tag.Value}")))));
        payload.Should().NotContain("private-");
    }

    private sealed class ActivityExporter : BaseExporter<Activity>
    {
        public List<Activity> Activities { get; } = [];
        public override ExportResult Export(in Batch<Activity> batch)
        {
            foreach (var activity in batch) Activities.Add(activity);
            return ExportResult.Success;
        }
    }
}
