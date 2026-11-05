using System.ComponentModel.DataAnnotations;

namespace AgentOps.Web.Infrastructure.Options;

public sealed class NotificationOptions
{
    public const string Section = "Notifications";
    [Required] public string Container { get; set; } = "notifications";
    [Range(1, 100)] public int PageSize { get; set; } = 20;
    [Range(1, 1000)] public int MaxSearchLength { get; set; } = 200;
    [Range(1, 10000)] public int MaxCommentLength { get; set; } = 2000;
    [Range(1, 2000)] public int MaxTestMessageLength { get; set; } = 500;
    [Range(1, 60)] public int TestRequestsPerMinute { get; set; } = 6;
    [Range(1, 60)] public int TestTimeoutSeconds { get; set; } = 10;
}
