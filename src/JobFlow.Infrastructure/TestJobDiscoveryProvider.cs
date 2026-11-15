using JobFlow.Application;
using JobFlow.Domain;
using Microsoft.Playwright;
namespace JobFlow.Infrastructure;
// Offline fixture adapter: never navigates to the configured platform URL.
public class TestJobDiscoveryProvider : IJobDiscoveryProvider
{
    public async Task<IReadOnlyList<Vacancy>> DiscoverAsync(JobPlatform platform, CancellationToken ct)
    {
        if (platform.Adapter != "test-form") throw new InvalidOperationException("Only the isolated test adapter supports discovery.");
        ct.ThrowIfCancellationRequested();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        var page = await browser.NewPageAsync();
        await page.SetContentAsync("""
            <!doctype html><html lang="en"><title>JobFlow synthetic vacancies</title>
            <article data-job-id="synthetic-001"><h1>TEST — Software engineer</h1>
            <span class="employer">Synthetic Employer</span>
            <p class="description">Synthetic vacancy for dry-run testing. C#, SQL. Salary and location require clarification.</p>
            </article></html>
            """);
        var listing = page.Locator("article");
        var vacancy = new Vacancy { JobPlatformId = platform.Id,
            ExternalId = await listing.GetAttributeAsync("data-job-id") ?? throw new InvalidOperationException("Missing ID."),
            Title = await listing.Locator("h1").InnerTextAsync(), Employer = await listing.Locator(".employer").InnerTextAsync(),
            Description = await listing.Locator(".description").InnerTextAsync(), Url = "https://example.invalid/jobs/synthetic-001" };
        ct.ThrowIfCancellationRequested();
        return [vacancy];
    }
}
