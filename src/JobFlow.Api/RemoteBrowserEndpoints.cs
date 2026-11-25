using JobFlow.Infrastructure;
using Microsoft.Playwright;

namespace JobFlow.Api;

public static class RemoteBrowserEndpoints
{
    public static void MapRemoteBrowserEndpoints(this WebApplication app)
    {
        app.MapGet("/api/browser-profiles/{id:guid}/tabs", async (Guid id, BrowserSessions sessions) =>
            Results.Ok(new { tabs = await sessions.GetTabsAsync(id), automating = sessions.IsAutomating(id) }));
        app.MapGet("/api/browser-profiles/{id:guid}/screen", async (Guid id, int? tab, HttpContext context, BrowserSessions sessions) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            var page = sessions.GetManualPage(id, tab ?? 0);
            var bytes = await page.ScreenshotAsync(new() { Type = ScreenshotType.Jpeg, Quality = 65, Timeout = 5000 });
            return Results.File(bytes, "image/jpeg");
        });
        app.MapPost("/api/browser-profiles/{id:guid}/control", async (Guid id, BrowserControlRequest input, BrowserSessions sessions) =>
        {
            var page = sessions.GetManualPage(id, input.Tab);
            await ControlAsync(page, input);
            return Results.Ok(new { url = page.Url });
        });
    }

    public static async Task ControlAsync(IPage page, BrowserControlRequest input)
    {
        switch (input.Action)
        {
            case "navigate":
                if (!Uri.TryCreate(input.Value, UriKind.Absolute, out var url) || url.UserInfo.Length != 0 ||
                    !(url.Scheme == "https" || url.Scheme == "http" && url.IsLoopback))
                    throw new ArgumentException("Enter an HTTPS address to open in the browser.");
                // The UI polls the browser view separately; don't wait for scripts and subframes here.
                await page.GotoAsync(url.AbsoluteUri, new() { WaitUntil = WaitUntilState.Commit, Timeout = 30000 }); break;
            case "click":
                var size = page.ViewportSize;
                if (!float.IsFinite(input.X) || !float.IsFinite(input.Y) || input.X < 0 || input.Y < 0 ||
                    input.X > (size?.Width ?? 4096) || input.Y > (size?.Height ?? 4096)) throw new ArgumentException("Click is outside the browser view.");
                await page.Mouse.ClickAsync(input.X, input.Y); break;
            case "text":
                if (input.Value.Length is < 1 or > 10000) throw new ArgumentException("Enter 1–10000 characters.");
                await page.Keyboard.InsertTextAsync(input.Value); break;
            case "key":
                if (!new[] { "Enter", "Tab", "Shift+Tab", "Backspace", "Delete", "Escape", "ArrowLeft", "ArrowRight", "ArrowUp", "ArrowDown", "Home", "End", "Control+a" }.Contains(input.Value))
                    throw new ArgumentException("Keyboard shortcut is not supported.");
                await page.Keyboard.PressAsync(input.Value); break;
            case "scroll":
                if (!float.IsFinite(input.Y) || Math.Abs(input.Y) > 2000) throw new ArgumentException("Invalid scroll distance.");
                await page.Mouse.WheelAsync(0, input.Y); break;
            default: throw new ArgumentException("Unknown browser control action.");
        }
    }

    public sealed record BrowserControlRequest(string Action, string Value = "", int Tab = 0, float X = 0, float Y = 0);
}
