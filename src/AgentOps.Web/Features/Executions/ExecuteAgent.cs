using System.Diagnostics;
using AgentOps.Web.Features.Agent;
using AgentOps.Web.Infrastructure;
using AgentOps.Web.Infrastructure.Options;
using Microsoft.Extensions.Options;

namespace AgentOps.Web.Features.Executions;

/// <summary>Shared execution handler for HTTP and scheduled callers.</summary>
public sealed class ExecuteAgent(
    IExecutionStore store,
    IAgentRunner runner,
    IExecutionNotifier notifier,
    TimeProvider clock,
    IOptions<ExecutionOptions> options,
    ILogger<ExecuteAgent> logger)
{
    public async Task<ExecutionRecord> RunAsync(string trigger, string requestedBy, CancellationToken cancellationToken,
        string? executionId = null)
    {
        if (trigger is not (ExecutionContract.Endpoint or ExecutionContract.Schedule))
            throw new ArgumentException("Unknown execution trigger.", nameof(trigger));
        ArgumentException.ThrowIfNullOrWhiteSpace(requestedBy);
        var previous = executionId is null ? null : await store.GetAsync(executionId, trigger, cancellationToken);
        if (previous?.Status == ExecutionContract.Completed) return previous;
        var execution = new ExecutionRecord(executionId ?? Guid.NewGuid().ToString("N"), trigger, ExecutionContract.Running,
            previous?.StartedAt ?? clock.GetUtcNow(), null, requestedBy,
            previous?.CorrelationId ?? Activity.Current?.TraceId.ToString() ?? Guid.NewGuid().ToString("N"), null);
        using var scope = logger.BeginScope(new Dictionary<string, object>
        {
            ["ExecutionId"] = execution.Id,
            ["Trigger"] = trigger,
            ["CorrelationId"] = execution.CorrelationId
        });
        await store.SaveAsync(execution, cancellationToken);
        await NotifyAsync(execution);
        logger.LogInformation("Agent execution started");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(options.Value.TimeoutSeconds));
        try
        {
            var outcome = await runner.RunAsync(execution, timeout.Token);
            execution = execution with { Status = outcome.Status, Summary = outcome.Summary, CompletedAt = clock.GetUtcNow() };
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            execution = execution with { Status = ExecutionContract.Canceled, Summary = "Execution was canceled or timed out.", CompletedAt = clock.GetUtcNow() };
            await CompleteAsync(execution);
            logger.LogWarning("Agent execution canceled");
            throw;
        }
        catch (Exception exception)
        {
            execution = execution with { Status = ExecutionContract.Failed, Summary = "Agent execution failed. See correlated application logs.", CompletedAt = clock.GetUtcNow() };
            await CompleteAsync(execution);
            logger.LogError(exception, "Agent execution failed");
            throw;
        }
        await CompleteAsync(execution);
        logger.LogInformation("Agent execution finished with status {ExecutionStatus}", execution.Status);
        return execution;
    }

    private async Task CompleteAsync(ExecutionRecord execution)
    {
        // A disconnected HTTP caller must not leave a completed run marked Running.
        using var finalization = new CancellationTokenSource(TimeSpan.FromSeconds(options.Value.FinalizationTimeoutSeconds));
        await store.SaveAsync(execution, finalization.Token);
        await NotifyAsync(execution);
    }

    private async Task NotifyAsync(ExecutionRecord execution)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(options.Value.FinalizationTimeoutSeconds));
            await notifier.PublishAsync(execution, timeout.Token);
        }
        catch (Exception exception)
        {
            // Notifications are best-effort; Cosmos history remains authoritative.
            logger.LogWarning(exception, "Live notification failed for execution {ExecutionId}; refresh history to recover", execution.Id);
        }
    }
}
