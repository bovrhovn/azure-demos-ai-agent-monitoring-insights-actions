using System.ComponentModel.DataAnnotations;

namespace AgentOps.Web.Infrastructure.Options;

/// <summary>Trust boundaries for ingress and explicitly configured reverse proxies.</summary>
public sealed class HostingOptions
{
    public const string Section = "Hosting";
    public bool BehindContainerAppsIngress { get; set; }
    [Range(1, 10)] public int ForwardLimit { get; set; } = 1;
    public string[] KnownProxies { get; set; } = [];
    public string[] KnownNetworks { get; set; } = [];
}
