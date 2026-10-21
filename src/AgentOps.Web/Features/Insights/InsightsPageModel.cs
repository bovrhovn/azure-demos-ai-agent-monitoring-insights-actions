using Azure;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace AgentOps.Web.Features.Insights;

/// <summary>Shows explicit query failures instead of fabricated telemetry.</summary>
public abstract class InsightsPageModel(IInsightsReader reader, ILogger logger) : PageModel
{
    protected IInsightsReader Reader => reader;
    public string? QueryError { get; private set; }
    public bool Configured => reader.IsConfigured;

    protected async Task LoadAsync(Func<Task> query)
    {
        if (!Configured)
            return;
        try
        {
            await query();
        }
        catch (Exception exception) when (exception is RequestFailedException or InvalidOperationException)
        {
            logger.LogError(exception, "Application Insights query failed");
            QueryError = "Application Insights could not be queried. Check the resource ID, network access and Monitoring Reader role. See application logs for details.";
        }
    }
}
