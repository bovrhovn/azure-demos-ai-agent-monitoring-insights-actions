using Microsoft.AspNetCore.SignalR;

namespace AgentOps.Web.Features.Executions;

/// <summary>Read-only, authorized live feed; no client-invokable mutation methods.</summary>
public sealed class ExecutionHub : Hub;

/// <summary>Broadcasts committed execution records to authorized dashboard viewers.</summary>
public sealed class SignalRExecutionNotifier(IHubContext<ExecutionHub> hub) : IExecutionNotifier
{
    public Task PublishAsync(ExecutionRecord execution, CancellationToken cancellationToken) =>
        hub.Clients.All.SendAsync(ExecutionContract.ChangedEvent, execution, cancellationToken);
}
