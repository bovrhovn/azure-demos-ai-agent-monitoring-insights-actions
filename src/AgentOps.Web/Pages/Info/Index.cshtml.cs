using AgentOps.Web.Features.Insights;
using AgentOps.Web.Infrastructure;
using Microsoft.Extensions.Options;

namespace AgentOps.Web.Pages.Info;

public sealed class IndexModel(IInsightsReader reader, IOptions<InsightsOptions> settings, ILogger<IndexModel> logger)
    : InsightsPageModel(reader, logger)
{
    public InsightsOverview? Overview { get; private set; }
    public int LookbackMinutes => settings.Value.LookbackMinutes;
    public Task OnGetAsync(CancellationToken cancellationToken) =>
        LoadAsync(async () => Overview = await Reader.OverviewAsync(cancellationToken));
}
