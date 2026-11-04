using AgentOps.Web.Features.Agent;
using AgentOps.Web.Infrastructure;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using ChatOptions = AgentOps.Web.Infrastructure.ChatOptions;

namespace AgentOps.Web.Features.Chat;

public sealed class FoundryChatAgent(IFoundryAgentFactory factory, IOptions<ChatOptions> options,
    IOptions<FoundryOptions> foundry) : IChatAgent
{
    public async Task<ChatReply> ReplyAsync(IReadOnlyList<ChatMessage> history, CancellationToken cancellationToken)
    {
        var agent = factory.Create(options.Value.Instructions);
        var messages = history.Select(message => new Microsoft.Extensions.AI.ChatMessage(
            message.Role == "user" ? ChatRole.User : ChatRole.Assistant, message.Content));
        var response = await agent.RunAsync(messages, options: new ChatClientAgentRunOptions(new()
        {
            MaxOutputTokens = options.Value.MaxOutputTokens
        }), cancellationToken: cancellationToken);
        if (string.IsNullOrWhiteSpace(response.Text))
            throw new InvalidOperationException("Foundry returned no text for the chat response.");
        var model = (response.RawRepresentation as ChatResponse)?.ModelId;
        return new(response.Text, string.IsNullOrWhiteSpace(model) ? foundry.Value.ModelDeployment : model);
    }
}
