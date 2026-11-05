using AgentOps.Web.Features.Identity;
using AgentOps.Web.Infrastructure;
using AgentOps.Web.Infrastructure.Options;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;

namespace AgentOps.Web.Pages.Chat;

public sealed class IndexModel(IAntiforgery antiforgery, IAuthorizationService authorization, IOptions<ChatOptions> options) : PageModel
{
    public string AntiforgeryToken { get; private set; } = "";
    public bool CanSend { get; private set; }
    public string? InitialSessionId { get; private set; }
    public int MaxPromptLength => options.Value.MaxPromptLength;
    public int HistoryLimit => options.Value.HistoryLimit;

    public async Task OnGetAsync(string? sessionId)
    {
        InitialSessionId = sessionId;
        AntiforgeryToken = antiforgery.GetAndStoreTokens(HttpContext).RequestToken!;
        CanSend = (await authorization.AuthorizeAsync(User, Policies.Operate)).Succeeded;
    }
}
