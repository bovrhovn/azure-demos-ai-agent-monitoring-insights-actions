using System.Globalization;

namespace AgentOps.Web.Features.Insights;

internal static class InsightsQueries
{
    public const string Overview = """
        let req = requests | summarize Requests=count(), FailedRequests=countif(success == false), AverageDurationMs=avg(duration);
        let exc = exceptions | summarize Exceptions=count();
        req | extend JoinKey=1 | join kind=inner (exc | extend JoinKey=1) on JoinKey
        | project Requests, FailedRequests, AverageDurationMs, Exceptions
        """;

    public static string Logs(int limit) =>
        "traces | top " + limit.ToString(CultureInfo.InvariantCulture) +
        " by timestamp desc | project timestamp, severityLevel, message, operation_Id";
}
