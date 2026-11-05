using System.ComponentModel.DataAnnotations;

namespace AgentOps.Web.Infrastructure.Options;

public sealed class WebhookOptions
{
    public const string Section = "Webhook";
    public const string ApiKeyHeader = "X-Api-Key";
    [Required] public string ApiKey { get; set; } = "";
}
