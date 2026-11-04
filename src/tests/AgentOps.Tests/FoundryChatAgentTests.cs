using AgentOps.Web.Features.Agent;
using AgentOps.Web.Features.Chat;
using AgentOps.Web.Infrastructure;
using FluentAssertions;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using Moq;
using ChatOptions = AgentOps.Web.Infrastructure.ChatOptions;
using ModelMessage = Microsoft.Extensions.AI.ChatMessage;

namespace AgentOps.Tests;

[TestClass]
public sealed class FoundryChatAgentTests
{
    [TestMethod]
    [DataRow("actual-model-version", "actual-model-version")]
    [DataRow(null, "configured-deployment")]
    public async Task AdapterSendsHistoricalRolesAndTextWithOutputBoundAndRecordsModel(string? modelId, string expectedModel)
    {
        var client = new Mock<IChatClient>();
        ModelMessage[]? supplied = null;
        Microsoft.Extensions.AI.ChatOptions? suppliedOptions = null;
        client.Setup(chat => chat.GetResponseAsync(It.IsAny<IEnumerable<ModelMessage>>(),
                It.IsAny<Microsoft.Extensions.AI.ChatOptions>(), It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<ModelMessage>, Microsoft.Extensions.AI.ChatOptions, CancellationToken>((messages, options, _) =>
            {
                supplied = messages.ToArray();
                suppliedOptions = options;
            })
            .ReturnsAsync(new ChatResponse(new ModelMessage(ChatRole.Assistant, "Teal is the colour.")) { ModelId = modelId });
        var factory = new Mock<IFoundryAgentFactory>();
        var options = new ChatOptions { Instructions = "Text-only conversation instructions", MaxOutputTokens = 123 };
        factory.Setup(item => item.Create(options.Instructions)).Returns(new ChatClientAgent(client.Object));
        var adapter = new FoundryChatAgent(factory.Object, Options.Create(options),
            Options.Create(new FoundryOptions { ModelDeployment = "configured-deployment" }));
        var now = DateTimeOffset.UtcNow;
        AgentOps.Web.Features.Chat.ChatMessage[] history =
        [
            new("1", "owner", "session", "user", "Remember teal", now, 1),
            new("2", "owner", "session", "agent", "I will remember.", now, 2, "previous-model"),
            new("3", "owner", "session", "user", "Which colour?", now, 3)
        ];
        var reply = await adapter.ReplyAsync(history, CancellationToken.None);
        reply.Content.Should().Be("Teal is the colour.");
        reply.Model.Should().Be(expectedModel);
        supplied!.Select(message => message.Role).Should().Equal(ChatRole.User, ChatRole.Assistant, ChatRole.User);
        supplied!.Select(message => message.Text).Should().Equal("Remember teal", "I will remember.", "Which colour?");
        suppliedOptions!.MaxOutputTokens.Should().Be(123);
        factory.VerifyAll();
    }

    [TestMethod]
    public async Task EmptyModelOutputIsAnExplicitFailure()
    {
        var client = new Mock<IChatClient>();
        client.Setup(chat => chat.GetResponseAsync(It.IsAny<IEnumerable<ModelMessage>>(),
                It.IsAny<Microsoft.Extensions.AI.ChatOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatResponse(new ModelMessage(ChatRole.Assistant, "")));
        var factory = new Mock<IFoundryAgentFactory>();
        factory.Setup(item => item.Create(It.IsAny<string>())).Returns(new ChatClientAgent(client.Object));
        var adapter = new FoundryChatAgent(factory.Object, Options.Create(new ChatOptions()), Options.Create(new FoundryOptions()));
        var call = () => adapter.ReplyAsync([], CancellationToken.None);
        await call.Should().ThrowAsync<InvalidOperationException>().WithMessage("*no text*");
    }
}
