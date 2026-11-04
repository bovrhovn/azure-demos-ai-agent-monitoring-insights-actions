using Microsoft.AspNetCore.SignalR;

namespace AgentOps.Web.Features.Executions;

/// <summary>Read-only, authorized live feed; no client-invokable mutation methods.</summary>
public sealed class ExecutionHub : Hub;
