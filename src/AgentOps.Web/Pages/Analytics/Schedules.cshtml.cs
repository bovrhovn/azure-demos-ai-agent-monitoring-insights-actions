using AgentOps.Web.Features.Executions;
using AgentOps.Web.Infrastructure;
using AgentOps.Web.Features.Scheduling;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;
using Hangfire;
using Hangfire.Storage;

namespace AgentOps.Web.Pages.Analytics;

public sealed class SchedulesModel(JobStorage storage, IExecutionStore store,
    IOptions<ExecutionOptions> executions, IOptions<SchedulingOptions> options) : PageModel
{
    public int HistoryLimit => executions.Value.HistoryLimit;
    public SchedulingOptions Settings => options.Value;
    public RecurringJobDto? Schedule { get; private set; }
    public IReadOnlyList<ExecutionRecord> Executions { get; private set; } = [];

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        using var connection = storage.GetConnection();
        Schedule = connection.GetRecurringJobs().SingleOrDefault(job => job.Id == Settings.RecurringJobId);
        Executions = await store.ListAsync(ExecutionContract.Schedule, executions.Value.HistoryLimit, cancellationToken);
    }
}
