using AgentOps.Web;
using AgentOps.Web.Features.Chat;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;

namespace AgentOps.Tests;

[TestClass]
public sealed class NotificationBrowserTests
{
    [TestMethod]
    [TestCategory("Browser")]
    public async Task InboxSearchPagingCompletionAndPrivateChatHandoffWorkOnDesktopAndMobile()
    {
        if (Environment.GetEnvironmentVariable("AGENTOPS_BROWSER_TESTS") != "true")
            Assert.Inconclusive("Set AGENTOPS_BROWSER_TESTS=true and install Playwright Chromium to run browser verification.");
        await using var factory = new AppFactory(new Dictionary<string, string?>
        {
            ["Notifications:PageSize"] = "2",
            ["Chat:SessionPageSize"] = "1"
        });
        var now = DateTimeOffset.UtcNow;
        const string message = "<img src=x onerror=alert(1)> Check latency";
        await factory.Notifications.CreateAsync(new("newest", """{"data":{"message":"<script>raw event</script>"}}""", message, now), CancellationToken.None);
        await factory.Notifications.CreateAsync(new("middle", "{}", "Earlier alert", now.AddMinutes(-1))
        { IsHandled = true, Comment = "Previously checked" }, CancellationToken.None);
        await factory.Notifications.CreateAsync(new("oldest", "{}", "Oldest alert", now.AddMinutes(-2)), CancellationToken.None);
        factory.UseKestrel();
        using var client = factory.CreateClient();
        var address = factory.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new()
        {
            Channel = Environment.GetEnvironmentVariable("AGENTOPS_BROWSER_CHANNEL"),
            Headless = true
        });
        await using var context = await browser.NewContextAsync(new()
        {
            ExtraHTTPHeaders = new Dictionary<string, string> { ["X-Test-Role"] = "AgentOps.Operator" },
            ViewportSize = new() { Width = 1440, Height = 1000 }
        });
        var page = await context.NewPageAsync();
        var errors = new List<string>();
        page.PageError += (_, error) => errors.Add(error);
        await page.GotoAsync(address + RouteConstants.NotificationsPage);
        var rows = page.Locator("tr[data-notification-id]");
        await Assertions.Expect(rows).ToHaveCountAsync(2);
        await Assertions.Expect(rows.First).ToHaveAttributeAsync("data-notification-id", "newest");
        await Assertions.Expect(page.GetByLabel("Status", new() { Exact = true })).ToHaveValueAsync("all");
        await Assertions.Expect(rows.First).ToContainTextAsync(message);
        await rows.First.Locator("summary").ClickAsync();
        await Assertions.Expect(rows.First.Locator("pre")).ToContainTextAsync("<script>raw event</script>");
        await Assertions.Expect(rows.Locator("img, script")).ToHaveCountAsync(0);
        if (Environment.GetEnvironmentVariable("AGENTOPS_NOTIFICATION_SCREENSHOT") is { Length: > 0 } screenshot)
            await page.ScreenshotAsync(new() { Path = screenshot, FullPage = true });
        await page.GetByRole(AriaRole.Button, new() { Name = "Next", Exact = true }).ClickAsync();
        await Assertions.Expect(rows).ToHaveCountAsync(1);
        await Assertions.Expect(rows.First).ToHaveAttributeAsync("data-notification-id", "oldest");
        await page.GetByRole(AriaRole.Button, new() { Name = "Previous", Exact = true }).ClickAsync();
        await Assertions.Expect(rows).ToHaveCountAsync(2);
        await page.GetByLabel("Received date").SelectOptionAsync("asc");
        await Assertions.Expect(rows.First).ToHaveAttributeAsync("data-notification-id", "oldest");
        await page.GetByLabel("Received date").SelectOptionAsync("desc");
        await Assertions.Expect(rows.First).ToHaveAttributeAsync("data-notification-id", "newest");
        await page.GetByLabel("Search messages").FillAsync("latency");
        await page.GetByRole(AriaRole.Button, new() { Name = "Search", Exact = true }).ClickAsync();
        await Assertions.Expect(rows).ToHaveCountAsync(1);
        var checkbox = rows.First.GetByRole(AriaRole.Checkbox);
        await checkbox.ClickAsync();
        var dialog = page.GetByRole(AriaRole.Dialog);
        await Assertions.Expect(dialog).ToBeVisibleAsync();
        await Assertions.Expect(checkbox).Not.ToBeCheckedAsync();
        await dialog.GetByRole(AriaRole.Button, new() { Name = "Cancel", Exact = true }).ClickAsync();
        factory.Notifications.Items["newest"].IsHandled.Should().BeFalse();
        await checkbox.ClickAsync();
        await dialog.GetByLabel("Completion comment").FillAsync("<b>Reviewed and resolved</b>");
        factory.Notifications.FailUpdate = true;
        await dialog.GetByRole(AriaRole.Button, new() { Name = "OK", Exact = true }).ClickAsync();
        await Assertions.Expect(dialog.GetByRole(AriaRole.Alert)).ToContainTextAsync("could not be updated");
        factory.Notifications.Items["newest"].IsHandled.Should().BeFalse();
        factory.Notifications.FailUpdate = false;
        await dialog.GetByRole(AriaRole.Button, new() { Name = "OK", Exact = true }).ClickAsync();
        await Assertions.Expect(dialog).Not.ToBeVisibleAsync();
        await Assertions.Expect(checkbox).ToBeCheckedAsync();
        await Assertions.Expect(rows.First.Locator(".notification-comment")).ToHaveTextAsync("<b>Reviewed and resolved</b>");
        await Assertions.Expect(rows.First.Locator(".notification-comment b")).ToHaveCountAsync(0);
        await checkbox.ClickAsync();
        await Assertions.Expect(page.Locator(".notification-pagination")).ToContainTextAsync("marked as unhandled");
        await Assertions.Expect(checkbox).Not.ToBeCheckedAsync();
        await checkbox.ClickAsync();
        await dialog.GetByRole(AriaRole.Button, new() { Name = "OK", Exact = true }).ClickAsync();
        await Assertions.Expect(dialog).Not.ToBeVisibleAsync();
        await Assertions.Expect(checkbox).ToBeCheckedAsync();
        factory.Notifications.Items["newest"].Comment.Should().BeNull("completion comments are optional");
        await page.GetByLabel("Status", new() { Exact = true }).SelectOptionAsync("unhandled");
        await Assertions.Expect(rows).ToHaveCountAsync(0);
        await page.GetByLabel("Status", new() { Exact = true }).SelectOptionAsync("handled");
        await Assertions.Expect(rows).ToHaveCountAsync(1);
        await rows.First.GetByRole(AriaRole.Button, new() { Name = "Start chat" }).ClickAsync();
        var prompt = page.GetByRole(AriaRole.Textbox, new() { Name = "Message your agent" });
        await Assertions.Expect(prompt).ToHaveValueAsync(message);
        var session = factory.Chats.Sessions.Values.Single();
        page.Url.Should().Contain("sessionId=" + session.Id);
        factory.Notifications.Items["newest"].ChatSessionId.Should().Be(session.Id);
        factory.ChatAgent.Calls.Should().BeEmpty();
        factory.Chats.Messages.Should().BeEmpty();
        await factory.Chats.CreateAsync(new ChatSession("different-session", session.OwnerId, "Newer session",
            now.AddHours(1), now.AddHours(1)), CancellationToken.None);
        await page.ReloadAsync();
        await Assertions.Expect(prompt).ToHaveValueAsync(message);
        await page.GetByRole(AriaRole.Button, new() { Name = "Send", Exact = true }).ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Article, new() { Name = "Agent response" })).ToContainTextAsync("Reply to:");
        await Assertions.Expect(prompt).ToHaveValueAsync("");
        factory.Chats.Sessions[(session.OwnerId, session.Id)].InitialDraft.Should().BeNull();
        await page.GetByRole(AriaRole.Navigation, new() { Name = "Primary navigation" })
            .GetByRole(AriaRole.Link, new() { Name = "Webhook inbox" }).ClickAsync();
        var sessionLink = page.GetByRole(AriaRole.Link, new() { Name = session.Id, Exact = true });
        await Assertions.Expect(sessionLink).ToBeVisibleAsync();
        await page.SetViewportSizeAsync(390, 844);
        (await page.EvaluateAsync<bool>("document.documentElement.scrollWidth > window.innerWidth")).Should().BeFalse();
        await sessionLink.ClickAsync();
        await Assertions.Expect(prompt).ToHaveValueAsync("");
        await Assertions.Expect(page.GetByRole(AriaRole.Article, new() { Name = "User prompt" })).ToContainTextAsync(message);
        errors.Should().BeEmpty();
    }

    [TestMethod]
    [TestCategory("Browser")]
    public async Task ReaderCanReviewButCannotChangeStatusOrStartChat()
    {
        if (Environment.GetEnvironmentVariable("AGENTOPS_BROWSER_TESTS") != "true")
            Assert.Inconclusive("Set AGENTOPS_BROWSER_TESTS=true and install Playwright Chromium to run browser verification.");
        await using var factory = new AppFactory();
        await factory.Notifications.CreateAsync(new("read-only", "{}", "Review this", DateTimeOffset.UtcNow), CancellationToken.None);
        factory.UseKestrel();
        using var client = factory.CreateClient();
        var address = factory.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new()
        {
            Channel = Environment.GetEnvironmentVariable("AGENTOPS_BROWSER_CHANNEL"),
            Headless = true
        });
        await using var context = await browser.NewContextAsync(new()
        {
            ExtraHTTPHeaders = new Dictionary<string, string> { ["X-Test-Role"] = "AgentOps.Reader" }
        });
        var page = await context.NewPageAsync();
        await page.GotoAsync(address + RouteConstants.NotificationsPage);
        await Assertions.Expect(page.GetByRole(AriaRole.Checkbox)).ToBeDisabledAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { Name = "Start chat" })).ToHaveCountAsync(0);
    }
}
