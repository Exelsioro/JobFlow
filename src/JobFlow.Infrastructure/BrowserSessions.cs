using System.Collections.Concurrent;
using Microsoft.Playwright;
namespace JobFlow.Infrastructure;
public sealed class BrowserSessions(bool headless = false) : IAsyncDisposable
{
    private sealed record Session(IBrowserContext Context, string Mode, FileStream Lease);
    private readonly ConcurrentDictionary<Guid, Session> contexts = new();
    private readonly Dictionary<Guid, Cookie[]> switchingCookies = new();
    private readonly ConcurrentDictionary<Guid, IPage> drafts = new();
    private readonly ConcurrentDictionary<Guid, IAsyncDisposable> retainedDrafts = new();
    private readonly SemaphoreSlim gate = new(1);
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> automationGates = new();
    private readonly ConcurrentDictionary<Guid, Guid> workflowOwners = new();
    private IPlaywright? playwright;
    private int disposed;
    public bool IsBusy(Guid id) => contexts.ContainsKey(id);
    public string? GetMode(Guid id) => contexts.TryGetValue(id, out var session) ? session.Mode : null;
    public bool IsAutomating(Guid id) => workflowOwners.ContainsKey(id) || automationGates.TryGetValue(id, out var value) && value.CurrentCount == 0;
    public IPage GetManualPage(Guid id, int tab = 0)
    {
        if (!contexts.TryGetValue(id, out var session)) throw new InvalidOperationException("Open the browser profile first.");
        if (IsAutomating(id)) throw new InvalidOperationException("Stop the workflow before controlling its browser manually.");
        var context = session.Context;
        if (tab < 0 || tab >= context.Pages.Count) throw new ArgumentException("Browser tab is unavailable; refresh the browser view.");
        return context.Pages[tab];
    }
    public async Task<object[]> GetTabsAsync(Guid id)
    {
        if (!contexts.TryGetValue(id, out var session)) throw new InvalidOperationException("Open the browser profile first.");
        var pages = session.Context.Pages.Select((page, index) => (page, index)).Where(x => !x.page.IsClosed);
        return await Task.WhenAll(pages.Select(async x =>
        {
            var title = "";
            // A loading or closing renderer must not keep the tab list from returning.
            try { title = await x.page.TitleAsync().WaitAsync(TimeSpan.FromSeconds(2)); }
            catch (Exception ex) when (ex is TimeoutException or PlaywrightException) { }
            return (object)new { x.index, url = x.page.Url, title };
        }));
    }

    public async Task<AutomationLease> AcquireAsync(Guid id, CancellationToken ct, string browserMode = "inherit", Guid? workflowToken = null)
    {
        var profileGate = automationGates.GetOrAdd(id, _ => new SemaphoreSlim(1));
        if (!await profileGate.WaitAsync(0, ct)) throw new InvalidOperationException("Browser profile is being used by another workflow.");
        try
        {
            if (workflowOwners.TryGetValue(id, out var owner) && owner != workflowToken || workflowToken.HasValue && !workflowOwners.ContainsKey(id))
                throw new InvalidOperationException("Browser profile belongs to another workflow run.");
            var context = await ContextAsync(id, browserMode);
            var page = await context.NewPageAsync();
            page.SetDefaultTimeout(8000);
            return new(page, profileGate, () => drafts.Any(x => ReferenceEquals(x.Value, page) && retainedDrafts.ContainsKey(x.Key)));
        }
        catch { profileGate.Release(); throw; }
    }

    public sealed class AutomationLease(IPage page, SemaphoreSlim profileGate, Func<bool>? retained = null) : IAsyncDisposable
    {
        public IPage Page => page;
        public async ValueTask DisposeAsync()
        {
            try { if (!page.IsClosed && retained?.Invoke() != true) await page.CloseAsync(); }
            finally { profileGate.Release(); }
        }
    }
    public async Task OpenAsync(Guid id, string browserMode = "inherit")
    {
        var profileGate = await EnterManualAsync(id);
        try
        {
            var context = await ContextAsync(id, browserMode);
            if (context.Pages.Count == 0) await context.NewPageAsync();
            else await context.Pages[0].BringToFrontAsync();
        }
        finally { profileGate.Release(); }
    }

    public async Task<IPage> OpenDraftAsync(Guid applicationId, Guid browserId, string url, string browserMode = "inherit")
    {
        var profileGate = await EnterManualAsync(browserId);
        try
        {
            var context = await ContextAsync(browserId, browserMode);
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
        finally { profileGate.Release(); }
    }

    public IPage GetDraftPage(Guid applicationId)
    {
        if (!drafts.TryGetValue(applicationId, out var page) || page.IsClosed)
            throw new InvalidOperationException("Open the draft browser tab first.");
        var profile = contexts.FirstOrDefault(x => ReferenceEquals(x.Value.Context, page.Context)).Key;
        if (IsAutomating(profile)) throw new InvalidOperationException("Stop the workflow before controlling its browser manually.");
        return page;
    }

    public bool HasDraft(Guid applicationId) => drafts.TryGetValue(applicationId, out var page) && !page.IsClosed;

    public async Task RetainDraftAsync(Guid applicationId, IPage page, IAsyncDisposable protection)
    {
        if (retainedDrafts.TryRemove(applicationId, out var previous)) await previous.DisposeAsync();
        if (drafts.TryGetValue(applicationId, out var oldPage) && oldPage != page && !oldPage.IsClosed) await oldPage.CloseAsync();
        drafts[applicationId] = page;
        retainedDrafts[applicationId] = protection;
        page.Close += async (_, _) =>
        {
            if (drafts.TryRemove(new KeyValuePair<Guid, IPage>(applicationId, page)) && retainedDrafts.TryRemove(applicationId, out var guard))
                await guard.DisposeAsync();
        };
    }

    public async Task<(Guid BrowserId, int Tab)> ReleaseDraftAsync(Guid applicationId)
    {
        var page = GetDraftPage(applicationId);
        var profile = contexts.Single(x => ReferenceEquals(x.Value.Context, page.Context)).Key;
        var profileGate = await EnterManualAsync(profile);
        try
        {
            if (retainedDrafts.TryRemove(applicationId, out var protection)) await protection.DisposeAsync();
            return (profile, page.Context.Pages.ToList().IndexOf(page));
        }
        finally { profileGate.Release(); }
    }

    public static bool SupportsMode(string? mode) => mode is "inherit" or "headed" or "headless" or "new-headless";

    private async Task<SemaphoreSlim> EnterManualAsync(Guid id)
    {
        var profileGate = automationGates.GetOrAdd(id, _ => new SemaphoreSlim(1));
        if (!await profileGate.WaitAsync(0)) throw new InvalidOperationException("Stop the workflow before changing its browser session.");
        if (workflowOwners.ContainsKey(id)) { profileGate.Release(); throw new InvalidOperationException("Stop the workflow before changing its browser session."); }
        return profileGate;
    }

    public async Task<WorkflowLease> BeginWorkflowAsync(Guid id, CancellationToken ct)
    {
        var profileGate = automationGates.GetOrAdd(id, _ => new SemaphoreSlim(1));
        if (!await profileGate.WaitAsync(0, ct)) throw new InvalidOperationException("Browser profile is being used by another workflow.");
        try
        {
            var token = Guid.NewGuid();
            if (!workflowOwners.TryAdd(id, token)) throw new InvalidOperationException("Browser profile belongs to another workflow run.");
            return new(this, id, token);
        }
        finally { profileGate.Release(); }
    }

    public sealed class WorkflowLease(BrowserSessions sessions, Guid id, Guid token) : IAsyncDisposable
    {
        public Guid Token => token;
        private int released;
        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref released, 1) != 0) return;
            var profileGate = sessions.automationGates[id];
            await profileGate.WaitAsync();
            try
            {
                await sessions.gate.WaitAsync();
                try
                {
                    if (sessions.contexts.TryGetValue(id, out var session))
                    {
                        var retained = sessions.drafts.Where(x => sessions.retainedDrafts.ContainsKey(x.Key) && !x.Value.IsClosed &&
                            ReferenceEquals(x.Value.Context, session.Context)).Select(x => x.Value).ToHashSet();
                        if (retained.Count > 0)
                        {
                            foreach (var page in session.Context.Pages.Where(x => !retained.Contains(x)).ToArray()) await page.CloseAsync();
                        }
                        else
                        {
                            try { await sessions.RememberCookiesAsync(id, session); }
                            finally { await sessions.CloseSessionAsync(id, session); }
                        }
                    }
                }
                finally { sessions.gate.Release(); }
            }
            finally { sessions.workflowOwners.TryRemove(new KeyValuePair<Guid, Guid>(id, token)); profileGate.Release(); }
        }
    }

    private async Task RememberCookiesAsync(Guid id, Session session) =>
        switchingCookies[id] = (await session.Context.CookiesAsync()).Select(cookie => new Cookie
        {
            Name = cookie.Name, Value = cookie.Value, Domain = cookie.Domain, Path = cookie.Path,
            Expires = cookie.Expires, HttpOnly = cookie.HttpOnly, Secure = cookie.Secure,
            SameSite = cookie.SameSite, PartitionKey = cookie.PartitionKey
        }).ToArray();

    private async Task<IBrowserContext> ContextAsync(Guid id, string browserMode)
    {
        if (!SupportsMode(browserMode)) throw new ArgumentException("Unsupported browser mode.");
        var mode = browserMode == "inherit" ? headless ? "headless" : "headed" : browserMode;
        await gate.WaitAsync();
        try
        {
            if (contexts.TryGetValue(id, out var existing))
            {
                if (existing.Mode == mode) return existing.Context;
                if (drafts.Any(x => retainedDrafts.ContainsKey(x.Key) && !x.Value.IsClosed && ReferenceEquals(x.Value.Context, existing.Context)))
                    return existing.Context; // Preserve filled review forms; remote browser controls work in either mode.
                await RememberCookiesAsync(id, existing);
                await CloseSessionAsync(id, existing);
            }
            playwright ??= await Playwright.CreateAsync();
            var directory = Path.Combine(Storage.Root, "browser-profiles", id.ToString("N"));
            Directory.CreateDirectory(directory);
            // Exclusive lock protects the same profile across multiple processes.
            var lease = new FileStream(Path.Combine(directory, ".jobflow-lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            try
            {
                var context = await playwright.Chromium.LaunchPersistentContextAsync(directory, new()
                {
                    Headless = mode != "headed", Channel = mode == "new-headless" ? "chromium" : null,
                    ServiceWorkers = ServiceWorkerPolicy.Block
                });
                var session = new Session(context, mode, lease);
                contexts[id] = session;
                context.Close += (_, _) => { contexts.TryRemove(new KeyValuePair<Guid, Session>(id, session)); lease.Dispose(); };
                try
                {
                    if (switchingCookies.TryGetValue(id, out var cookies)) await context.AddCookiesAsync(cookies);
                    switchingCookies.Remove(id);
                }
                catch { await CloseSessionAsync(id, session); throw; }
                return context;
            }
            catch { lease.Dispose(); throw; }
        }
        finally { gate.Release(); }
    }
    public async Task CloseAsync(Guid id)
    {
        var profileGate = await EnterManualAsync(id);
        try
        {
            await gate.WaitAsync();
            try { if (contexts.TryGetValue(id, out var session)) await CloseSessionAsync(id, session); switchingCookies.Remove(id); }
            finally { gate.Release(); }
        }
        finally { profileGate.Release(); }
    }
    private async Task CloseSessionAsync(Guid id, Session session)
    {
        await session.Context.CloseAsync();
        contexts.TryRemove(new KeyValuePair<Guid, Session>(id, session));
        session.Lease.Dispose();
    }
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        foreach (var id in contexts.Keys)
            if (contexts.TryGetValue(id, out var session)) await CloseSessionAsync(id, session);
        switchingCookies.Clear();
        playwright?.Dispose(); gate.Dispose();
    }
}
