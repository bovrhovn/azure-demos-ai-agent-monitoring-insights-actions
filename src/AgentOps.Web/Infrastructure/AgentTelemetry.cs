using System.Diagnostics;
using Azure.Monitor.OpenTelemetry.Exporter;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace AgentOps.Web.Infrastructure;

public static class AgentTelemetry
{
    public const string SourceName = "AgentOps.GenAI";
    public static readonly ActivitySource ExecutionSource = new("AgentOps.Executions");

    public static IServiceCollection AddAgentTelemetry(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration["ApplicationInsights:ConnectionString"];
        if (string.IsNullOrWhiteSpace(connectionString)) return services;

        // Classic Application Insights retains web requests, dependencies, logs and TelemetryClient.
        services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService("AgentOps.Web"))
            .WithTracing(tracing => tracing
                .AddSource(SourceName, ExecutionSource.Name)
                .SetSampler(new AlwaysOnSampler())
                .AddAzureMonitorTraceExporter(exporter => exporter.ConnectionString = connectionString))
            .WithMetrics(metrics => metrics
                .AddMeter(SourceName)
                .AddAzureMonitorMetricExporter(exporter => exporter.ConnectionString = connectionString));
        return services;
    }

    public static IChatClient InstrumentChatClient(IChatClient client, int maxToolCalls) =>
        client.AsBuilder().UseFunctionInvocation(configure: invocation =>
        {
            invocation.AllowConcurrentInvocation = false;
            invocation.IncludeDetailedErrors = false;
            invocation.MaximumIterationsPerRequest = maxToolCalls;
        }).UseOpenTelemetry(sourceName: SourceName, configure: telemetry => telemetry.EnableSensitiveData = false).Build();

    public static AIAgent InstrumentAgent(AIAgent agent) =>
        agent.AsBuilder().UseOpenTelemetry(sourceName: SourceName,
            configure: telemetry => telemetry.EnableSensitiveData = false).Build();

    public static Activity StartExecution(string? correlationId)
    {
        var parent = correlationId is { Length: 32 } && correlationId.All(Uri.IsHexDigit)
            ? new ActivityContext(ActivityTraceId.CreateFromString(correlationId), ActivitySpanId.CreateRandom(), ActivityTraceFlags.Recorded)
            : Activity.Current?.Context ?? default;
        var activity = ExecutionSource.StartActivity("execute_agent", ActivityKind.Internal, parent);
        if (activity is not null) return activity;
        activity = new Activity("execute_agent").SetIdFormat(ActivityIdFormat.W3C);
        if (parent.TraceId != default) activity.SetParentId(parent.TraceId, parent.SpanId, parent.TraceFlags);
        return activity.Start();
    }
}
