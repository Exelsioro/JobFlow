using JobFlow.Infrastructure;
using Microsoft.Playwright;

namespace JobFlow.IntegrationTests;

public class BrowserSessionModeTests
{
    [Fact]
    public async Task WorkflowOwnsProfileBetweenActionsAndClosesItWithoutLosingAuthorizationOnNextRun()
    {
        await using var fixture = new ProfileFixture();
        await using var sessions = new BrowserSessions(true);
        await sessions.OpenAsync(fixture.Id);
        await sessions.GetManualPage(fixture.Id).Context.RouteAsync("https://example.invalid/**", route => route.FulfillAsync(new() { ContentType = "text/html", Body = "<h1>Synthetic draft</h1>" }));
        var draftId = Guid.NewGuid();
        await sessions.OpenDraftAsync(draftId, fixture.Id, "https://example.invalid/");
        Assert.False(sessions.GetDraftPage(draftId).IsClosed);
        var workflow = await sessions.BeginWorkflowAsync(fixture.Id, default);
        await using (var step = await sessions.AcquireAsync(fixture.Id, default, workflowToken: workflow.Token))
        {
            await step.Page.Context.AddCookiesAsync([new() { Name = "session_auth", Value = "synthetic", Domain = "example.invalid", Path = "/" }]);
            await LoadFixtureAsync(step.Page);
            await step.Page.EvaluateAsync("localStorage.setItem('auth', 'preserved')");
        }
        Assert.True(sessions.IsAutomating(fixture.Id));
        Assert.Throws<InvalidOperationException>(() => sessions.GetDraftPage(draftId));
        await Assert.ThrowsAsync<InvalidOperationException>(() => sessions.OpenAsync(fixture.Id));
        await Assert.ThrowsAsync<InvalidOperationException>(() => sessions.CloseAsync(fixture.Id));
        await Assert.ThrowsAsync<InvalidOperationException>(() => sessions.AcquireAsync(fixture.Id, default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => sessions.BeginWorkflowAsync(fixture.Id, default));
        await workflow.DisposeAsync();
        Assert.False(sessions.IsBusy(fixture.Id));
        Assert.False(sessions.IsAutomating(fixture.Id));
        await using var nextWorkflow = await sessions.BeginWorkflowAsync(fixture.Id, default);
        await using var nextStep = await sessions.AcquireAsync(fixture.Id, default, workflowToken: nextWorkflow.Token);
        Assert.Contains(await nextStep.Page.Context.CookiesAsync(), x => x.Name == "session_auth" && x.Value == "synthetic");
        await LoadFixtureAsync(nextStep.Page);
        Assert.Equal("preserved", await nextStep.Page.EvaluateAsync<string>("localStorage.getItem('auth')"));
    }

    [Fact]
    public async Task ChangingModePreservesSessionCookiesPersistentCookiesAndLocalStorage()
    {
        await using var fixture = new ProfileFixture();
        await using var sessions = new BrowserSessions(true);
        IBrowserContext initialContext;
        await using (var lease = await sessions.AcquireAsync(fixture.Id, default, "headless"))
        {
            initialContext = lease.Page.Context;
            await initialContext.AddCookiesAsync([
                new() { Name = "session_auth", Value = "synthetic", Domain = "example.invalid", Path = "/", HttpOnly = true, Secure = true },
                new() { Name = "persistent_auth", Value = "synthetic", Domain = "example.invalid", Path = "/", HttpOnly = true, Secure = true,
                    Expires = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds() }
            ]);
            await LoadFixtureAsync(lease.Page);
            await lease.Page.EvaluateAsync("localStorage.setItem('synthetic-auth', 'preserved')");
        }
        await using (var sameMode = await sessions.AcquireAsync(fixture.Id, default, "headless"))
            Assert.Same(initialContext, sameMode.Page.Context);
        foreach (var mode in new[] { "new-headless", "headed", "inherit" })
        {
            await using var lease = await sessions.AcquireAsync(fixture.Id, default, mode);
            Assert.NotSame(initialContext, lease.Page.Context);
            Assert.Equal(mode == "inherit" ? "headless" : mode, sessions.GetMode(fixture.Id));
            var cookies = await lease.Page.Context.CookiesAsync();
            Assert.Contains(cookies, x => x.Name == "session_auth" && x.Value == "synthetic" && x.HttpOnly);
            Assert.Contains(cookies, x => x.Name == "persistent_auth" && x.Value == "synthetic");
            await LoadFixtureAsync(lease.Page);
            Assert.Equal("preserved", await lease.Page.EvaluateAsync<string>("localStorage.getItem('synthetic-auth')"));
            var ua = await lease.Page.EvaluateAsync<string>("navigator.userAgent");
            Assert.Equal(mode != "headed", ua.Contains("HeadlessChrome"));
        }
    }

    [Fact]
    public async Task ModeSwitchAndSecondHostCannotInterruptAnActiveAutomationLease()
    {
        await using var fixture = new ProfileFixture();
        await using var sessions = new BrowserSessions(true);
        await using var anotherHost = new BrowserSessions(true);
        await using (var lease = await sessions.AcquireAsync(fixture.Id, default, "headless"))
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => sessions.OpenAsync(fixture.Id, "headed"));
            await Assert.ThrowsAsync<InvalidOperationException>(() => sessions.AcquireAsync(fixture.Id, default, "new-headless"));
            await Assert.ThrowsAsync<InvalidOperationException>(() => sessions.CloseAsync(fixture.Id));
            await Assert.ThrowsAsync<IOException>(() => anotherHost.AcquireAsync(fixture.Id, default));
            Assert.False(lease.Page.IsClosed);
            Assert.Equal("headless", sessions.GetMode(fixture.Id));
        }
        await sessions.CloseAsync(fixture.Id);
        await Assert.ThrowsAsync<ArgumentException>(() => anotherHost.AcquireAsync(fixture.Id, default, "invalid"));
        await using var acquiredAfterClose = await anotherHost.AcquireAsync(fixture.Id, default);
        Assert.Equal("headless", anotherHost.GetMode(fixture.Id));
    }

    private static async Task LoadFixtureAsync(IPage page)
    {
        await page.RouteAsync("https://example.invalid/**", route => route.FulfillAsync(new() { ContentType = "text/html", Body = "<h1>Synthetic authenticated page</h1>" }));
        await page.GotoAsync("https://example.invalid/", new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 30000 });
    }

    private sealed class ProfileFixture : IAsyncDisposable
    {
        private readonly string directory = Path.Combine(Path.GetTempPath(), "jobflow-mode-" + Guid.NewGuid().ToString("N"));
        private readonly string? previous = Environment.GetEnvironmentVariable("JOBFLOW_DATA_DIR");
        public Guid Id { get; } = Guid.NewGuid();
        public ProfileFixture() => Environment.SetEnvironmentVariable("JOBFLOW_DATA_DIR", directory);
        public async ValueTask DisposeAsync()
        {
            Environment.SetEnvironmentVariable("JOBFLOW_DATA_DIR", previous);
            for (var attempt = 0; ; attempt++)
            {
                try { if (Directory.Exists(directory)) Directory.Delete(directory, true); break; }
                catch (IOException) when (attempt < 19) { await Task.Delay(250); }
            }
        }
    }
}
