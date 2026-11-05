using System.Globalization;
using AgentOps.Web.Infrastructure;
using AgentOps.Web.Infrastructure.Options;
using Azure.Core;
using Azure.Monitor.Query.Logs;
using Azure.Monitor.Query.Logs.Models;
using Microsoft.Extensions.Options;

namespace AgentOps.Web.Features.Insights;

public enum TelemetryQueryKind { Exceptions, ExceptionDetails, Traces, Operation }

public sealed record TelemetryQueryRequest(TelemetryQueryKind Kind, DateTimeOffset Start, DateTimeOffset End,
    int Count, string? ItemId = null, string? OperationId = null, string? Service = null, int? MinimumSeverity = null);

public sealed record TelemetryRow(DateTimeOffset Timestamp, string Type, string ItemId, string OperationId,
    string Service, string Severity, string Summary, string Details);

public sealed record TelemetryQueryResult(IReadOnlyList<TelemetryRow> Rows, bool Truncated);

public sealed class TelemetryQueryException(string message) : Exception(message);

public interface ITelemetryQuery
{
    Task<TelemetryQueryResult> QueryAsync(TelemetryQueryRequest request, CancellationToken cancellationToken);
}

public sealed class TelemetryQuery(LogsQueryClient client, IOptions<InsightsOptions> insights,
    IOptions<InsightsToolOptions> options) : ITelemetryQuery
{
    public async Task<TelemetryQueryResult> QueryAsync(TelemetryQueryRequest request, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        if (!settings.Enabled || string.IsNullOrWhiteSpace(insights.Value.ResourceId))
            throw new TelemetryQueryException("Application Insights tools are not configured.");
        if (request.Count < 1 || request.Count > settings.MaxRows || request.End <= request.Start ||
            request.End - request.Start > TimeSpan.FromMinutes(settings.MaxLookbackMinutes))
            throw new ArgumentOutOfRangeException(nameof(request), "Telemetry query exceeds the configured bounds.");
        if (request.MinimumSeverity is < 0 or > 4)
            throw new ArgumentOutOfRangeException(nameof(request), "Severity must be between 0 and 4.");
        if (request.Kind == TelemetryQueryKind.ExceptionDetails && string.IsNullOrWhiteSpace(request.ItemId) ||
            request.Kind == TelemetryQueryKind.Operation && string.IsNullOrWhiteSpace(request.OperationId))
            throw new ArgumentException("A telemetry identifier is required.", nameof(request));

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(settings.QueryTimeoutSeconds));
        var response = await client.QueryResourceAsync(new ResourceIdentifier(insights.Value.ResourceId),
            BuildQuery(request), new LogsQueryTimeRange(request.Start, request.End),
            new LogsQueryOptions { ServerTimeout = TimeSpan.FromSeconds(settings.QueryTimeoutSeconds) }, timeout.Token);
        if (response.Value.Status != LogsQueryResultStatus.Success || response.Value.Error?.Code is { Length: > 0 } ||
            response.Value.AllTables.Count != 1)
            throw new TelemetryQueryException("Application Insights returned partial results; the query is incomplete.");
        var rows = response.Value.Table.Rows;
        return new(rows.Take(request.Count).Select(row => new TelemetryRow(
            row.GetDateTimeOffset(0)!.Value, Text(row[1]), Text(row[2]), Text(row[3]),
            Text(row[4]), Text(row[5]), Text(row[6]), Text(row[7]))).ToArray(), rows.Count > request.Count);
    }

    private static string Text(object? value) => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";

    private static string BuildQuery(TelemetryQueryRequest request)
    {
        const string exceptionSummary = "strcat(type, ': ', coalesce(outerMessage, innermostMessage, message))";
        var source = request.Kind switch
        {
            TelemetryQueryKind.Exceptions => $"exceptions | extend telemetryType='exception', summary={exceptionSummary}, detail=''",
            TelemetryQueryKind.ExceptionDetails => $"exceptions | where itemId == {Literal(request.ItemId!)} | extend telemetryType='exception', summary={exceptionSummary}, detail=tostring(details)",
            TelemetryQueryKind.Traces => "traces | extend telemetryType='trace', summary=message, detail=''",
            TelemetryQueryKind.Operation => $"""
                union
                (requests | extend telemetryType='request', summary=name, detail=strcat('ResultCode=', resultCode, ', DurationMs=', duration, ', Success=', success)),
                (dependencies | extend telemetryType='dependency', summary=name, detail=strcat('Type=', type, ', ResultCode=', resultCode, ', DurationMs=', duration, ', Success=', success)),
                (exceptions | extend telemetryType='exception', summary={exceptionSummary}, detail=tostring(details)),
                (traces | extend telemetryType='trace', summary=message, detail='')
                | where operation_Id == {Literal(request.OperationId!)}
                """,
            _ => throw new ArgumentOutOfRangeException(nameof(request))
        };
        var query = source + "\n| where timestamp between (datetime(" +
            request.Start.UtcDateTime.ToString("O", CultureInfo.InvariantCulture) + ") .. datetime(" +
            request.End.UtcDateTime.ToString("O", CultureInfo.InvariantCulture) + "))";
        if (!string.IsNullOrWhiteSpace(request.Service))
            query += "\n| where cloud_RoleName == " + Literal(request.Service);
        if (request.MinimumSeverity is not null)
            query += "\n| where severityLevel >= " + request.MinimumSeverity.Value.ToString(CultureInfo.InvariantCulture);
        return query + "\n| top " + (request.Count + 1).ToString(CultureInfo.InvariantCulture) +
            " by timestamp " + (request.Kind == TelemetryQueryKind.Operation ? "asc" : "desc") +
            "\n| project timestamp, telemetryType, itemId, operation_Id, cloud_RoleName, severity=tostring(severityLevel), summary, detail";
    }

    private static string Literal(string value)
    {
        if (value.Length > 256 || value.Any(char.IsControl))
            throw new ArgumentException("Telemetry filters must be at most 256 characters without control characters.");
        return "'" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("'", "\\'", StringComparison.Ordinal) + "'";
    }
}
