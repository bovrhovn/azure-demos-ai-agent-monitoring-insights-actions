using AgentOps.Web.Infrastructure.Options;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Options;

namespace AgentOps.Web.Features.Notifications;

public sealed class LocalWebhookDelivery(HttpClient client, IServer server, IOptions<WebhookOptions> webhook,
    IOptions<HostingOptions> hosting, IConfiguration configuration)
{
    public async Task SendAsync(byte[] body, CancellationToken cancellationToken, HttpRequest? original = null)
    {
        var address = server.Features.Get<IServerAddressesFeature>()?.Addresses
            .Select(value => Uri.TryCreate(value, UriKind.Absolute, out var uri) ? uri : null)
            .OfType<Uri>().Where(uri => uri.Scheme is "http" or "https")
            .OrderBy(uri => uri.Scheme == "https" ? 0 : 1).FirstOrDefault();
        if (address is null)
            throw new NotificationTestUnavailableException("Local webhook delivery requires a listening HTTP or HTTPS application endpoint.");
        if (body.Length > webhook.Value.MaxPayloadBytes)
            throw new InvalidOperationException("The event exceeds Webhook:MaxPayloadBytes.");

        var host = original?.Host.Value ?? configuration["AllowedHosts"]?
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault(value => !value.Contains('*'));
        if (string.IsNullOrWhiteSpace(host))
            throw new NotificationTestUnavailableException("Background webhook delivery requires a concrete host in AllowedHosts.");
        // Only the Host header varies. Credentials always travel to this process's loopback listener.
        var uri = new UriBuilder(address)
        {
            Host = "localhost",
            Path = original?.PathBase.Add(RouteConstants.EventGridWebhook).Value ?? RouteConstants.EventGridWebhook,
            Query = "",
            Fragment = ""
        }.Uri;
        using var request = new HttpRequestMessage(HttpMethod.Post, uri);
        request.Headers.Host = host;
        request.Headers.Add(WebhookOptions.ApiKeyHeader, webhook.Value.ApiKey);
        request.Headers.Add("X-Forwarded-Proto", original?.Scheme ??
            (hosting.Value.BehindContainerAppsIngress ? "https" : address.Scheme));
        request.Headers.Add("aeg-event-type", "Notification");
        request.Content = new ByteArrayContent(body);
        request.Content.Headers.ContentType = new("application/json");
        using var response = await client.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
    }
}
