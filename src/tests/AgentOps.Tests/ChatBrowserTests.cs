using AgentOps.Web;
using AgentOps.Web.Features.Agent;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;

namespace AgentOps.Tests;

[TestClass]
public sealed class ChatBrowserTests
{
    [TestMethod]
    [TestCategory("Browser")]
    public async Task ChatDeletionConfirmsBeforeRemovalAndAllowsRetryAfterFailedCleanup()
    {
        if (Environment.GetEnvironmentVariable("AGENTOPS_BROWSER_TESTS") != "true")
            Assert.Inconclusive("Set AGENTOPS_BROWSER_TESTS=true and install Playwright Chromium to run browser verification.");
        await using var factory = new AppFactory();
        factory.UseKestrel();
        using var client = factory.CreateClient();
        var address = factory.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new()
        {
            Channel = Environment.GetEnvironmentVariable("AGENTOPS_BROWSER_CHANNEL"), Headless = true
        });
        await using var context = await browser.NewContextAsync(new()
        {
            ExtraHTTPHeaders = new Dictionary<string, string> { ["X-Test-Role"] = "AgentOps.Operator" },
            ViewportSize = new() { Width = 390, Height = 844 }
        });
        var page = await context.NewPageAsync();
        await page.GotoAsync(address + RouteConstants.ChatPage);
        var input = page.GetByRole(AriaRole.Textbox, new() { Name = "Message your agent" });
        var newSession = page.GetByRole(AriaRole.Button, new() { Name = "New session" });
        await newSession.ClickAsync();
        await input.FillAsync("Remove this conversation");
        await input.PressAsync("Enter");
        await Assertions.Expect(page.GetByRole(AriaRole.Article, new() { Name = "Agent response" })).ToHaveCountAsync(1);
        var removed = factory.Chats.Sessions.Values.Single().Id;
        await newSession.ClickAsync();
        await input.FillAsync("Keep this conversation");
        await input.PressAsync("Enter");
        await Assertions.Expect(page.GetByRole(AriaRole.Article, new() { Name = "Agent response" })).ToHaveCountAsync(1);
        await page.Locator(".chat-session").Filter(new() { HasText = "Remove this conversation" }).ClickAsync();
        var accept = false;
        page.Dialog += async (_, dialog) =>
        {
            if (accept) await dialog.AcceptAsync();
            else await dialog.DismissAsync();
        };
        var delete = page.GetByRole(AriaRole.Button, new() { Name = "Delete session", Exact = true });
        await delete.ClickAsync();
        await Assertions.Expect(page.Locator(".chat-session")).ToHaveCountAsync(2);
        factory.Chats.Sessions.Values.Should().Contain(session => session.Id == removed);
        accept = true;
        await delete.ClickAsync();
        await Assertions.Expect(page.Locator(".chat-session")).ToHaveCountAsync(1);
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Keep this conversation" })).ToBeVisibleAsync();
        factory.Chats.Sessions.Values.Should().NotContain(session => session.Id == removed);
        factory.Chats.Messages.Should().HaveCount(2).And.OnlyContain(message => message.SessionId != removed);
        factory.Chats.FailDelete = true;
        await delete.ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Alert)).ToContainTextAsync("HTTP 500");
        var retry = page.GetByRole(AriaRole.Button, new() { Name = "Retry deletion" });
        await Assertions.Expect(retry).ToBeVisibleAsync();
        await Assertions.Expect(input).ToBeDisabledAsync();
        factory.Chats.FailDelete = false;
        await retry.ClickAsync();
        await Assertions.Expect(page.Locator(".chat-session")).ToHaveCountAsync(0);
        await Assertions.Expect(page.GetByRole(AriaRole.Article)).ToHaveCountAsync(0);
        await Assertions.Expect(input).ToBeDisabledAsync();
        factory.Chats.Sessions.Should().BeEmpty();
        factory.Chats.Messages.Should().BeEmpty();
        (await page.EvaluateAsync<bool>("document.documentElement.scrollWidth > window.innerWidth")).Should().BeFalse();
    }

    [TestMethod]
    [TestCategory("Browser")]
    public async Task ChatWorkspaceCreatesAndRestoresSessionsSendsHistoryAndCopiesSafeModelLabelledReplies()
    {
        if (Environment.GetEnvironmentVariable("AGENTOPS_BROWSER_TESTS") != "true")
            Assert.Inconclusive("Set AGENTOPS_BROWSER_TESTS=true and install Playwright Chromium to run browser verification.");
        await using var factory = new AppFactory();
        factory.UseKestrel();
        using var client = factory.CreateClient();
        var address = factory.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new()
        {
            Channel = Environment.GetEnvironmentVariable("AGENTOPS_BROWSER_CHANNEL"), Headless = true
        });
        await using var context = await browser.NewContextAsync(new()
        {
            ExtraHTTPHeaders = new Dictionary<string, string> { ["X-Test-Role"] = "AgentOps.Operator" },
            ViewportSize = new() { Width = 1440, Height = 1000 }
        });
        await context.GrantPermissionsAsync(["clipboard-read", "clipboard-write"]);
        var page = await context.NewPageAsync();
        var errors = new List<string>();
        page.PageError += (_, error) => errors.Add(error);
        await page.GotoAsync(address);
        await page.GetByRole(AriaRole.Navigation, new() { Name = "Primary navigation" })
            .GetByRole(AriaRole.Link, new() { Name = "Agent chat" }).ClickAsync();
        page.Url.Should().Be(address + RouteConstants.ChatPage);
        await page.GetByRole(AriaRole.Button, new() { Name = "New session" }).ClickAsync();
        var input = page.GetByRole(AriaRole.Textbox, new() { Name = "Message your agent" });
        const string prompt = "<img src=x onerror=alert(1)> Remember teal";
        await input.FillAsync(prompt);
        await input.PressAsync("Enter");
        var agent = page.GetByRole(AriaRole.Article, new() { Name = "Agent response" });
        var user = page.GetByRole(AriaRole.Article, new() { Name = "User prompt" });
        await Assertions.Expect(agent).ToContainTextAsync($"Reply to: {prompt}");
        await Assertions.Expect(agent).ToContainTextAsync("Model: test-deployment");
        await Assertions.Expect(agent.Locator("img")).ToHaveCountAsync(0);
        await Assertions.Expect(user.Locator("img")).ToHaveCountAsync(0);
        var userBox = await user.BoundingBoxAsync();
        var agentBox = await agent.BoundingBoxAsync();
        userBox!.X.Should().BeGreaterThan(agentBox!.X);
        await agent.GetByRole(AriaRole.Button, new() { Name = "Copy agent message" }).ClickAsync();
        await Assertions.Expect(agent.GetByRole(AriaRole.Button, new() { Name = "Message copied" })).ToBeVisibleAsync();
        (await page.EvaluateAsync<string>("navigator.clipboard.readText()")).Should().Be($"Reply to: {prompt}");
        await input.FillAsync("Which colour?");
        await page.GetByRole(AriaRole.Button, new() { Name = "Send", Exact = true }).ClickAsync();
        await Assertions.Expect(agent).ToHaveCountAsync(2);
        factory.ChatAgent.Calls.Last().Select(message => message.Role).Should().Equal("user", "agent", "user");
        await page.ReloadAsync();
        await Assertions.Expect(agent).ToHaveCountAsync(2);
        await Assertions.Expect(user).ToHaveCountAsync(2);
        await page.GetByRole(AriaRole.Button, new() { Name = "New session" }).ClickAsync();
        await Assertions.Expect(user).ToHaveCountAsync(0);
        await page.Locator(".chat-session").Filter(new() { HasText = "Remember teal" }).ClickAsync();
        await Assertions.Expect(user).ToHaveCountAsync(2);
        await input.FillAsync("first line");
        await input.PressAsync("Shift+Enter");
        await input.PressAsync("x");
        await Assertions.Expect(input).ToHaveValueAsync("first line\nx");
        await Assertions.Expect(agent).ToHaveCountAsync(2);
        await page.SetViewportSizeAsync(390, 844);
        (await page.EvaluateAsync<bool>("document.documentElement.scrollWidth > window.innerWidth")).Should().BeFalse();
        await Assertions.Expect(input).ToBeVisibleAsync();
        if (Environment.GetEnvironmentVariable("AGENTOPS_CHAT_SCREENSHOT") is { Length: > 0 } screenshot)
        {
            await page.SetViewportSizeAsync(1440, 1000);
            await page.ScreenshotAsync(new() { Path = screenshot, FullPage = true });
        }
        errors.Should().BeEmpty();
    }

    [TestMethod]
    [TestCategory("Browser")]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ChatShowsModelFailureAndRetainsSavedPromptWithoutInventingAReply(bool missingConfiguration)
    {
        if (Environment.GetEnvironmentVariable("AGENTOPS_BROWSER_TESTS") != "true")
            Assert.Inconclusive("Set AGENTOPS_BROWSER_TESTS=true and install Playwright Chromium to run browser verification.");
        await using var factory = new AppFactory();
        if (missingConfiguration) factory.ChatAgent.Failure = new FoundryConfigurationException();
        else factory.ChatAgent.Fail = true;
        factory.UseKestrel();
        using var client = factory.CreateClient();
        var address = factory.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new()
        {
            Channel = Environment.GetEnvironmentVariable("AGENTOPS_BROWSER_CHANNEL"), Headless = true
        });
        await using var context = await browser.NewContextAsync(new()
        {
            ExtraHTTPHeaders = new Dictionary<string, string> { ["X-Test-Role"] = "AgentOps.Operator" }
        });
        var page = await context.NewPageAsync();
        await page.GotoAsync(address + RouteConstants.ChatPage);
        await page.GetByRole(AriaRole.Button, new() { Name = "New session" }).ClickAsync();
        await page.GetByRole(AriaRole.Textbox).FillAsync("A prompt worth saving");
        await page.GetByRole(AriaRole.Button, new() { Name = "Send", Exact = true }).ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Alert)).ToContainTextAsync("Refresh to check saved messages");
        await Assertions.Expect(page.GetByRole(AriaRole.Alert)).ToContainTextAsync("Reference:");
        if (missingConfiguration)
        {
            await Assertions.Expect(page.GetByRole(AriaRole.Alert)).ToContainTextAsync("Foundry:ProjectEndpoint");
            await Assertions.Expect(page.GetByRole(AriaRole.Alert)).ToContainTextAsync("HTTP 503");
        }
        await Assertions.Expect(page.GetByRole(AriaRole.Article, new() { Name = "User prompt" })).ToContainTextAsync("A prompt worth saving");
        await Assertions.Expect(page.GetByRole(AriaRole.Article, new() { Name = "Agent response" })).ToHaveCountAsync(0);
    }
}
