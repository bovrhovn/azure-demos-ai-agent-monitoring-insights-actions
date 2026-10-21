using System.Text.Json;
using AgentOps.Web.Features.Executions;
using AgentOps.Web.Infrastructure;
using AgentOps.Web.Features.Agent;
using AgentOps.Web.Features.Scheduling;
using FluentAssertions;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Hangfire;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Configuration;

namespace AgentOps.Tests;

[TestClass]
public sealed class CosmosEmulatorTests
{
    [TestMethod]
    [TestCategory("Emulator")]
    public async Task HangfireBackgroundExecutionPersistsRealCosmosHistoryBeyondSchedulerLifetime()
    {
        using var client = EmulatorClient();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var databaseId = "test-scheduler-" + Guid.NewGuid().ToString("N");
        var database = (await client.CreateDatabaseAsync(databaseId, cancellationToken: timeout.Token)).Database;
        try
        {
            await database.CreateContainerAsync("executions", "/trigger", cancellationToken: timeout.Token);
            var cosmos = Options.Create(new CosmosOptions { Database = databaseId });
            var store = new CosmosExecutionStore(client, cosmos);
            var builder = Host.CreateApplicationBuilder();
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["Scheduling:Cron"] = "0 0 1 1 *" });
            var services = builder.Services;
            services.AddSingleton<IExecutionStore>(store);
            services.AddSingleton<IAgentRunner, PendingAgentRunner>();
            var notifier = new CompletionNotifier();
            services.AddSingleton<IExecutionNotifier>(notifier);
            services.AddSingleton<TimeProvider>(TimeProvider.System);
            services.AddSingleton(Options.Create(new ExecutionOptions()));
            services.AddScoped<ExecuteAgent>();
            services.AddAgentScheduling();
            ExecutionRecord completed;
            using (var host = builder.Build())
            {
                await host.StartAsync(timeout.Token);
                host.Services.GetRequiredService<IRecurringJobManager>().Trigger("insights-agent");
                completed = await notifier.Completed.Task.WaitAsync(timeout.Token);
                completed.Status.Should().Be(ExecutionContract.NotImplemented);
                completed.RequestedBy.Should().Be("hangfire");
                completed.CompletedAt.Should().BeOnOrAfter(completed.StartedAt);
                notifier.Statuses.Should().Equal(ExecutionContract.Running, ExecutionContract.NotImplemented);
                await host.StopAsync(timeout.Token);
            }
            var freshHistory = new CosmosExecutionStore(client, cosmos);
            (await freshHistory.ListAsync(ExecutionContract.Schedule, 10, timeout.Token))
                .Should().ContainSingle().Which.Should().Be(completed);
        }
        finally
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await database.DeleteAsync(cancellationToken: cleanup.Token);
        }
    }

    [TestMethod]
    [TestCategory("Emulator")]
    public async Task CosmosRoundTripsCamelCasePartitionedHistoryAndOrdering()
    {
        using var client = EmulatorClient();
        var databaseId = "test-agentops-" + Guid.NewGuid().ToString("N");
        var database = (await client.CreateDatabaseAsync(databaseId)).Database;
        try
        {
            await database.CreateContainerAsync("executions", "/trigger");
            var store = new CosmosExecutionStore(client, Options.Create(new CosmosOptions { Database = databaseId, Container = "executions" }));
            var run = new ExecutionRecord("older", ExecutionContract.Endpoint, ExecutionContract.Running,
                DateTimeOffset.UtcNow.AddMinutes(-1), null, "test", "trace", null);
            await store.SaveAsync(run, CancellationToken.None);
            await store.SaveAsync(run with { Id = "newer", StartedAt = DateTimeOffset.UtcNow }, CancellationToken.None);
            await store.SaveAsync(run with { Id = "scheduled", Trigger = ExecutionContract.Schedule }, CancellationToken.None);
            await store.SaveAsync(run with { Status = ExecutionContract.NotImplemented, CompletedAt = DateTimeOffset.UtcNow }, CancellationToken.None);

            var history = await store.ListAsync(ExecutionContract.Endpoint, 2, CancellationToken.None);
            history.Select(x => x.Id).Should().Equal("newer", "older");
            history.Last().Status.Should().Be(ExecutionContract.NotImplemented);
            (await store.GetAsync("older", ExecutionContract.Endpoint, CancellationToken.None))!.Id.Should().Be("older");
            (await store.GetAsync("older", ExecutionContract.Schedule, CancellationToken.None)).Should().BeNull();
            (await store.ListAsync(ExecutionContract.Endpoint, 1, CancellationToken.None)).Should().ContainSingle(x => x.Id == "newer");
        }
        finally
        {
            await database.DeleteAsync();
        }
    }

    private static CosmosClient EmulatorClient()
    {
        var connection = Environment.GetEnvironmentVariable("AGENTOPS_TEST_COSMOS_CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(connection))
            Assert.Inconclusive("Set AGENTOPS_TEST_COSMOS_CONNECTION_STRING to opt into real local emulator tests.");
        var builder = new System.Data.Common.DbConnectionStringBuilder { ConnectionString = connection };
        var endpoint = new Uri((string)builder["AccountEndpoint"]);
        if (!endpoint.IsLoopback)
            Assert.Inconclusive("Destructive test setup is restricted to a loopback emulator, never a deployed account.");
        var options = new CosmosClientOptions
        {
            ConnectionMode = ConnectionMode.Gateway,
            UseSystemTextJsonSerializerWithOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web),
            RequestTimeout = TimeSpan.FromSeconds(10)
        };
        if (Environment.GetEnvironmentVariable("AGENTOPS_TEST_EMULATOR_INSECURE_TLS") == "true")
            options.HttpClientFactory = () => new HttpClient(new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
            });
        return new CosmosClient(connection, options);
    }

    private sealed class CompletionNotifier : IExecutionNotifier
    {
        public List<string> Statuses { get; } = [];
        public TaskCompletionSource<ExecutionRecord> Completed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task PublishAsync(ExecutionRecord execution, CancellationToken cancellationToken)
        {
            Statuses.Add(execution.Status);
            if (execution.Status != ExecutionContract.Running) Completed.TrySetResult(execution);
            return Task.CompletedTask;
        }
    }
}
