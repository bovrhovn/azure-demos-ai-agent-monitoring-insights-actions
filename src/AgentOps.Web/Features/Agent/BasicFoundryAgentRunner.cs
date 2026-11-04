using System.Diagnostics;
using AgentOps.Web.Features.Chat;
using AgentOps.Web.Infrastructure;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using ChatOptions = AgentOps.Web.Infrastructure.ChatOptions;
using ChatMessage = AgentOps.Web.Features.Chat.ChatMessage;

namespace AgentOps.Web.Features.Agent;

public sealed class BasicFoundryAgentRunner(IFoundryAgentFactory factory, IOptions<ChatOptions> options,
    IOptions<FoundryOptions> foundry, ILogger<BasicFoundryAgentRunner> logger) : IChatAgent
{
    public async Task<ChatReply> ReplyAsync(IReadOnlyList<ChatMessage> history, CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        logger.LogInformation("Invoking Foundry deployment {ModelDeployment} with {MessageCount} conversation messages",
            foundry.Value.ModelDeployment, history.Count);
        try
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
            var reply = new ChatReply(response.Text, string.IsNullOrWhiteSpace(model) ? foundry.Value.ModelDeployment : model);
            logger.LogInformation("Foundry model {Model} returned a chat reply in {ElapsedMilliseconds} ms",
                reply.Model, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            return reply;
        }
        catch (OperationCanceledException exception)
        {
            logger.LogWarning(exception, "Foundry invocation for deployment {ModelDeployment} was canceled", foundry.Value.ModelDeployment);
            throw;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Foundry invocation for deployment {ModelDeployment} failed", foundry.Value.ModelDeployment);
            throw;
        }
    }
}
