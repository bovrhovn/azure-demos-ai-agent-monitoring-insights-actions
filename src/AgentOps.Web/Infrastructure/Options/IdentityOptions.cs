using System.ComponentModel.DataAnnotations;

namespace AgentOps.Web.Infrastructure;

/// <summary>Entra registration and application authorization configuration.</summary>
public sealed class IdentityOptions
{
    public const string Section = "AzureAd";
    [Required] public string TenantId { get; set; } = "";
    [Required] public string ClientId { get; set; } = "";
    [Required] public string Instance { get; set; } = "https://login.microsoftonline.com/";
    [Required] public string OperatorRole { get; set; } = "AgentOps.Operator";
    [Required] public string ReaderRole { get; set; } = "AgentOps.Reader";
    [Required] public string RunScope { get; set; } = "Agent.Run";
}
