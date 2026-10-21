using AgentOps.Web.Features.Executions;
using AgentOps.Web.Infrastructure;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;

namespace AgentOps.Web.Pages.Analytics;

public sealed class ExecutionsModel(IExecutionStore store, IOptions<ExecutionOptions> options) : PageModel
{
    public int HistoryLimit => options.Value.HistoryLimit;
    public IReadOnlyList<ExecutionRecord> Executions { get; private set; } = [];

    public async Task OnGetAsync(CancellationToken cancellationToken) =>
        Executions = await store.ListAsync(ExecutionContract.Endpoint, options.Value.HistoryLimit, cancellationToken);
}
