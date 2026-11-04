using System.ComponentModel.DataAnnotations;

namespace AgentOps.Web.Infrastructure;

public sealed class ChatOptions
{
    public const string Section = "Chat";
    [Required] public string Container { get; set; } = "chats";
    [Range(1, 100)] public int SessionPageSize { get; set; } = 30;
    [Range(1, 100)] public int MessagePageSize { get; set; } = 40;
    [Range(1, 100)] public int HistoryLimit { get; set; } = 40;
    [Range(1, 32000)] public int MaxPromptLength { get; set; } = 8000;
    [Range(1, 16384)] public int MaxOutputTokens { get; set; } = 2048;
    [Range(1, 120)] public int RequestsPerMinute { get; set; } = 20;
    [Range(1, 300)] public int TimeoutSeconds { get; set; } = 120;
    [Range(1, 60)] public int FinalizationTimeoutSeconds { get; set; } = 15;
    [Required] public string Instructions { get; set; } =
        "You are the Agent Observatory conversational assistant. Answer clearly and accurately. " +
        "You have no tools or access to application telemetry, secrets, or cloud resources. " +
        "Never claim to have inspected resources or performed actions. Use the supplied conversation history as context.";
}
