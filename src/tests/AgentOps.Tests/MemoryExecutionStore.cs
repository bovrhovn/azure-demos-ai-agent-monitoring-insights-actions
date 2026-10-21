using System.Collections.Concurrent;
using AgentOps.Web.Features.Executions;

namespace AgentOps.Tests;

internal sealed class MemoryExecutionStore : IExecutionStore
{
    private readonly ConcurrentDictionary<string, ExecutionRecord> records = new();
    public ConcurrentQueue<ExecutionRecord> Writes { get; } = new();
    public TaskCompletionSource<ExecutionRecord> ScheduledCompletion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task SaveAsync(ExecutionRecord execution, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        records[execution.Id] = execution;
        Writes.Enqueue(execution);
        if (execution.Trigger == ExecutionContract.Schedule && execution.CompletedAt is not null)
            ScheduledCompletion.TrySetResult(execution);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ExecutionRecord>> ListAsync(string trigger, int limit, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<ExecutionRecord>>(records.Values.Where(x => x.Trigger == trigger)
            .OrderByDescending(x => x.StartedAt).Take(limit).ToArray());

    public Task<ExecutionRecord?> GetAsync(string id, string trigger, CancellationToken cancellationToken) =>
        Task.FromResult(records.TryGetValue(id, out var record) && record.Trigger == trigger ? record : null);
}
