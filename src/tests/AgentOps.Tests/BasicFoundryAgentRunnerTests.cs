using System.Net;
using System.ClientModel;
using System.Net.Http.Json;
using AgentOps.Web;
using AgentOps.Web.Features.Agent;
using AgentOps.Web.Features.Chat;
using AgentOps.Web.Features.Insights;
using AgentOps.Web.Infrastructure;
using AgentOps.Web.Infrastructure.Options;
using FluentAssertions;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Moq;
using ChatOptions = AgentOps.Web.Infrastructure.Options.ChatOptions;
using ModelMessage = Microsoft.Extensions.AI.ChatMessage;

namespace AgentOps.Tests;

[TestClass]
public sealed class BasicFoundryAgentRunnerTests
{
    [TestMethod]
    public async Task HttpChatUsesFrameworkRunnerAndPersistsTheReturnedReplyWithItsModel()
    {
        var client = new Mock<IChatClient>();
        var calls = new List<ModelMessage[]>();
        client.Setup(chat => chat.GetResponseAsync(It.IsAny<IEnumerable<ModelMessage>>(),
                It.IsAny<Microsoft.Extensions.AI.ChatOptions>(), It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<ModelMessage>, Microsoft.Extensions.AI.ChatOptions, CancellationToken>((messages, _, _) =>
                calls.Add(messages.ToArray()))
            .ReturnsAsync(new ChatResponse(new ModelMessage(ChatRole.Assistant, "A real framework reply.")) { ModelId = "model-version" });
        var factory = new Mock<IFoundryAgentFactory>();
        factory.Setup(item => item.Create(It.IsAny<string>(), It.IsAny<IList<AITool>>(), AgentPurpose.Chat)).Returns(new ChatClientAgent(client.Object));
        await using var app = new AppFactory();
        await using var host = app.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IChatAgent>();
            services.AddSingleton<IChatAgent, BasicFoundryAgentRunner>();
            services.RemoveAll<IFoundryAgentFactory>();
            services.AddSingleton(factory.Object);
        }));
        using var http = host.CreateClient();
        http.DefaultRequestHeaders.Add("X-Test-Role", "AgentOps.Operator");
        await ChatTests.AuthorizePosts(http);
        var created = await http.PostAsync(RouteConstants.ChatSessions, null);
        created.EnsureSuccessStatusCode();
        var session = (await created.Content.ReadFromJsonAsync<ChatSession>())!;
        var path = $"{RouteConstants.ChatSessions}/{session.Id}/messages";
        foreach (var prompt in new[] { "Remember teal.", "Which colour?" })
        {
            var response = await http.PostAsJsonAsync(path, new { content = prompt });
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            var reply = (await response.Content.ReadFromJsonAsync<AgentOps.Web.Features.Chat.ChatMessage>())!;
            reply.Content.Should().Be("A real framework reply.");
            reply.Role.Should().Be("agent");
            reply.Model.Should().Be("model-version");
        }
        var history = (await http.GetFromJsonAsync<ChatHistory>(path))!;
        history.Items.Select(message => message.Role).Should().Equal("user", "agent", "user", "agent");
        history.Items.Last().Content.Should().Be("A real framework reply.");
        calls.Last().Select(message => message.Role).Should().Equal(ChatRole.User, ChatRole.Assistant, ChatRole.User);
        calls.Last().Select(message => message.Text).Should().Equal("Remember teal.", "A real framework reply.", "Which colour?");
        app.ChatAgent.Calls.Should().BeEmpty("chat must use the registered framework runner, not the test-host fake");
    }

    [TestMethod]
    [DataRow(429, HttpStatusCode.ServiceUnavailable)]
    [DataRow(500, HttpStatusCode.InternalServerError)]
    public async Task UpstreamRateLimitIsActionableWithoutLeakingProviderDetailsOrLosingThePrompt(int upstreamStatus, HttpStatusCode expected)
    {
        var client = new Mock<IChatClient>();
        client.Setup(chat => chat.GetResponseAsync(It.IsAny<IEnumerable<ModelMessage>>(),
                It.IsAny<Microsoft.Extensions.AI.ChatOptions>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ClientResultException("private provider detail",
                Mock.Of<System.ClientModel.Primitives.PipelineResponse>(response => response.Status == upstreamStatus)));
        var factory = new Mock<IFoundryAgentFactory>();
        factory.Setup(item => item.Create(It.IsAny<string>(), It.IsAny<IList<AITool>>(), AgentPurpose.Chat)).Returns(new ChatClientAgent(client.Object));
        await using var app = new AppFactory();
        await using var host = app.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IChatAgent>();
            services.AddSingleton<IChatAgent, BasicFoundryAgentRunner>();
            services.RemoveAll<IFoundryAgentFactory>();
            services.AddSingleton(factory.Object);
        }));
        using var http = host.CreateClient();
        http.DefaultRequestHeaders.Add("X-Test-Role", "AgentOps.Operator");
        await ChatTests.AuthorizePosts(http);
        var created = await http.PostAsync(RouteConstants.ChatSessions, null);
        created.EnsureSuccessStatusCode();
        var session = (await created.Content.ReadFromJsonAsync<ChatSession>())!;
        var response = await http.PostAsJsonAsync($"{RouteConstants.ChatSessions}/{session.Id}/messages", new { content = "Saved before throttling" });
        response.StatusCode.Should().Be(expected);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().NotContain("private provider detail");
        if (upstreamStatus == 429)
            body.Should().Contain("Foundry is temporarily rate limited.").And.Contain("Foundry:ModelDeployment");
        else body.Should().NotContain("temporarily rate limited");
        app.Chats.Messages.Should().ContainSingle().Which.Content.Should().Be("Saved before throttling");
        client.Verify(chat => chat.GetResponseAsync(It.IsAny<IEnumerable<ModelMessage>>(),
            It.IsAny<Microsoft.Extensions.AI.ChatOptions>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    [DataRow("", "configured-model")]
    [DataRow("http://insecure.example/api/projects/test", "configured-model")]
    [DataRow("https://test.services.ai.azure.com/api/projects/test", "")]
    public async Task MissingOrInvalidConfigurationReturnsActionable503AfterSavingThePrompt(string endpoint, string model)
    {
        await using var app = new AppFactory(new Dictionary<string, string?>
        {
            ["Foundry:ProjectEndpoint"] = endpoint, ["Foundry:ModelDeployment"] = model
        });
        await using var host = app.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IChatAgent>();
            services.AddSingleton<IChatAgent, BasicFoundryAgentRunner>();
        }));
        using var http = host.CreateClient();
        http.DefaultRequestHeaders.Add("X-Test-Role", "AgentOps.Operator");
        await ChatTests.AuthorizePosts(http);
        var created = await http.PostAsync(RouteConstants.ChatSessions, null);
        created.EnsureSuccessStatusCode();
        var session = (await created.Content.ReadFromJsonAsync<ChatSession>())!;
        var response = await http.PostAsJsonAsync($"{RouteConstants.ChatSessions}/{session.Id}/messages", new { content = "Saved prompt" });
        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        var problem = await response.Content.ReadFromJsonAsync<Microsoft.AspNetCore.Mvc.ProblemDetails>();
        problem!.Title.Should().Be("Foundry is not configured.");
        problem.Detail.Should().Contain("Foundry:ProjectEndpoint").And.Contain("Foundry:ModelDeployment");
        problem.Extensions["traceId"]?.ToString().Should().NotBeNullOrWhiteSpace();
        app.Chats.Messages.Should().ContainSingle().Which.Content.Should().Be("Saved prompt");
        app.Chats.Messages.Should().OnlyContain(message => message.Role == "user");
    }

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
        factory.Setup(item => item.Create(It.Is<string>(text => text.StartsWith(options.Instructions)), It.IsAny<IList<AITool>>(), AgentPurpose.Chat))
            .Returns(new ChatClientAgent(client.Object));
        var adapter = new BasicFoundryAgentRunner(factory.Object, Options.Create(options),
            Options.Create(new FoundryOptions { ModelDeployment = "configured-deployment" }), NullLogger<BasicFoundryAgentRunner>.Instance,
            Mock.Of<ITelemetryQuery>(), Options.Create(new InsightsOptions()), Options.Create(new InsightsToolOptions()), TimeProvider.System);
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
    public async Task OutputLimitIsDisclosedRatherThanReturningASilentlyClippedAnswer()
    {
        var client = new Mock<IChatClient>();
        client.Setup(chat => chat.GetResponseAsync(It.IsAny<IEnumerable<ModelMessage>>(),
                It.IsAny<Microsoft.Extensions.AI.ChatOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatResponse(new ModelMessage(ChatRole.Assistant, "Partial explanation"))
                { FinishReason = ChatFinishReason.Length });
        var factory = new Mock<IFoundryAgentFactory>();
        factory.Setup(item => item.Create(It.IsAny<string>(), It.IsAny<IList<AITool>>(), AgentPurpose.Chat)).Returns(new ChatClientAgent(client.Object));
        var runner = new BasicFoundryAgentRunner(factory.Object, Options.Create(new ChatOptions()), Options.Create(new FoundryOptions()),
            NullLogger<BasicFoundryAgentRunner>.Instance, Mock.Of<ITelemetryQuery>(), Options.Create(new InsightsOptions()),
            Options.Create(new InsightsToolOptions()), TimeProvider.System);
        var reply = await runner.ReplyAsync([], CancellationToken.None);
        reply.Content.Should().StartWith("Partial explanation").And.Contain("truncated by the configured output limit");
    }

    [TestMethod]
    public async Task EmptyModelOutputIsAnExplicitFailure()
    {
        var client = new Mock<IChatClient>();
        client.Setup(chat => chat.GetResponseAsync(It.IsAny<IEnumerable<ModelMessage>>(),
                It.IsAny<Microsoft.Extensions.AI.ChatOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatResponse(new ModelMessage(ChatRole.Assistant, "")));
        var factory = new Mock<IFoundryAgentFactory>();
        factory.Setup(item => item.Create(It.IsAny<string>(), It.IsAny<IList<AITool>>(), AgentPurpose.Chat)).Returns(new ChatClientAgent(client.Object));
        var adapter = new BasicFoundryAgentRunner(factory.Object, Options.Create(new ChatOptions()), Options.Create(new FoundryOptions()),
            NullLogger<BasicFoundryAgentRunner>.Instance, Mock.Of<ITelemetryQuery>(), Options.Create(new InsightsOptions()),
            Options.Create(new InsightsToolOptions()), TimeProvider.System);
        var call = () => adapter.ReplyAsync([], CancellationToken.None);
        await call.Should().ThrowAsync<InvalidOperationException>().WithMessage("*no text*");
    }

    [TestMethod]
    public async Task RunnerPassesCancellationToTheFrameworkWithoutReturningASyntheticReply()
    {
        using var cancellation = new CancellationTokenSource();
        var client = new Mock<IChatClient>();
        client.Setup(chat => chat.GetResponseAsync(It.IsAny<IEnumerable<ModelMessage>>(),
                It.IsAny<Microsoft.Extensions.AI.ChatOptions>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException(cancellation.Token));
        var factory = new Mock<IFoundryAgentFactory>();
        factory.Setup(item => item.Create(It.IsAny<string>(), It.IsAny<IList<AITool>>(), AgentPurpose.Chat)).Returns(new ChatClientAgent(client.Object));
        var runner = new BasicFoundryAgentRunner(factory.Object, Options.Create(new ChatOptions()), Options.Create(new FoundryOptions()),
            NullLogger<BasicFoundryAgentRunner>.Instance, Mock.Of<ITelemetryQuery>(), Options.Create(new InsightsOptions()),
            Options.Create(new InsightsToolOptions()), TimeProvider.System);
        var call = () => runner.ReplyAsync([], cancellation.Token);
        await call.Should().ThrowAsync<OperationCanceledException>();
        client.Verify(chat => chat.GetResponseAsync(It.IsAny<IEnumerable<ModelMessage>>(),
            It.IsAny<Microsoft.Extensions.AI.ChatOptions>(), cancellation.Token), Times.Once);
    }
}
