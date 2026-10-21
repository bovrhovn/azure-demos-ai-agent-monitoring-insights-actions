using AgentOps.Web.Infrastructure;
using FluentAssertions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Moq;

namespace AgentOps.Tests;

[TestClass]
public sealed class DataSourceTests
{
    [TestMethod]
    [DataRow("Development", "Cosmos development", "Local emulator")]
    [DataRow("Production", "Cosmos production", "observatory-prod")]
    [DataRow("Staging", "Cosmos staging", "observatory-prod")]
    public void CosmosSourceUsesActualEnvironmentAndPublicAccountMetadata(string environmentName, string label, string serviceName)
    {
        var environment = new Mock<IHostEnvironment>();
        environment.SetupGet(x => x.EnvironmentName).Returns(environmentName);
        var source = new CosmosDataSource(environment.Object, Options.Create(new CosmosOptions
        {
            Endpoint = environmentName == Environments.Development ? "" : "https://observatory-prod.documents.azure.com/",
            Database = "monitoring", Container = "runs"
        }));
        source.Label.Should().Be(label);
        source.ServiceName.Should().Be(serviceName);
        source.Database.Should().Be("monitoring");
        source.Container.Should().Be("runs");
    }

    [TestMethod]
    [DataRow("Development", "Cosmos development", "Local emulator")]
    [DataRow("Production", "Cosmos production", "observatory-prod")]
    [DataRow("Staging", "Cosmos staging", "observatory-prod")]
    public async Task FooterRendersConfiguredSourceWithoutCredentials(string environmentName, string label, string serviceName)
    {
        var settings = new Dictionary<string, string?>
        {
            ["Cosmos:Endpoint"] = "https://observatory-prod.documents.azure.com/",
            ["Cosmos:Database"] = "monitoring", ["Cosmos:Container"] = "runs",
            ["Cosmos:InitializeDatabase"] = "false", ["Cosmos:AllowInsecureEmulatorTls"] = "false"
        };
        if (environmentName != Environments.Development) settings["Cosmos:ConnectionString"] = "";
        await using var factory = new AppFactory(settings, environmentName);
        using var client = factory.Client("AgentOps.Reader");
        var html = await client.GetStringAsync("/");
        html.Should().Contain($"data-environment=\"{environmentName}\"")
            .And.Contain(label).And.Contain(serviceName).And.Contain("monitoring").And.Contain("runs")
            .And.Contain("aria-label=\"Execution data source\"").And.Contain("data-source-icon")
            .And.NotContain("AccountEndpoint=").And.NotContain("AccountKey=").And.NotContain("dGVzdA==")
            .And.NotContain("test-only-webhook-key");
    }
}
