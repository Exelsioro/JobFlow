using System.Text.Json;
using Microsoft.Playwright;

internal static class WorkspaceUiSmoke
{
    // Serve the production bundle and synthetic API responses entirely inside Playwright.
    public static async Task RunAsync(string root)
    {
        var dist = Path.Combine(root, "src", "JobFlow.Web", "dist", "jobflow-web", "browser");
        if (!File.Exists(Path.Combine(dist, "index.html")))
            throw new InvalidOperationException("Build JobFlow.Web before running --workspace-ui.");

        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync();
        var page = await browser.NewPageAsync(new() { ViewportSize = new() { Width = 1440, Height = 900 } });
        var errors = new List<string>();
        page.PageError += (_, error) => errors.Add(error);
        var failServices = true;
        var records = new Dictionary<string, object>
        {
            ["profiles"] = new[] { new { id = "a", name = "Первый" }, new { id = "b", name = "Второй" } },
            ["vacancies"] = new[] { new { id = "v", title = "Тестовая вакансия", employer = "Тест" } },
            ["applications"] = new[] { new { id = "r", candidateProfileId = "a", vacancyId = "v", state = "ReadyForReview", isTest = true } },
            ["workflows"] = new[] { new { id = "w", candidateProfileId = "a", name = "Тестовый workflow" } },
            ["workflow-runs"] = new[] { new { id = "run", candidateProfileId = "a", workflowDefinitionId = "w",
                status = "Succeeded", createdAt = "2026-01-01T12:00:00", @checked = 1, prepared = 1, submitted = 0 } }
        };
        await page.RouteAsync("**/*", async route =>
        {
            var url = new Uri(route.Request.Url);
            if (url.Host != "workspace.test" || route.Request.Method != "GET")
            {
                errors.Add($"Unexpected request: {route.Request.Method} {url.AbsolutePath}");
                await route.AbortAsync();
                return;
            }
            if (!url.AbsolutePath.StartsWith("/api/", StringComparison.Ordinal))
            {
                var name = url.AbsolutePath == "/" ? "index.html" : Path.GetFileName(url.AbsolutePath);
                await route.FulfillAsync(new() { Path = Path.Combine(dist, name) });
                return;
            }
            var key = url.AbsolutePath[5..];
            await route.FulfillAsync(new()
            {
                Status = key == "services" && failServices ? 503 : 200,
                ContentType = "application/json",
                Body = JsonSerializer.Serialize(records.GetValueOrDefault(key, Array.Empty<object>()))
            });
        });

        await page.GotoAsync("http://workspace.test");
        await page.Locator(".notice.warning").Filter(new() { HasText = "Службы" }).WaitForAsync();
        await page.GetByRole(AriaRole.Link, new() { NameRegex = new("Тестовая вакансия") }).WaitForAsync();
        await page.GotoAsync("http://workspace.test/#services");
        await page.GetByText("Данные раздела не получены.", new() { Exact = true }).WaitForAsync();
        failServices = false;
        var refresh = page.GetByRole(AriaRole.Button, new() { Name = "Обновить", Exact = true });
        await refresh.ClickAsync();
        await page.Locator(".notice.warning").WaitForAsync(new() { State = WaitForSelectorState.Hidden });
        await page.GetByRole(AriaRole.Heading, new() { Name = "Здесь пока нет записей" }).WaitForAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Обзор", Exact = true }).ClickAsync();

        await page.GetByRole(AriaRole.Button, new() { NameRegex = new("Проверить материалы.*1") }).ClickAsync();
        await page.Locator("tbody tr").Filter(new() { HasText = "Тестовая вакансия" }).WaitForAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Обзор", Exact = true }).ClickAsync();
        await page.GetByRole(AriaRole.Link, new() { NameRegex = new("Тестовый workflow") }).ClickAsync();
        await page.Locator(".run-details summary").ClickAsync();
        var profile = page.GetByLabel("Рабочий профиль");
        await profile.SelectOptionAsync("b");
        await page.WaitForURLAsync("**/#workflow-runs");
        await refresh.ClickAsync();
        await refresh.WaitForAsync();
        if (await profile.InputValueAsync() != "b" || await page.Locator(".related-record").CountAsync() != 0)
            throw new InvalidOperationException("Refreshing restored the previous profile or focused record.");

        await page.GetByRole(AriaRole.Button, new() { Name = "Обзор", Exact = true }).ClickAsync();
        await page.GetByText("Нет откликов, требующих решения.", new() { Exact = true }).WaitForAsync();
        await profile.SelectOptionAsync("a");
        await page.SetViewportSizeAsync(390, 844);
        await page.GetByRole(AriaRole.Button, new() { Name = "Все отклики" }).ClickAsync();
        await page.Locator(".application-list-table tbody tr").WaitForAsync();
        if (await page.EvaluateAsync<bool>("document.documentElement.scrollWidth > innerWidth"))
            throw new InvalidOperationException("Application list overflows the mobile viewport.");
        if (errors.Count > 0) throw new InvalidOperationException(string.Join("\n", errors));
        Console.WriteLine("PASS: partial API outage and retry, profile-scoped dashboard and drill-down, mobile list; no external requests or writes.");
    }
}
