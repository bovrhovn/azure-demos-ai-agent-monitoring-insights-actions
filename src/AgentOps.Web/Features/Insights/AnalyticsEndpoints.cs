using AgentOps.Web.Features.Executions;
using AgentOps.Web.Features.Identity;
using AgentOps.Web.Features.Scheduling;
using AgentOps.Web.Infrastructure;
using Hangfire;
using Hangfire.Storage;
using Microsoft.Extensions.Options;

namespace AgentOps.Web.Features.Insights;

public sealed record AnalyticsPage<T>(IReadOnlyList<T> Items, int Page, int PageSize, int TotalCount, int Limit)
{
    public int TotalPages => Math.Max(1, (TotalCount + PageSize - 1) / PageSize);
}

/// <summary>Read-only session endpoints; action APIs retain their bearer-only policy.</summary>
public static class AnalyticsEndpoints
{
    public static IEndpointRouteBuilder MapAnalyticsEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(RouteConstants.AnalyticsExecutions, async (string trigger, int? page, int? pageSize,
            IExecutionStore store, IOptions<ExecutionOptions> executions, IOptions<AnalyticsOptions> analytics,
            ILoggerFactory loggerFactory, CancellationToken cancellationToken) =>
        {
            if (trigger is not (ExecutionContract.Endpoint or ExecutionContract.Schedule))
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["trigger"] = ["Use Endpoint or Schedule."] });
            if (InvalidPaging(page, pageSize, analytics.Value) is { } error) return error;
            var records = await store.ListAsync(trigger, executions.Value.HistoryLimit, cancellationToken);
            var result = Paginate(records, page, pageSize, analytics.Value, executions.Value.HistoryLimit);
            loggerFactory.CreateLogger(typeof(AnalyticsEndpoints)).LogInformation(
                "Execution analytics loaded for {Trigger}; page {Page}, size {PageSize}, {ExecutionCount} executions, total {TotalCount}, limit {HistoryLimit}",
                trigger, result.Page, result.PageSize, result.Items.Count, result.TotalCount, result.Limit);
            return Results.Ok(result);
        }).RequireAuthorization(Policies.View);

        endpoints.MapGet(RouteConstants.AnalyticsOverview, async (IInsightsReader reader, IOptions<InsightsOptions> insights,
            ILoggerFactory loggerFactory, CancellationToken cancellationToken) =>
        {
            var logger = loggerFactory.CreateLogger(typeof(AnalyticsEndpoints));
            if (!reader.IsConfigured) return NotConfigured(logger);
            var overview = await reader.OverviewAsync(cancellationToken);
            logger.LogInformation("Overview analytics loaded with lookback {LookbackMinutes} minutes", insights.Value.LookbackMinutes);
            return Results.Ok(new { Overview = overview, insights.Value.LookbackMinutes });
        }).RequireAuthorization(Policies.View);

        endpoints.MapGet(RouteConstants.AnalyticsLogs, async (int? page, int? pageSize, IInsightsReader reader,
            IOptions<InsightsOptions> insights, IOptions<AnalyticsOptions> analytics, HttpResponse response,
            ILoggerFactory loggerFactory, CancellationToken cancellationToken) =>
        {
            response.Headers.CacheControl = "no-store";
            if (InvalidPaging(page, pageSize, analytics.Value) is { } error) return error;
            var logger = loggerFactory.CreateLogger(typeof(AnalyticsEndpoints));
            if (!reader.IsConfigured) return NotConfigured(logger);
            var logs = await reader.LogsAsync(cancellationToken);
            var result = Paginate(logs, page, pageSize, analytics.Value, insights.Value.LogLimit);
            logger.LogInformation(
                "Log analytics loaded; page {Page}, size {PageSize}, {LogCount} entries, total {TotalCount}, limit {LogLimit}, lookback {LookbackMinutes} minutes",
                result.Page, result.PageSize, result.Items.Count, result.TotalCount, result.Limit, insights.Value.LookbackMinutes);
            return Results.Ok(result);
        }).RequireAuthorization(Policies.View)
            .Produces<AnalyticsPage<InsightLog>>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        endpoints.MapGet(RouteConstants.AnalyticsSchedule, (JobStorage storage, IOptions<SchedulingOptions> options,
            ILoggerFactory loggerFactory) =>
        {
            var settings = options.Value;
            using var connection = storage.GetConnection();
            var schedule = connection.GetRecurringJobs().SingleOrDefault(job => job.Id == settings.RecurringJobId);
            loggerFactory.CreateLogger(typeof(AnalyticsEndpoints)).LogInformation(
                "Schedule analytics loaded for {RecurringJobId}; enabled {Enabled}, registered {ScheduleFound}",
                settings.RecurringJobId, settings.Enabled, schedule is not null);
            return Results.Ok(new
            {
                settings.RecurringJobId, settings.Enabled, settings.Cron,
                schedule?.NextExecution, schedule?.LastExecution
            });
        }).RequireAuthorization(Policies.View);
        return endpoints;
    }

    private static IResult NotConfigured(ILogger logger)
    {
        logger.LogInformation("Application Insights query skipped because telemetry is not configured");
        return Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable,
            title: "Telemetry is not configured.",
            detail: "Set Insights:ResourceId and grant Monitoring Reader access to query Application Insights.");
    }

    private static IResult? InvalidPaging(int? page, int? pageSize, AnalyticsOptions options)
    {
        if (page is <= 0 || pageSize is <= 0 || pageSize > options.MaxPageSize)
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["paging"] = [$"Page must be positive and pageSize must be between 1 and {options.MaxPageSize}."]
            });
        return null;
    }

    private static AnalyticsPage<T> Paginate<T>(IReadOnlyList<T> records, int? page, int? pageSize,
        AnalyticsOptions options, int limit)
    {
        var size = pageSize ?? options.PageSize;
        var bounded = records.Take(limit).ToArray();
        var lastPage = Math.Max(1, (bounded.Length + size - 1) / size);
        var current = Math.Min(page ?? 1, lastPage);
        return new(bounded.Skip((current - 1) * size).Take(size).ToArray(), current, size, bounded.Length, limit);
    }
}
