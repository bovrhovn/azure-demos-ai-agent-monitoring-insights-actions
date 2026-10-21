using AgentOps.Web;
using AgentOps.Web.Features.Executions;
using AgentOps.Web.Features.Identity;
using AgentOps.Web.Features.Insights;
using AgentOps.Web.Features.Webhooks;
using AgentOps.Web.Infrastructure;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddAgentOps(builder.Configuration, builder.Environment);

var app = builder.Build();

app.UseForwardedHeaders();
app.UseExceptionHandler();
if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
    app.UseWhen(context => context.Request.Path != RouteConstants.Health &&
        context.Request.Path != RouteConstants.Liveness &&
        context.Request.Path != RouteConstants.Readiness,
        branch => branch.UseHttpsRedirection());
}
app.UseStaticFiles();
app.UseRouting();
app.UseAuthentication();
app.UseExecutionHubOriginGuard();
app.UseAuthorization();
app.UseRateLimiter();
app.UseAntiforgery();
app.MapRazorPages();
app.MapControllers();
app.MapExecutionEndpoints();
app.MapAnalyticsEndpoints();
app.MapEventGridWebhook();
app.MapHub<ExecutionHub>(RouteConstants.ExecutionHub).RequireAuthorization(Policies.View);
app.MapHealthChecks(RouteConstants.Health).AllowAnonymous();
app.MapHealthChecks(RouteConstants.Liveness, new() { Predicate = _ => false }).AllowAnonymous();
app.MapHealthChecks(RouteConstants.Readiness).AllowAnonymous();
await app.RunAsync();

/// <summary>Application entry point exposed for integration testing.</summary>
public partial class Program;
