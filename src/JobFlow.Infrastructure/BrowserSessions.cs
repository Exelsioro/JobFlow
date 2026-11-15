using System.Collections.Concurrent;
using Microsoft.Playwright;
namespace JobFlow.Infrastructure;
public sealed class BrowserSessions : IAsyncDisposable
{
    private readonly ConcurrentDictionary<Guid, IBrowserContext> contexts = new();
    private readonly SemaphoreSlim gate = new(1);
    private IPlaywright? playwright;
    public bool IsBusy(Guid id) => contexts.ContainsKey(id);
    public async Task OpenAsync(Guid id)
    {
        await gate.WaitAsync();
        try
        {
            if (contexts.ContainsKey(id)) throw new InvalidOperationException("Browser profile is busy.");
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
                await context.NewPageAsync();
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
