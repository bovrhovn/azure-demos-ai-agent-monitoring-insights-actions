using System.Security.Claims;
using AgentOps.Web.Features.Agent;
using AgentOps.Web.Features.Chat;
using AgentOps.Web.Features.Identity;
using AgentOps.Web.Infrastructure;
using AgentOps.Web.Features.Executions;
using AgentOps.Web.Features.Scheduling;
using AgentOps.Web.Infrastructure.Options;
using Azure.Core;
using Azure.Identity;
using FluentAssertions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Hangfire;
using Hangfire.InMemory;

namespace AgentOps.Tests;

[TestClass]
public sealed class ConfigurationTests
{
    [TestMethod]
    [DataRow("Development", null)]
    [DataRow("Staging", null)]
    [DataRow("Production", null)]
    [DataRow("Production", "33333333-3333-3333-3333-333333333333")]
    public void AzureSdkCompositionUsesDefaultCredentialsLocallyAndOnlyManagedIdentityInProduction(string environmentName, string? clientId)
    {
        var environment = new Mock<IHostEnvironment>();
        environment.SetupGet(item => item.EnvironmentName).Returns(environmentName);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["AZURE_CLIENT_ID"] = clientId
        }).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddAgentOpsServices(environment.Object);
        using var provider = services.BuildServiceProvider();
        var credential = provider.GetRequiredService<TokenCredential>();
        if (environmentName == Environments.Production) credential.Should().BeOfType<ManagedIdentityCredential>();
        else credential.Should().BeOfType<DefaultAzureCredential>();
        provider.GetRequiredService<TokenCredential>().Should().BeSameAs(credential);
        provider.GetRequiredService<IChatAgent>().Should().BeOfType<BasicFoundryAgentRunner>();
        provider.GetRequiredService<IAgentRunner>().Should().BeOfType<PendingAgentRunner>(
            "automated actions remain disabled; the conversational runner only serves chat");
    }

    [TestMethod]
    public void EntraLoginBindsManagedIdentityFederationWithoutClientSecret()
    {
        const string loginClientId = "22222222-2222-2222-2222-222222222222";
        const string managedIdentityClientId = "33333333-3333-3333-3333-333333333333";
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["AzureAd:Instance"] = "https://login.microsoftonline.com/",
            ["AzureAd:TenantId"] = "11111111-1111-1111-1111-111111111111",
            ["AzureAd:ClientId"] = loginClientId,
            ["AzureAd:ClientCredentials:0:SourceType"] = "SignedAssertionFromManagedIdentity",
            ["AzureAd:ClientCredentials:0:ManagedIdentityClientId"] = managedIdentityClientId
        }).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddEntraIdentity(configuration);
        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptionsMonitor<Microsoft.Identity.Web.MicrosoftIdentityOptions>>()
            .Get(Microsoft.AspNetCore.Authentication.OpenIdConnect.OpenIdConnectDefaults.AuthenticationScheme);

        options.ClientId.Should().Be(loginClientId);
        options.ClientSecret.Should().BeNullOrEmpty();
        var credential = options.ClientCredentials.Should().ContainSingle().Subject;
        credential.SourceType.ToString().Should().Be("SignedAssertionFromManagedIdentity");
        credential.ManagedIdentityClientId.Should().Be(managedIdentityClientId);
        var signIn = provider.GetRequiredService<IOptionsMonitor<Microsoft.AspNetCore.Authentication.OpenIdConnect.OpenIdConnectOptions>>()
            .Get(Microsoft.AspNetCore.Authentication.OpenIdConnect.OpenIdConnectDefaults.AuthenticationScheme);
        signIn.ResponseType.Should().Be(Microsoft.IdentityModel.Protocols.OpenIdConnect.OpenIdConnectResponseType.IdToken);
    }

    [TestMethod]
    public void ProductionRejectsConnectionStringsAndInsecureTls()
    {
        var environment = new Mock<IHostEnvironment>();
        environment.SetupGet(x => x.EnvironmentName).Returns(Environments.Production);
        var validator = new CosmosOptionsValidator(environment.Object);

        validator.Validate(null, new CosmosOptions { Endpoint = "https://example.documents.azure.com" }).Succeeded.Should().BeTrue();
        validator.Validate(null, new CosmosOptions { ConnectionString = "emulator", Endpoint = "https://example.documents.azure.com" }).Failed.Should().BeTrue();
        validator.Validate(null, new CosmosOptions { Endpoint = "http://localhost:8081" }).Failed.Should().BeTrue();
        validator.Validate(null, new CosmosOptions { Endpoint = "https://example.documents.azure.com", AllowInsecureEmulatorTls = true }).Failed.Should().BeTrue();
        validator.Validate(null, new CosmosOptions { Endpoint = "https://example.documents.azure.com", InitializeDatabase = true }).Failed.Should().BeTrue();
    }

    [TestMethod]
    public void DevelopmentRequiresEmulatorConnectionString()
    {
        var environment = new Mock<IHostEnvironment>();
        environment.SetupGet(x => x.EnvironmentName).Returns(Environments.Development);
        var validator = new CosmosOptionsValidator(environment.Object);
        validator.Validate(null, new CosmosOptions()).Failed.Should().BeTrue();
        validator.Validate(null, new CosmosOptions { ConnectionString = "AccountEndpoint=https://localhost:8081/;AccountKey=dGVzdA==;" }).Succeeded.Should().BeTrue();
        validator.Validate(null, new CosmosOptions { ConnectionString = "AccountEndpoint=https://deployed.documents.azure.com/;AccountKey=dGVzdA==;" }).Failed.Should().BeTrue();
        validator.Validate(null, new CosmosOptions { ConnectionString = "invalid" }).Failed.Should().BeTrue();
    }

    [TestMethod]
    public void ProductionCompositionUsesActualCosmosAdaptersWithManagedCredentialClient()
    {
        var environment = new Mock<IHostEnvironment>();
        environment.SetupGet(x => x.EnvironmentName).Returns(Environments.Production);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Cosmos:Endpoint"] = "https://test.documents.azure.com/",
            ["AzureAd:TenantId"] = "11111111-1111-1111-1111-111111111111",
            ["AzureAd:ClientId"] = "22222222-2222-2222-2222-222222222222"
        }).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddSingleton(environment.Object);
        services.AddAgentOps(configuration, environment.Object);
        services.AddSingleton(Mock.Of<TokenCredential>());
        services.AddSingleton<Microsoft.Extensions.Logging.ILoggerFactory>(Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance);
        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IExecutionStore>().Should().BeOfType<CosmosExecutionStore>();
        provider.GetRequiredService<JobStorage>().Should().BeOfType<InMemoryStorage>();
        provider.GetRequiredService<CosmosClient>().Endpoint.Should().Be(new Uri("https://test.documents.azure.com/"));
        provider.GetRequiredService<CosmosClient>().Should().BeSameAs(provider.GetRequiredService<CosmosClient>());
    }

    [TestMethod]
    public void OptionsRegistrationBindsEveryExtractedOptionsClassWithoutApplicationServices()
    {
        var environment = new Mock<IHostEnvironment>();
        environment.SetupGet(x => x.EnvironmentName).Returns(Environments.Production);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Cosmos:Endpoint"] = "https://options-test.documents.azure.com/",
            ["Cosmos:Database"] = "history",
            ["AzureAd:TenantId"] = "11111111-1111-1111-1111-111111111111",
            ["AzureAd:ClientId"] = "22222222-2222-2222-2222-222222222222",
            ["Executions:HistoryLimit"] = "37",
            ["Insights:LogLimit"] = "19",
            ["Analytics:PageSize"] = "7",
            ["Foundry:AgentName"] = "configured-agent",
            ["Webhook:ApiKey"] = "test-options-key",
            ["Hosting:ForwardLimit"] = "2"
        }).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddSingleton(environment.Object);
        services.AddAgentOpsOptions();
        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IOptions<CosmosOptions>>().Value.Database.Should().Be("history");
        provider.GetRequiredService<IOptions<IdentityOptions>>().Value.ClientId.Should().Be("22222222-2222-2222-2222-222222222222");
        provider.GetRequiredService<IOptions<ExecutionOptions>>().Value.HistoryLimit.Should().Be(37);
        provider.GetRequiredService<IOptions<InsightsOptions>>().Value.LogLimit.Should().Be(19);
        provider.GetRequiredService<IOptions<AnalyticsOptions>>().Value.PageSize.Should().Be(7);
        provider.GetRequiredService<IOptions<FoundryOptions>>().Value.AgentName.Should().Be("configured-agent");
        provider.GetRequiredService<IOptions<WebhookOptions>>().Value.ApiKey.Should().Be("test-options-key");
        provider.GetRequiredService<IOptions<HostingOptions>>().Value.ForwardLimit.Should().Be(2);
        provider.GetService<IExecutionStore>().Should().BeNull();
        provider.GetService<CosmosClient>().Should().BeNull();
    }

    [TestMethod]
    [DataRow("not cron")]
    [DataRow("0 0 31 2 *")]
    public async Task InvalidOrImpossibleCronFailsStartup(string cron)
    {
        await using var factory = new AppFactory(new Dictionary<string, string?> { ["Scheduling:Cron"] = cron });
        var act = () => factory.Client();
        act.Should().Throw<OptionsValidationException>().WithMessage("*Scheduling:Cron*");
    }

    [TestMethod]
    [DataRow("0", "50")]
    [DataRow("10", "0")]
    [DataRow("10", "201")]
    [DataRow("20", "10")]
    public async Task InvalidAnalyticsPageSizesFailStartup(string pageSize, string maxPageSize)
    {
        await using var factory = new AppFactory(new Dictionary<string, string?>
        {
            ["Analytics:PageSize"] = pageSize, ["Analytics:MaxPageSize"] = maxPageSize
        });
        var start = () => factory.Client();
        start.Should().Throw<OptionsValidationException>();
    }

    [TestMethod]
    [DataRow("InsightsTools:MaxRows", "0")]
    [DataRow("InsightsTools:MaxRows", "2")]
    [DataRow("InsightsTools:MaxLookbackMinutes", "30")]
    [DataRow("InsightsTools:MaxToolCalls", "0")]
    [DataRow("InsightsTools:MaxResultCharacters", "1024")]
    public async Task InvalidTelemetryToolLimitsFailStartup(string setting, string value)
    {
        await using var factory = new AppFactory(new Dictionary<string, string?> { [setting] = value });
        var start = () => factory.Client();
        start.Should().Throw<OptionsValidationException>();
    }

    [TestMethod]
    public void AgentFactoryRejectsMissingConfigurationAndEmptyInstructions()
    {
        var factory = new FoundryAgentFactory(Mock.Of<TokenCredential>(), Options.Create(new FoundryOptions()), NullLogger<FoundryAgentFactory>.Instance,
            Options.Create(new InsightsToolOptions()));
        var missingConfiguration = () => factory.Create("instructions supplied later");
        missingConfiguration.Should().Throw<InvalidOperationException>().WithMessage("*Configure Foundry*");
        var missingInstructions = () => factory.Create("");
        missingInstructions.Should().Throw<ArgumentException>();
    }

    [TestMethod]
    [DataRow("scp", "Agent.Run", true)]
    [DataRow("scp", "Other Agent.Run", true)]
    [DataRow("scp", "Agent.Run.Other", false)]
    [DataRow("roles", "AgentOps.Operator", true)]
    [DataRow("roles", "AgentOps.Reader", false)]
    public void ApiRequiresExactScopeOrOperatorRole(string type, string value, bool expected)
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(type, value)], "Test", "name", "roles"));
        Policies.CanRun(principal, new IdentityOptions()).Should().Be(expected);
    }
}
