using JobFlow.Infrastructure;
namespace JobFlow.IntegrationTests;
public class BrowserTests
{
    [Fact] public async Task BrowserDraftFillsRecognizedFieldsAndLeavesSubmissionToTheUser()
    {
        using var playwright = await Microsoft.Playwright.Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        var page = await browser.NewPageAsync();
        var submits = 0;
        await page.RouteAsync("https://test-form.invalid/**", async route =>
        {
            if (route.Request.Method == "POST") Interlocked.Increment(ref submits);
            await route.FulfillAsync(new() { ContentType = "text/html", Body = """
                <form method="post">
                  <label>Full name<input autocomplete="name" value="Existing"></label>
                  <label>Email<input type="email"></label>
                  <label>Cover letter<textarea></textarea></label>
                  <label>Unexpected question<input name="extra" required></label>
                  <button type="submit">Send</button>
                </form>
                <form><label>Email<input type="email"></label><button>Login</button></form>
                """ });
        });
        await page.GotoAsync("https://test-form.invalid/apply");
        var preview = await BrowserDraftFiller.InspectAsync(page);
        var form = Assert.Single(preview.Forms);
        var filled = await BrowserDraftFiller.FillAsync(page, preview, form.Index,
            new Dictionary<string, string> { ["name"] = "Override", ["email"] = "synthetic@example.invalid",
                ["letter"] = "Approved test draft" });
        Assert.Equal(2, filled);
        Assert.Equal("Existing", await page.GetByLabel("Full name").InputValueAsync());
        Assert.Equal("Approved test draft", await page.GetByLabel("Cover letter").InputValueAsync());
        Assert.Empty(await page.Locator("[name=extra]").InputValueAsync());
        Assert.Empty(await page.Locator("form").Nth(1).GetByLabel("Email").InputValueAsync());
        Assert.Equal(0, submits);
        await page.Locator("textarea").EvaluateAsync("el => el.name = 'changed'");
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            BrowserDraftFiller.FillAsync(page, preview, form.Index, new Dictionary<string, string>()));
    }

    [Fact] public async Task IsolatedBrowserFormProducesTestReceipt()
    {
        var result = await new PlaywrightBrowser().DryRunAsync(default);
        Assert.True(result.Confirmed); Assert.Equal("TEST-RECEIPT-001", result.Evidence);
    }
    [Theory]
    [InlineData(TestBrowserScenario.UnknownRequiredField, "Blocked")]
    [InlineData(TestBrowserScenario.AuthorizationLost, "Blocked")]
    [InlineData(TestBrowserScenario.ConfirmationMissing, "SubmissionUnknown")]
    public async Task UnsafeBrowserOutcomesNeverReportSuccess(TestBrowserScenario scenario, string expected)
    {
        var result = await new PlaywrightBrowser().RunScenarioAsync(scenario, default);
        Assert.False(result.Confirmed); Assert.StartsWith(expected, result.Evidence);
    }
    [Fact] public async Task TestDiscoveryExtractsVacancyFromRenderedFixture()
    {
        var platform = new JobFlow.Domain.JobPlatform { Adapter = "test-form" };
        var vacancies = await new TestJobDiscoveryProvider().DiscoverAsync(platform, default);
        var vacancy = Assert.Single(vacancies);
        Assert.Equal(platform.Id, vacancy.JobPlatformId); Assert.Equal("synthetic-001", vacancy.ExternalId);
        Assert.StartsWith("TEST", vacancy.Title);
    }
}

