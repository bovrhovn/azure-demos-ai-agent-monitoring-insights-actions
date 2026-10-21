using AgentOps.Web.Features.Agent;
using AgentOps.Web.Features.Executions;
using AgentOps.Web.Features.Scheduling;
using AgentOps.Web.Infrastructure;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace AgentOps.Tests;

[TestClass]
public sealed class ExecutionTests
{
    private readonly MemoryExecutionStore store = new();
    private readonly Mock<IAgentRunner> runner = new();
    private readonly Mock<IExecutionNotifier> notifier = new();
    private readonly DateTimeOffset now = new(2026, 10, 19, 10, 0, 0, TimeSpan.Zero);

    private ExecuteAgent Handler() => new(store, runner.Object, notifier.Object, new FixedClock(now),
        Options.Create(new ExecutionOptions()), NullLogger<ExecuteAgent>.Instance);

    [TestMethod]
    [DataRow(ExecutionContract.Endpoint)]
    [DataRow(ExecutionContract.Schedule)]
    public async Task BothTriggersPersistAndPublishStartedThenFinalState(string trigger)
    {
        runner.Setup(x => x.RunAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentOutcome(ExecutionContract.NotImplemented, "TODO"));

        var execution = await Handler().RunAsync(trigger, "test", CancellationToken.None);

        execution.Trigger.Should().Be(trigger);
        execution.Status.Should().Be(ExecutionContract.NotImplemented);
        execution.StartedAt.Should().Be(now);
        execution.CompletedAt.Should().Be(now);
        execution.CorrelationId.Should().NotBeNullOrWhiteSpace();
        store.Writes.Select(x => x.Status).Should().Equal(ExecutionContract.Running, ExecutionContract.NotImplemented);
        notifier.Verify(x => x.PublishAsync(It.Is<ExecutionRecord>(record => record.Status == ExecutionContract.Running),
            It.IsAny<CancellationToken>()), Times.Once);
        notifier.Verify(x => x.PublishAsync(execution, It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    public async Task AgentFailureIsPersistedAndRethrownWithoutLeakingExceptionInHistory()
    {
        runner.Setup(x => x.RunAsync(It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException("private error detail"));

        var action = () => Handler().RunAsync(ExecutionContract.Endpoint, "test", CancellationToken.None);

        await action.Should().ThrowAsync<InvalidOperationException>();
        var record = store.Writes.Last();
        record.Status.Should().Be(ExecutionContract.Failed);
        record.Summary.Should().NotContain("private error detail");
        record.CompletedAt.Should().NotBeNull();
    }

    [TestMethod]
    public async Task CanceledCallerStillPersistsFinalStateWithIndependentToken()
    {
        using var cancellation = new CancellationTokenSource();
        runner.Setup(x => x.RunAsync(It.IsAny<CancellationToken>())).Returns<CancellationToken>(_ =>
        {
            cancellation.Cancel();
            return Task.FromCanceled<AgentOutcome>(cancellation.Token);
        });

        var action = () => Handler().RunAsync(ExecutionContract.Endpoint, "test", cancellation.Token);

        await action.Should().ThrowAsync<OperationCanceledException>();
        store.Writes.Last().Status.Should().Be(ExecutionContract.Canceled);
        store.Writes.Last().CompletedAt.Should().NotBeNull();
    }

    [TestMethod]
    [DataRow(ExecutionContract.Endpoint)]
    [DataRow(ExecutionContract.Schedule)]
    public async Task NotificationFailureDoesNotChangeDurableOutcome(string trigger)
    {
        notifier.Setup(x => x.PublishAsync(It.IsAny<ExecutionRecord>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("disconnected"));
        runner.Setup(x => x.RunAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new AgentOutcome(ExecutionContract.NotImplemented, "TODO"));

        var result = await Handler().RunAsync(trigger, "test", CancellationToken.None);

        result.Status.Should().Be(ExecutionContract.NotImplemented);
        store.Writes.Should().HaveCount(2);
    }

    [TestMethod]
    public async Task InitialPersistenceFailurePreventsAgentInvocation()
    {
        var failingStore = new Mock<IExecutionStore>();
        failingStore.Setup(x => x.SaveAsync(It.IsAny<ExecutionRecord>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("storage unavailable"));
        var handler = new ExecuteAgent(failingStore.Object, runner.Object, notifier.Object, TimeProvider.System,
            Options.Create(new ExecutionOptions()), NullLogger<ExecuteAgent>.Instance);

        var action = () => handler.RunAsync(ExecutionContract.Endpoint, "test", CancellationToken.None);

        await action.Should().ThrowAsync<IOException>();
        runner.Verify(x => x.RunAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [TestMethod]
    public async Task FinalPersistenceFailureIsNotReportedAsSuccessfulExecution()
    {
        var failingStore = new Mock<IExecutionStore>();
        failingStore.Setup(x => x.SaveAsync(It.Is<ExecutionRecord>(r => r.Status != ExecutionContract.Running), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("write unavailable"));
        runner.Setup(x => x.RunAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new AgentOutcome(ExecutionContract.NotImplemented, "TODO"));
        var handler = new ExecuteAgent(failingStore.Object, runner.Object, notifier.Object, TimeProvider.System,
            Options.Create(new ExecutionOptions()), NullLogger<ExecuteAgent>.Instance);

        var action = () => handler.RunAsync(ExecutionContract.Endpoint, "test", CancellationToken.None);

        await action.Should().ThrowAsync<IOException>();
    }

    [TestMethod]
    public async Task ScheduledJobUsesSharedPipelineAndScheduleTrigger()
    {
        runner.Setup(x => x.RunAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new AgentOutcome(ExecutionContract.NotImplemented, "TODO"));
        await new AgentJob(Handler()).RunAsync(CancellationToken.None);
        store.Writes.Last().Trigger.Should().Be(ExecutionContract.Schedule);
        store.Writes.Last().RequestedBy.Should().Be("hangfire");
    }

    [TestMethod]
    public async Task StubExplicitlyReportsNotImplemented()
    {
        var result = await new PendingAgentRunner(NullLogger<PendingAgentRunner>.Instance).RunAsync(CancellationToken.None);
        result.Status.Should().Be(ExecutionContract.NotImplemented);
        result.Summary.Should().Contain("No model or action");
    }

    [TestMethod]
    public async Task InvalidTriggerIsRejectedBeforeWriting()
    {
        var action = () => Handler().RunAsync("unknown", "test", CancellationToken.None);
        await action.Should().ThrowAsync<ArgumentException>();
        store.Writes.Should().BeEmpty();
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
