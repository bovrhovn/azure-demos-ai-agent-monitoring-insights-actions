namespace AgentOps.Web.Features.Insights;

public interface IInsightsReader
{
    bool IsConfigured { get; }
    Task<InsightsOverview> OverviewAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<InsightLog>> LogsAsync(CancellationToken cancellationToken);
}
