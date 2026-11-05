using AgentOps.Web.Features.Executions;
using AgentOps.Web.Infrastructure;
using AgentOps.Web.Features.Scheduling;
using AgentOps.Web.Infrastructure.Options;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;
using Hangfire;
using Hangfire.Storage;

namespace AgentOps.Web.Pages.Analytics;

public sealed class SchedulesModel(JobStorage storage, ILogger<SchedulesModel> logger, IExecutionStore store,
    IOptions<ExecutionOptions> executions, IOptions<SchedulingOptions> options,
    IOptions<InsightsOptions> insights, IOptions<InsightsToolOptions> tools, IOptions<FoundryOptions> foundry) : PageModel
{
    public int HistoryLimit => executions.Value.HistoryLimit;
    public SchedulingOptions Settings => options.Value;
    public int LookbackMinutes => tools.Value.DefaultLookbackMinutes;
    public int EvidenceCount => tools.Value.DefaultCount;
    public bool InvestigationConfigured => tools.Value.Enabled && tools.Value.MaxToolCalls >= 2 &&
        !string.IsNullOrWhiteSpace(insights.Value.ResourceId) &&
        Uri.TryCreate(foundry.Value.ProjectEndpoint, UriKind.Absolute, out var endpoint) && endpoint.Scheme == "https" &&
        !string.IsNullOrWhiteSpace(foundry.Value.ModelDeployment);
    public RecurringJobDto? Schedule { get; private set; }
    public IReadOnlyList<ExecutionRecord> Executions { get; private set; } = [];

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        using var connection = storage.GetConnection();
        Schedule = connection.GetRecurringJobs().SingleOrDefault(job => job.Id == Settings.RecurringJobId);
        Executions = await store.ListAsync(ExecutionContract.Schedule, executions.Value.HistoryLimit, cancellationToken);
        logger.LogInformation(
            "Schedules page loaded for {RecurringJobId}; enabled {Enabled}, registered {ScheduleFound}, {ExecutionCount} executions with limit {HistoryLimit}",
            Settings.RecurringJobId, Settings.Enabled, Schedule is not null, Executions.Count, HistoryLimit);
    }
}
