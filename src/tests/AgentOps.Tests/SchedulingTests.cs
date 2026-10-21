using AgentOps.Web.Features.Scheduling;
using FluentAssertions;
using Hangfire;
using Hangfire.Common;
using Hangfire.InMemory;
using Hangfire.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Hosting;
using Moq;

namespace AgentOps.Tests;

[TestClass]
public sealed class SchedulingTests
{
    [TestMethod]
    public async Task ShippingStorageRegistersOneTypedUtcJobAndDoesNotRetryAgentFailures()
    {
        await using var factory = new AppFactory();
        using var client = factory.Client();
        var storage = factory.Services.GetRequiredService<JobStorage>();
        storage.Should().BeOfType<InMemoryStorage>();
        using var connection = storage.GetConnection();
        var recurring = connection.GetRecurringJobs().Should().ContainSingle().Subject;
        recurring.Id.Should().Be("insights-agent");
        recurring.Cron.Should().Be("0 0 1 1 *");
        recurring.TimeZoneId.Should().Be(TimeZoneInfo.Utc.Id);
        recurring.Job.Type.Should().Be(typeof(AgentJob));
        recurring.Job.Method.Name.Should().Be(nameof(AgentJob.RunAsync));
        recurring.NextExecution.Should().NotBeNull();
        await factory.Services.GetServices<IHostedService>().OfType<RecurringScheduleRegistration>().Single()
            .StartAsync(CancellationToken.None);
        connection.GetRecurringJobs().Should().ContainSingle("startup registration must be idempotent");
        var retry = JobFilterProviders.Providers.GetFilters(Job.FromExpression<AgentJob>(job => job.RunAsync(CancellationToken.None)))
            .Select(filter => filter.Instance).OfType<AutomaticRetryAttribute>().Single();
        retry.Attempts.Should().Be(0);
    }

    [TestMethod]
    public async Task DisabledScheduleIsNotRegisteredButServerStillAcceptsBackgroundWork()
    {
        await using var factory = new AppFactory(new Dictionary<string, string?> { ["Scheduling:Enabled"] = "false" });
        using var client = factory.Client();
        using var connection = factory.Services.GetRequiredService<JobStorage>().GetConnection();
        connection.GetRecurringJobs().Should().BeEmpty();
        factory.Services.GetRequiredService<IBackgroundJobClient>().Enqueue<AgentJob>(job => job.RunAsync(CancellationToken.None));
        var execution = await factory.Store.ScheduledCompletion.Task.WaitAsync(TimeSpan.FromSeconds(20));
        execution.RequestedBy.Should().Be("hangfire");
    }

    [TestMethod]
    public async Task RestartDropsQueuedStateAndReRegistersRecurringDefinition()
    {
        await using (var first = new AppFactory())
        {
            using var client = first.Client();
            first.Services.GetRequiredService<IBackgroundJobClient>().Schedule<AgentJob>(
                job => job.RunAsync(CancellationToken.None), TimeSpan.FromDays(1));
            first.Services.GetRequiredService<JobStorage>().GetMonitoringApi().ScheduledCount().Should().Be(1);
        }
        await using var restarted = new AppFactory();
        using var restartedClient = restarted.Client();
        var storage = restarted.Services.GetRequiredService<JobStorage>();
        storage.GetMonitoringApi().ScheduledCount().Should().Be(0);
        using var connection = storage.GetConnection();
        connection.GetRecurringJobs().Should().ContainSingle(job => job.Id == "insights-agent");
        restarted.Store.Writes.Should().BeEmpty("lost jobs must not be silently replayed");
    }

    [TestMethod]
    public async Task DisablingRegistrationRemovesExistingDefinition()
    {
        await using var factory = new AppFactory();
        using var client = factory.Client();
        var storage = factory.Services.GetRequiredService<JobStorage>();
        var manager = factory.Services.GetRequiredService<IRecurringJobManager>();
        var registration = new RecurringScheduleRegistration(manager, Options.Create(new SchedulingOptions { Enabled = false }),
            NullLogger<RecurringScheduleRegistration>.Instance);
        await registration.StartAsync(CancellationToken.None);
        using var connection = storage.GetConnection();
        connection.GetRecurringJobs().Should().BeEmpty();
    }

    [TestMethod]
    public async Task RegistrationFailurePropagatesFromStartup()
    {
        var manager = new Mock<IRecurringJobManager>();
        manager.Setup(service => service.AddOrUpdate(It.IsAny<string>(), It.IsAny<Job>(), It.IsAny<string>(),
                It.IsAny<RecurringJobOptions>())).Throws(new IOException("storage unavailable"));
        var registration = new RecurringScheduleRegistration(manager.Object, Options.Create(new SchedulingOptions()),
            NullLogger<RecurringScheduleRegistration>.Instance);
        var action = () => registration.StartAsync(CancellationToken.None);
        await action.Should().ThrowAsync<IOException>();
    }
}
