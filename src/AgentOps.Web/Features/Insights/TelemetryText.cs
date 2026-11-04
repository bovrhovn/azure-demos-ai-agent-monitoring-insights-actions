using System.Text.RegularExpressions;

namespace AgentOps.Web.Features.Insights;

internal static partial class TelemetryText
{
    public static string Sanitize(string value)
    {
        value = ConnectionString().Replace(value, "[REDACTED CONNECTION STRING]");
        value = Secret().Replace(value, "$1=[REDACTED]");
        value = Bearer().Replace(value, "Bearer [REDACTED]");
        value = Jwt().Replace(value, "[REDACTED TOKEN]");
        value = Email().Replace(value, "[REDACTED EMAIL]");
        return UrlQuery().Replace(value, "$1?[REDACTED]");
    }

    [GeneratedRegex(@"(?i)(?:InstrumentationKey|AccountEndpoint|Server|Data Source|Host)\s*=\s*[^;\r\n""<>]+(?:;[^;\r\n""<>]*=[^;\r\n""<>]*)*;?", RegexOptions.NonBacktracking)]
    private static partial Regex ConnectionString();
    [GeneratedRegex(@"(?i)\b(password|pwd|accountkey|apikey|api[_-]key|client[_-]?secret|access[_-]?token|authorization|sig)\b[""']?\s*[:=]\s*[""']?[^""'\s,;}\]]+", RegexOptions.NonBacktracking)]
    private static partial Regex Secret();
    [GeneratedRegex(@"(?i)\bBearer\s+[A-Za-z0-9._~+/\-=]+", RegexOptions.NonBacktracking)]
    private static partial Regex Bearer();
    [GeneratedRegex(@"\beyJ[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+", RegexOptions.NonBacktracking)]
    private static partial Regex Jwt();
    [GeneratedRegex(@"\b[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}\b", RegexOptions.NonBacktracking)]
    private static partial Regex Email();
    [GeneratedRegex(@"(https?://[^\s?""'<>]+)\?[^\s""'<>]+", RegexOptions.NonBacktracking)]
    private static partial Regex UrlQuery();
}
