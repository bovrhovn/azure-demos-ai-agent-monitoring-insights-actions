using System.Security.Claims;
using AgentOps.Web.Infrastructure;
using AgentOps.Web.Infrastructure.Options;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Identity.Web;

namespace AgentOps.Web.Features.Identity;

/// <summary>Tenant authentication and least-privilege UI/API authorization.</summary>
public static class Policies
{
    public const string View = "View";
    public const string Operate = "Operate";
    public const string RunApi = "RunApi";

    public static IServiceCollection AddEntraIdentity(this IServiceCollection services, IConfiguration configuration)
    {
        var options = configuration.GetSection(IdentityOptions.Section).Get<IdentityOptions>() ?? new();
        var authentication = services.AddAuthentication(OpenIdConnectDefaults.AuthenticationScheme);
        authentication.AddMicrosoftIdentityWebApp(configuration.GetSection(IdentityOptions.Section));
        services.Configure<OpenIdConnectOptions>(OpenIdConnectDefaults.AuthenticationScheme, settings =>
        {
            settings.CallbackPath = configuration["AzureAd:CallbackPath"] ?? RouteConstants.SignInCallback;
            settings.SignedOutCallbackPath = configuration["AzureAd:SignedOutCallbackPath"] ?? RouteConstants.SignOutCallback;
        });
        authentication.AddMicrosoftIdentityWebApi(configuration.GetSection(IdentityOptions.Section));
        services.AddAuthorizationBuilder()
            .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build())
            .AddPolicy(View, policy => policy.RequireAuthenticatedUser()
                .RequireRole(options.ReaderRole, options.OperatorRole))
            .AddPolicy(Operate, policy => policy.RequireAuthenticatedUser().RequireRole(options.OperatorRole))
            .AddPolicy(RunApi, policy => policy
                .AddAuthenticationSchemes(JwtBearerDefaults.AuthenticationScheme)
                .RequireAuthenticatedUser()
                .RequireAssertion(context => CanRun(context.User, options)));
        return services;
    }

    public static bool CanRun(ClaimsPrincipal user, IdentityOptions options) =>
        user.IsInRole(options.OperatorRole) ||
        user.FindAll("scp").Concat(user.FindAll("http://schemas.microsoft.com/identity/claims/scope"))
            .SelectMany(claim => claim.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Contains(options.RunScope, StringComparer.Ordinal);
}
