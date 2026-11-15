using System.Collections.Concurrent;
using Microsoft.Playwright;
namespace JobFlow.Infrastructure;
public sealed class BrowserSessions : IAsyncDisposable
{
    private readonly ConcurrentDictionary<Guid, IBrowserContext> contexts = new();
    private readonly ConcurrentDictionary<Guid, IPage> drafts = new();
    private readonly SemaphoreSlim gate = new(1);
    private IPlaywright? playwright;
    public bool IsBusy(Guid id) => contexts.ContainsKey(id);
    public async Task OpenAsync(Guid id)
    {
        var context = await ContextAsync(id, false);
        if (context.Pages.Count == 0) await context.NewPageAsync();
    }

    public async Task<IPage> OpenDraftAsync(Guid applicationId, Guid browserId, string url)
    {
        var context = await ContextAsync(browserId, true);
        var page = await context.NewPageAsync();
        try
        {
            await page.GotoAsync(url, new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 30000 });
            var oldPage = drafts.TryGetValue(applicationId, out var existing) ? existing : null;
            drafts[applicationId] = page;
            page.Close += (_, _) => drafts.TryRemove(new KeyValuePair<Guid, IPage>(applicationId, page));
            if (oldPage is { IsClosed: false }) await oldPage.CloseAsync();
            return page;
        }
        catch { await page.CloseAsync(); throw; }
    }

    public IPage GetDraftPage(Guid applicationId) =>
        drafts.TryGetValue(applicationId, out var page) && !page.IsClosed
            ? page
            : throw new InvalidOperationException("Open the draft browser tab first.");

    private async Task<IBrowserContext> ContextAsync(Guid id, bool reuse)
    {
        await gate.WaitAsync();
        try
        {
            if (contexts.TryGetValue(id, out var existing))
            {
                if (reuse) return existing;
                throw new InvalidOperationException("Browser profile is busy.");
            }
            playwright ??= await Playwright.CreateAsync();
            var directory = Path.Combine(Storage.Root, "browser-profiles", id.ToString("N"));
            Directory.CreateDirectory(directory);
            // Exclusive lock protects the same profile across multiple processes.
            var lease = new FileStream(Path.Combine(directory, ".jobflow-lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            try
            {
                var context = await playwright.Chromium.LaunchPersistentContextAsync(directory, new() { Headless = false });
                contexts[id] = context;
                context.Close += (_, _) => { contexts.TryRemove(id, out _); lease.Dispose(); };
                return context;
            }
            catch { lease.Dispose(); throw; }
        }
        finally { gate.Release(); }
    }
    public async Task CloseAsync(Guid id)
    {
        if (contexts.TryRemove(id, out var context)) await context.CloseAsync();
    }
    public async ValueTask DisposeAsync()
    {
        foreach (var id in contexts.Keys) await CloseAsync(id);
        playwright?.Dispose(); gate.Dispose();
    }
}
