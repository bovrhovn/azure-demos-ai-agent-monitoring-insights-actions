using AgentOps.Web.Features.Identity;
using AgentOps.Web.Infrastructure.Options;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;

namespace AgentOps.Web.Pages.Notifications;

public sealed class IndexModel(IAntiforgery antiforgery, IAuthorizationService authorization,
    IOptions<NotificationOptions> options) : PageModel
{
    public string AntiforgeryToken { get; private set; } = "";
    public bool CanOperate { get; private set; }
    public int MaxSearchLength => options.Value.MaxSearchLength;
    public int MaxCommentLength => options.Value.MaxCommentLength;

    public async Task OnGetAsync()
    {
        AntiforgeryToken = antiforgery.GetAndStoreTokens(HttpContext).RequestToken!;
        CanOperate = (await authorization.AuthorizeAsync(User, Policies.Operate)).Succeeded;
    }
}
