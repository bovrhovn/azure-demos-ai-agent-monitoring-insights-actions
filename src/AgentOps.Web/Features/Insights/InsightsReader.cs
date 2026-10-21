using System.Globalization;
using AgentOps.Web.Infrastructure;
using Azure.Core;
using Azure.Monitor.Query.Logs;
using Azure.Monitor.Query.Logs.Models;
using Microsoft.Extensions.Options;

namespace AgentOps.Web.Features.Insights;

/// <summary>Dashboard aggregate; an empty dataset is not a disconnected service.</summary>
public sealed record InsightsOverview(long Requests, long FailedRequests, double AverageDurationMs, long Exceptions);
public sealed record InsightLog(DateTimeOffset Timestamp, string Severity, string Message, string OperationId);

public interface IInsightsReader
{
    bool IsConfigured { get; }
    Task<InsightsOverview> OverviewAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<InsightLog>> LogsAsync(CancellationToken cancellationToken);
}

/// <summary>Queries a configured Application Insights resource using Entra credentials.</summary>
public sealed class InsightsReader(LogsQueryClient client, IOptions<InsightsOptions> options) : IInsightsReader
{
    public bool IsConfigured => !string.IsNullOrWhiteSpace(options.Value.ResourceId);

    public async Task<InsightsOverview> OverviewAsync(CancellationToken cancellationToken)
    {
        var result = await QueryAsync("""
            let req = requests | summarize Requests=count(), FailedRequests=countif(success == false), AverageDurationMs=avg(duration);
            let exc = exceptions | summarize Exceptions=count();
            req | extend JoinKey=1 | join kind=inner (exc | extend JoinKey=1) on JoinKey
            | project Requests, FailedRequests, AverageDurationMs, Exceptions
            """, cancellationToken);
        var row = result.Table.Rows.Single();
        return new(Convert.ToInt64(row[0], CultureInfo.InvariantCulture),
            Convert.ToInt64(row[1], CultureInfo.InvariantCulture),
            row[2] is null ? 0 : Convert.ToDouble(row[2], CultureInfo.InvariantCulture),
            Convert.ToInt64(row[3], CultureInfo.InvariantCulture));
    }

    public async Task<IReadOnlyList<InsightLog>> LogsAsync(CancellationToken cancellationToken)
    {
        var query = "traces | top " + options.Value.LogLimit.ToString(CultureInfo.InvariantCulture) +
            " by timestamp desc | project timestamp, severityLevel, message, operation_Id";
        var result = await QueryAsync(query, cancellationToken);
        return result.Table.Rows.Select(row => new InsightLog(row.GetDateTimeOffset(0)!.Value,
            row[1]?.ToString() ?? "0", row[2]?.ToString() ?? "", row[3]?.ToString() ?? "")).ToArray();
    }

    private async Task<LogsQueryResult> QueryAsync(string query, CancellationToken cancellationToken)
    {
        if (!IsConfigured)
            throw new InvalidOperationException("Configure Insights:ResourceId to query Application Insights.");
        var response = await client.QueryResourceAsync(new ResourceIdentifier(options.Value.ResourceId), query,
            new LogsQueryTimeRange(TimeSpan.FromMinutes(options.Value.LookbackMinutes)), cancellationToken: cancellationToken);
        if (response.Value.Status != LogsQueryResultStatus.Success)
            throw new InvalidOperationException("Application Insights returned partial query results; no incomplete metrics will be displayed.");
        return response.Value;
    }
}
