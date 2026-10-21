using System.ComponentModel.DataAnnotations;

namespace AgentOps.Web.Infrastructure;

/// <summary>Cosmos persistence configuration; credentials are development-only.</summary>
public sealed class CosmosOptions
{
    public const string Section = "Cosmos";
    [Required] public string Database { get; set; } = "agentops";
    [Required] public string Container { get; set; } = "executions";
    public string Endpoint { get; set; } = "";
    public string ConnectionString { get; set; } = "";
    public bool InitializeDatabase { get; set; }
    public bool AllowInsecureEmulatorTls { get; set; }
}
