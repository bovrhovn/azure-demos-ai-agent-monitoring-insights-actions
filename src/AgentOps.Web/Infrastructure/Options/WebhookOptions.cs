using System.ComponentModel.DataAnnotations;

namespace AgentOps.Web.Infrastructure.Options;

public sealed class WebhookOptions
{
    public const string Section = "Webhook";
    public const string ApiKeyHeader = "X-Api-Key";
    [Required] public string ApiKey { get; set; } = "";
    [Range(1024, 524288)] public int MaxPayloadBytes { get; set; } = 262144;
}
