using Microsoft.AspNetCore.Mvc.RazorPages;

namespace AgentOps.Web.Pages.Analytics;

public sealed class LogsModel(ILogger<LogsModel> logger) : PageModel
{
    public void OnGet() => logger.LogInformation("Application logs viewer loaded");
}
