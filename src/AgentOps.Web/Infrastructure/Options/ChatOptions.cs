using System.ComponentModel.DataAnnotations;

namespace AgentOps.Web.Infrastructure.Options;

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
        "Use the supplied read-only Application Insights tools when telemetry evidence is needed. " +
        "Only claim to have inspected telemetry returned by a successful tool call. " +
        "You cannot perform actions or changes. Use the supplied conversation history as context.";
}
