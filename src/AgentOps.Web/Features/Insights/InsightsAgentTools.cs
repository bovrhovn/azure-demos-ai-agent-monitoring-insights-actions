using System.ComponentModel;
using System.Text.Json;
using AgentOps.Web.Infrastructure;
using Azure;
using Microsoft.Extensions.AI;

namespace AgentOps.Web.Features.Insights;

public sealed record TelemetryEvidence(string Reference, string ResourceId, string Type, string ItemId,
    string OperationId, DateTimeOffset Timestamp, DateTimeOffset Start, DateTimeOffset End);

public sealed record TelemetryToolRow(int Number, string Reference, DateTimeOffset Timestamp, string Type,
    string Service, string Severity, string Summary, string Details);

public sealed record TelemetryToolResult(string Status, string ResourceId, DateTimeOffset Start, DateTimeOffset End,
    IReadOnlyList<TelemetryToolRow> Rows, bool Truncated = false, string? Error = null);

public sealed class InsightsAgentTools(ITelemetryQuery query, InsightsOptions insights, InsightsToolOptions options,
    TimeProvider time, IReadOnlyList<TelemetryEvidence> previousEvidence, ILogger logger)
{
    private readonly Dictionary<string, TelemetryEvidence> evidence = previousEvidence
        .Where(item => item.ResourceId == insights.ResourceId).TakeLast(options.MaxEvidenceReferences)
        .DistinctBy(item => item.Reference).ToDictionary(item => item.Reference);
    private readonly List<TelemetryEvidence> usedEvidence = [];
    private int calls;

    public IReadOnlyList<TelemetryEvidence> Evidence => usedEvidence.ToArray();

    public IList<AITool> CreateTools() => options.Enabled ?
    [
        AIFunctionFactory.Create(GetRecentExceptions),
        AIFunctionFactory.Create(GetExceptionDetails),
        AIFunctionFactory.Create(GetRecentTraces),
        AIFunctionFactory.Create(GetOperationTimeline)
    ] : [];

    [Description("Query recent Application Insights exceptions, newest first. Use returned reference IDs for drill-down. Lookback is in minutes; count and service are optional.")]
    public Task<TelemetryToolResult> GetRecentExceptions(int? lookbackMinutes = null, int? count = null,
        string? service = null, CancellationToken cancellationToken = default) =>
        RecentAsync(TelemetryQueryKind.Exceptions, lookbackMinutes, count, service, null, cancellationToken);

    [Description("Retrieve full exception details and stack frames using an exact reference from previous tool results in this conversation. Never invent references.")]
    public Task<TelemetryToolResult> GetExceptionDetails(string reference, CancellationToken cancellationToken = default) =>
        ReferencedAsync(reference, TelemetryQueryKind.ExceptionDetails, null, cancellationToken);

    [Description("Query recent Application Insights traces, newest first. Lookback is in minutes. Minimum severity is optional: 0 verbose, 1 information, 2 warning, 3 error, 4 critical.")]
    public Task<TelemetryToolResult> GetRecentTraces(int? lookbackMinutes = null, int? count = null,
        string? service = null, int? minimumSeverity = null, CancellationToken cancellationToken = default) =>
        RecentAsync(TelemetryQueryKind.Traces, lookbackMinutes, count, service, minimumSeverity, cancellationToken);

    [Description("Correlate requests, dependencies, exceptions and traces for a returned reference's operation in its original query window, oldest first. Count is optional.")]
    public Task<TelemetryToolResult> GetOperationTimeline(string reference, int? count = null,
        CancellationToken cancellationToken = default) =>
        ReferencedAsync(reference, TelemetryQueryKind.Operation, count, cancellationToken);

    private Task<TelemetryToolResult> RecentAsync(TelemetryQueryKind kind, int? lookbackMinutes, int? count,
        string? service, int? severity, CancellationToken cancellationToken)
    {
        var end = time.GetUtcNow();
        var minutes = lookbackMinutes ?? options.DefaultLookbackMinutes;
        if (minutes < 1 || minutes > options.MaxLookbackMinutes)
            return Task.FromResult(Failure("invalid_arguments", $"Lookback must be 1..{options.MaxLookbackMinutes} minutes."));
        return ExecuteAsync(new(kind, end.AddMinutes(-minutes), end, count ?? options.DefaultCount,
            Service: service, MinimumSeverity: severity), cancellationToken);
    }

    private Task<TelemetryToolResult> ReferencedAsync(string reference, TelemetryQueryKind kind, int? count,
        CancellationToken cancellationToken)
    {
        if (!evidence.TryGetValue(reference, out var item))
            return Task.FromResult(Failure("unknown_reference", "That reference is not available in this conversation. Ask which earlier result is intended or list telemetry again; do not guess."));
        if (kind == TelemetryQueryKind.ExceptionDetails && (item.Type != "exception" || string.IsNullOrWhiteSpace(item.ItemId)) ||
            kind == TelemetryQueryKind.Operation && string.IsNullOrWhiteSpace(item.OperationId))
            return Task.FromResult(Failure("invalid_reference", "The referenced record does not have the identifier required for this query."));
        return ExecuteAsync(new(kind, item.Start, item.End, kind == TelemetryQueryKind.ExceptionDetails ? 1 : count ?? options.MaxRows,
            ItemId: item.ItemId, OperationId: item.OperationId), cancellationToken);
    }

    private async Task<TelemetryToolResult> ExecuteAsync(TelemetryQueryRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Interlocked.Increment(ref calls) > options.MaxToolCalls)
            return Failure("call_limit", "The per-turn telemetry tool-call limit was reached. Report this limit; do not claim further queries succeeded.");
        if (!options.Enabled || string.IsNullOrWhiteSpace(insights.ResourceId))
            return Failure("not_configured", "Application Insights querying is unavailable. Configure Insights:ResourceId and grant the application identity Monitoring Reader.");
        if (request.Count < 1 || request.Count > options.MaxRows || request.MinimumSeverity is < 0 or > 4 ||
            request.Service is { Length: > 256 } || request.Service?.Any(char.IsControl) == true)
            return Failure("invalid_arguments", $"Count must be 1..{options.MaxRows}, severity 0..4, and service at most 256 characters without control characters.");

        try
        {
            var result = await query.QueryAsync(request, cancellationToken);
            List<TelemetryToolRow> rows = [];
            var truncated = result.Truncated;
            foreach (var row in result.Rows.Take(request.Count))
            {
                var existing = evidence.Values.FirstOrDefault(item => item.ResourceId == insights.ResourceId &&
                    item.Type == row.Type && item.ItemId == row.ItemId && item.Timestamp == row.Timestamp);
                var reference = existing ?? new TelemetryEvidence(Guid.NewGuid().ToString("N"), insights.ResourceId,
                    row.Type, row.ItemId, row.OperationId, row.Timestamp, request.Start, request.End);
                var fields = new[] { row.Service, row.Severity, row.Summary, row.Details };
                var sanitized = fields.Select(TelemetryText.Sanitize).ToArray();
                truncated |= sanitized.Any(field => field.Length > options.MaxFieldCharacters);
                var output = sanitized.Select(field => field[..Math.Min(field.Length, options.MaxFieldCharacters)]).ToArray();
                rows.Add(new(rows.Count + 1, reference.Reference, row.Timestamp, row.Type, output[0], output[1], output[2], output[3]));
                if (JsonSerializer.Serialize(new TelemetryToolResult("success", insights.ResourceId, request.Start, request.End, rows)).Length > options.MaxResultCharacters ||
                    !usedEvidence.Contains(reference) && usedEvidence.Count >= options.MaxEvidenceReferences)
                {
                    rows.RemoveAt(rows.Count - 1);
                    truncated = true;
                    break;
                }
                evidence[reference.Reference] = reference;
                if (!usedEvidence.Contains(reference)) usedEvidence.Add(reference);
            }
            logger.LogInformation("Telemetry tool {QueryKind} returned {RowCount} rows; truncated: {Truncated}",
                request.Kind, rows.Count, truncated);
            return new("success", insights.ResourceId, request.Start, request.End, rows, truncated);
        }
        catch (RequestFailedException exception)
        {
            logger.LogWarning("Telemetry tool {QueryKind} failed with Azure status {Status} and code {ErrorCode}",
                request.Kind, exception.Status, exception.ErrorCode);
            return Failure(exception.Status is 401 or 403 ? "access_denied" : "query_failed",
                "Application Insights could not be queried. Check the application identity's read permission and Azure Monitor connectivity.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Failure("timeout", "Application Insights query timed out. No complete result is available.");
        }
        catch (TelemetryQueryException)
        {
            return Failure("query_failed", "Application Insights returned an incomplete or unavailable result. Do not interpret this as an empty dataset.");
        }
    }

    private TelemetryToolResult Failure(string status, string message)
    {
        logger.LogWarning("Telemetry tool rejected or failed with status {ToolStatus}", status);
        return new(status, insights.ResourceId, default, default, [], Error: message);
    }
}
