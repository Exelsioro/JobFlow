using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using JobFlow.Application;
using JobFlow.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Playwright;

namespace JobFlow.Infrastructure;

public sealed partial class BrowserWorkflowDriver(BrowserSessions sessions, IAgentExecutor agents, JobFlowDb db, BrowserWorkflowOptions? options = null,
    IExternalApplicationAddressResolver? externalResolver = null, IExternalApplicationDocumentLoader? externalDocuments = null) : IBrowserWorkflowDriver
{
    private readonly BrowserWorkflowOptions settings = Validated(options ?? new());
    private static BrowserWorkflowOptions Validated(BrowserWorkflowOptions options) { options.Validate(); return options; }
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private Guid? workflowToken;
    public async Task<IAsyncDisposable> BeginRunAsync(WorkflowDefinition workflow, CancellationToken ct)
    {
        var lease = await sessions.BeginWorkflowAsync(workflow.BrowserProfileId!.Value, ct);
        workflowToken = lease.Token;
        return new RunLease(lease, () => workflowToken = null);
    }
    private sealed class RunLease(IAsyncDisposable lease, Action release) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() { try { await lease.DisposeAsync(); } finally { release(); } }
    }
    private static readonly Regex Confirmation = new(@"application (?:has been )?(?:submitted|received)|thank you for applying|successfully applied|отклик (?:успешно )?отправлен|вы откликнулись|заявка (?:успешно )?отправлена", RegexOptions.IgnoreCase);
    private static readonly Regex AlreadyApplied = new(@"already applied|уже откликались|вы уже откликнулись", RegexOptions.IgnoreCase);

    public async Task<IReadOnlyList<Vacancy>> DiscoverAsync(WorkflowDefinition workflow, JobPlatform platform, CandidateProfile candidate, CancellationToken ct)
    {
        await using var lease = await sessions.AcquireAsync(workflow.BrowserProfileId!.Value, ct, platform.BrowserMode, workflowToken);
        var page = lease.Page;
        return await WithDiagnosticsAsync<IReadOnlyList<Vacancy>>(workflow, "search", page, async () =>
        {
            await using var guard = await PageGuard.CreateAsync(page, platform.Url);
            await NavigateAsync(page, platform.Url, ct);
            var collected = new Dictionary<string, Vacancy>(StringComparer.Ordinal);
            var recovery = new RecoveryState();
            var memory = new BrowserAgentMemory();
            for (var step = 0; step < workflow.MaxBrowserSteps; step++)
            {
                ct.ThrowIfCancellationRequested();
                var observed = await ObserveAsync(page, guard, ct);
                observed = observed with { Snapshot = observed.Snapshot with { LastActionError = recovery.LastError } };
                memory.Observe(Fingerprint(observed.Snapshot) + ":" + collected.Count);
                var action = await DecideForPageAsync(workflow, "search", page, guard, observed, new
                {
                    query = platform.SearchFilters, candidate, allowedHost = new Uri(platform.Url).Host, limit = workflow.MaxVacanciesPerPlatform, remainingSteps = workflow.MaxBrowserSteps - step,
                    currentUtc = DateTime.UtcNow, workflow.FreshnessDays, workflow.RulesJson, workflow.Instructions,
                    collected = collected.Values.Select(x => new { x.Title, x.Url }).ToArray(),
                    task = "Discover plausible matches for later evaluation, including vacancies with uncertain requirements. Search this platform for the candidate's preferred roles. Use the publication date filter and newest-first ordering. Skip closed, archived and old vacancies. Respect FreshnessDays. For each excluded candidate, put its title, observed URL and specific exclusion reason in action.reason. Links outside allowedHost and its subdomains cannot be opened or collected; skip them and explain why. Open results as needed and collect up to limit vacancy links. Finish when the limit is reached, results are exhausted, or remainingSteps <= 2; do not open another vacancy on the last step. Do not apply during search."
                }, memory, ct);
                if (action.Action == "visual") { recovery.LastError = ""; continue; }
                Trace(workflow, "search", action, observed.Snapshot);
                if (observed.Snapshot.ActiveDialog.Length > 0 && action.Action is "collect" or "finish")
                    throw new InvalidOperationException("A browser dialog is still open. Handle it or request manual attention before finishing the search.");
                if (action.Action is "collect" or "finish")
                {
                    var links = observed.Snapshot.Controls.Select(x => x.Href).Append(page.Url).ToHashSet(StringComparer.Ordinal);
                    foreach (var item in action.Vacancies.Take(workflow.MaxVacanciesPerPlatform))
                    {
                        if (!guard.Allows(item.Url) || !links.Contains(item.Url) || string.IsNullOrWhiteSpace(item.Title))
                            throw new InvalidOperationException("Browser agent returned a vacancy link that was not observed on the platform.");
                        var key = VacancyIdentity.ExternalId(item.Url);
                        if (!collected.ContainsKey(key) && collected.Count >= workflow.MaxVacanciesPerPlatform) break;
                        collected.TryAdd(key, new Vacancy
                        {
                            JobPlatformId = platform.Id, ExternalId = VacancyIdentity.ExternalId(item.Url),
                            Url = item.Url, Title = item.Title[..Math.Min(500, item.Title.Length)],
                            Employer = item.Employer, Description = item.Description
                        });
                    }
                    await CheckpointAsync(workflow, platform, collected, ct);
                    memory.Record("search", action.Action, "vacancy links", page.Url, $"Collected {collected.Count} unique vacancies", action.Reason);
                    if (action.Action == "finish" || collected.Count >= workflow.MaxVacanciesPerPlatform)
                    {
                        await db.SaveChangesAsync(ct);
                        return collected.Values.Take(workflow.MaxVacanciesPerPlatform).ToArray();
                    }
                }
                else await ActWithRecoveryAsync(workflow, page, guard, observed, action, "search", null, recovery, memory, ct);
            }
            throw new InvalidOperationException("Browser search reached its step limit. Narrow the search or increase the workflow step limit.");
        }, ct);
    }

    public async Task ReadVacancyAsync(WorkflowDefinition workflow, JobPlatform platform, Vacancy vacancy, CancellationToken ct)
    {
        await using var lease = await sessions.AcquireAsync(workflow.BrowserProfileId!.Value, ct, platform.BrowserMode, workflowToken);
        await WithDiagnosticsAsync<bool>(workflow, "read", lease.Page, async () =>
        {
            await using var guard = await PageGuard.CreateAsync(lease.Page, platform.Url);
            if (!guard.Allows(vacancy.Url)) throw new InvalidOperationException("Vacancy URL is outside the selected platform.");
            await NavigateAsync(lease.Page, vacancy.Url, ct);
            var observed = await ObserveAsync(lease.Page, guard, ct);
            if (observed.Snapshot.Text.Length < 40) throw new InvalidOperationException("Vacancy page has no readable description. Check authorization in the browser profile.");
            vacancy.Description = observed.Snapshot.Text;
            if (workflow.FreshnessDays.HasValue || workflow.IgnorePublicationAge)
                await VerifyFreshnessAsync(workflow, lease.Page, vacancy, observed.Snapshot.Text, ct);
            return true;
        }, ct);
    }

    public async Task<BrowserResult> ApplyAsync(WorkflowDefinition workflow, JobPlatform platform, CandidateProfile candidate,
        Vacancy vacancy, JobApplication application, string resumePath, Func<Task> beforeSubmit, CancellationToken ct)
    {
        await using var lease = await sessions.AcquireAsync(workflow.BrowserProfileId!.Value, ct, platform.BrowserMode, workflowToken);
        var page = lease.Page;
        return await WithDiagnosticsAsync<BrowserResult>(workflow, "apply", page, async () =>
        {
            await using var guard = await PageGuard.CreateAsync(page, platform.Url);
            if (!guard.Allows(vacancy.Url)) throw new InvalidOperationException("Application URL is outside the selected platform.");
            await NavigateAsync(page, vacancy.Url, ct);
            if (workflow.FreshnessDays.HasValue)
            {
                var initial = await ObserveAsync(page, guard, ct);
                await VerifyFreshnessAsync(workflow, page, vacancy, initial.Snapshot.Text, ct);
            }
            var submitted = false;
            var beforeSubmissionText = "";
            var recovery = new RecoveryState();
            var memory = new BrowserAgentMemory();
            for (var step = 0; step < workflow.MaxBrowserSteps; step++)
            {
                ct.ThrowIfCancellationRequested();
                var observed = await ObserveAsync(page, guard, ct);
                observed = observed with { Snapshot = observed.Snapshot with { LastActionError = recovery.LastError } };
                memory.Observe(Fingerprint(observed.Snapshot));
                if (!submitted && AlreadyApplied.IsMatch(observed.Snapshot.Text))
                    throw new InvalidOperationException("The platform says this vacancy has already been applied to.");
                if (!submitted && IsHeadHunterVacancy(vacancy.Url, out _) && guard.Allows(page.Url))
                {
                    // The native submit phase opens a fresh page; the resume or its visibility may have changed since the probe.
                    try
                    {
                        if (HeadHunterBlocker(await ReadHeadHunterEntryStateAsync(page, ct)) is string reason)
                            throw new HumanReviewRequiredException(reason);
                    }
                    catch (PlaywrightException ex) when (!page.IsClosed && IsNavigationInterruption(ex)) { continue; }
                }
                if (submitted && FindConfirmation(observed.Snapshot.Text, beforeSubmissionText) is string receipt)
                    return new(true, page.Url + " | " + receipt);
                var phase = submitted ? "confirmation" : "apply";
                var action = await DecideForPageAsync(workflow, phase, page, guard, observed, new
                {
                    candidate, vacancy = new { vacancy.Title, vacancy.Employer, vacancy.Url },
                    application.CoverLetter, resume = Path.GetFileName(resumePath),
                    submissionAuthorized = !submitted && workflow.AutoSubmit && application.State == ApplicationState.ReadyToSubmit,
                    task = submitted ? "Read the application receipt. Do not interact with submission controls again." :
                        """
                        Prepare the application using the supplied facts, letter and resume. Stop for login, CAPTCHA or required answers not supported by candidate facts.
                        When submissionAuthorized is true, the application is already approved: the fact that a button actually sends this application is not a blocker.
                        For the final send control marked isSubmit or maySubmit, return action "submit", never "click". This includes a one-click Apply/Respond button when the observed page makes clear it sends this application immediately and no required data or answers are missing. An ordinary "click" cannot send and a blocked POST is not a submission.
                        Use "click" only for a control that opens a form or navigates. If the effect of a control is unclear or a required answer is missing, stop and name that specific uncertainty. Never infer an answer from approval alone.
                        """
                }, memory, ct);
                if (action.Action == "visual") { recovery.LastError = ""; continue; }
                Trace(workflow, phase, action, observed.Snapshot);
                if (submitted)
                {
                    if (action.Action == "confirmed" && action.Reason.Length > 0 &&
                        observed.Snapshot.Text.Contains(action.Reason, StringComparison.Ordinal) && !beforeSubmissionText.Contains(action.Reason, StringComparison.Ordinal) && Confirmation.IsMatch(action.Reason))
                        return new(true, page.Url + " | " + action.Reason);
                    if (action.Action != "wait") throw new InvalidOperationException("No verifiable receipt after submission. " + action.Reason);
                    await Task.Delay(750, ct);
                    memory.Record(phase, action.Action, "", page.Url, "Executed", action.Reason);
                    continue;
                }
                if (action.Action == "submit")
                {
                    if (workflow.FreshnessDays.HasValue && (vacancy.Availability != "Open" || vacancy.PublishedAt == null ||
                        vacancy.PublishedAt < DateTime.UtcNow.AddDays(-workflow.FreshnessDays.Value) || Closed.IsMatch(observed.Snapshot.Text)))
                        throw new InvalidOperationException("Vacancy is closed, stale or its freshness could not be verified. Submission stopped.");
                    var control = await GetControlAsync(page, guard, observed, action.Target, ct);
                    if (!control.Info.IsSubmit && !control.Info.MaySubmit) throw new InvalidOperationException("The agent did not choose a recognized application submission control.");
                    var invalid = await control.Locator.EvaluateAsync<string[]>("""
                        el => el.form ? Array.from(el.form.elements).filter(x => x.willValidate && !x.checkValidity())
                            .map(x => x.getAttribute('aria-label') || x.name || x.type) : []
                        """);
                    if (invalid.Length > 0) throw new InvalidOperationException("Required application fields are incomplete: " + string.Join(", ", invalid.Take(8)));
                    ct.ThrowIfCancellationRequested();
                    beforeSubmissionText = observed.Snapshot.Text;
                    await beforeSubmit();
                    submitted = true;
                    memory.SubmissionAttempted = true;
                    memory.Record(phase, action.Action, ControlLabel(control.Info), page.Url, "SubmissionStarted; never repeat", action.Reason);
                    guard.AllowWrites = true;
                    await control.Locator.ClickAsync();
                    await SettleAsync(page, ct);
                }
                else await ActWithRecoveryAsync(workflow, page, guard, observed, action, "apply", resumePath, recovery, memory, ct);
            }
            if (submitted) throw new InvalidOperationException("Submission was attempted, but no receipt appeared within the browser step limit.");
            throw new InvalidOperationException("Application preparation reached its browser step limit.");
        }, ct);
    }

    private async Task<BrowserAction> DecideAsync(WorkflowDefinition workflow, string phase, BrowserSnapshot snapshot, object task, BrowserAgentMemory memory, CancellationToken ct)
    {
        var result = await agents.ExecuteAsync(new(workflow.BrowserAgentId!.Value,
            JsonSerializer.Serialize(new { phase, task, page = snapshot, memory, workflow.Instructions }, Json),
            "Perform exactly one browser action for phase " + phase + ". Follow the task and supplied workflow instructions within the candidate dossier. Never invent facts or repeat a submission. " +
            (phase == "search" ? VacancyAssessmentPolicy.Search + "\n" : "") +
            "When activeDialog is present, handle that dialog first using its observed controls; stop for login, CAPTCHA or unsupported answers. " +
            "During search, page.visitedPages contains previously observed GET pages that you may navigate back to, including the results list. Use their exact URLs; never invent a return URL. " +
            "Custom dropdowns may be div/span/combobox controls. Click the dropdown, then click its observed option or label. Use select only for native SELECT elements. Search filter checkboxes/radios may use check; never enable subscriptions or marketing. " +
            "Use memory.recentActions and pageChanged to track what already happened. Memory contains historical actions, not current target IDs. Do not redo completed filter steps or reopen a closed panel just to repeat a missing button. " +
            "Executed means only that the browser action returned, not that the task succeeded. If memory.warning is present, change strategy or return blocked with a useful reason. " +
            "When lastActionError is present, the previous action failed: inspect the NEW snapshot and choose a different valid action or wait. Do not blindly repeat the failed action. Never force clicks or remove overlays."), ct);
        using var document = JsonDocument.Parse(result.Output);
        if (!global::Json.Schema.JsonSchema.FromText(BrowserAgentTemplate.Schema).Evaluate(document.RootElement).IsValid)
            throw new InvalidOperationException("Browser agent output does not match the browser action schema. Apply the BrowserAutomation template.");
        return JsonSerializer.Deserialize<BrowserAction>(result.Output, Json)!;
    }

    private sealed class RecoveryState
    {
        public int Failures { get; set; }
        public string LastError { get; set; } = "";
    }
    private sealed class BrowserInteractionException(string message) : InvalidOperationException(message);

    private async Task CheckpointAsync(WorkflowDefinition workflow, JobPlatform platform, Dictionary<string, Vacancy> found, CancellationToken ct)
    {
        foreach (var entry in found.ToArray())
        {
            var vacancy = entry.Value;
            if (db.Entry(vacancy).State != EntityState.Detached) continue;
            var existing = await db.Vacancies.SingleOrDefaultAsync(x => x.JobPlatformId == platform.Id && x.ExternalId == vacancy.ExternalId, ct);
            if (existing == null) db.Add(vacancy);
            else
            {
                existing.Title = vacancy.Title; existing.Url = vacancy.Url;
                if (vacancy.Employer.Length > 0) existing.Employer = vacancy.Employer;
                if (vacancy.Description.Length > 0) existing.Description = vacancy.Description;
                found[entry.Key] = existing;
            }
        }
        db.Audit.Add(new AuditEvent { EntityId = workflow.Id, EntityType = nameof(WorkflowDefinition), Action = "Browser/search/Checkpoint",
            Details = JsonSerializer.Serialize(new { count = found.Count, platformId = platform.Id, browserMode = sessions.GetMode(workflow.BrowserProfileId ?? Guid.Empty),
                vacancies = found.Values.Select(x => new { x.Id, x.ExternalId, x.Title, x.Url }).ToArray() }, Json) });
        await db.SaveChangesAsync(ct);
    }

    private async Task ActWithRecoveryAsync(WorkflowDefinition workflow, IPage page, PageGuard guard, ObservedPage observed,
        BrowserAction action, string phase, string? resume, RecoveryState recovery, BrowserAgentMemory memory, CancellationToken ct, bool readOnly = false)
    {
        var info = observed.Snapshot.Controls.SingleOrDefault(x => x.Id == action.Target);
        var safeToRecover = action.Action is "navigate" or "fill" or "select" or "check" ||
            action.Action == "click" && (readOnly || info == null || !info.IsSubmit && !info.MaySubmit ||
                phase == "search" && Regex.IsMatch(info.Label, "search|найти|поиск", RegexOptions.IgnoreCase));
        try
        {
            await ActAsync(page, guard, observed, action, phase, resume, ct);
            memory.Record(phase, action.Action, info == null ? action.Target : ControlLabel(info), page.Url, "Executed", action.Reason);
            db.Audit.Add(new AuditEvent { EntityId = workflow.Id, EntityType = nameof(WorkflowDefinition), Action = "Browser/" + phase + "/ActionCompleted",
                Details = JsonSerializer.Serialize(new { action = action.Action, browserMode = sessions.GetMode(workflow.BrowserProfileId ?? Guid.Empty), requestedUrl = action.Action == "navigate" ? action.Url : "", actualUrl = page.Url }, Json) });
            recovery.LastError = "";
        }
        catch (Exception ex) when (!ct.IsCancellationRequested && safeToRecover &&
            ex is TimeoutException or PlaywrightException or BrowserInteractionException)
        {
            var error = $"{action.Action} ({(info == null ? action.Target : ControlLabel(info))}) failed: {ex.Message}";
            recovery.LastError = error[..Math.Min(1800, error.Length)];
            memory.Record(phase, action.Action, info == null ? action.Target : ControlLabel(info), page.Url, "Failed", action.Reason, recovery.LastError);
            db.Audit.Add(new AuditEvent { EntityId = workflow.Id, EntityType = nameof(WorkflowDefinition),
                Action = "Browser/" + phase + "/ActionFailed", Details = JsonSerializer.Serialize(new {
                    action = action.Action, browserMode = sessions.GetMode(workflow.BrowserProfileId ?? Guid.Empty), requestedUrl = action.Action == "navigate" ? action.Url : "", actualUrl = page.Url, error = recovery.LastError }, Json) });
            await db.SaveChangesAsync(ct);
            if (recovery.Failures++ >= settings.ActionRecoveryLimit)
                throw new InvalidOperationException($"Browser action recovery limit ({settings.ActionRecoveryLimit}) exhausted. {recovery.LastError}", ex);
        }
    }

    private async Task<T> WithDiagnosticsAsync<T>(WorkflowDefinition workflow, string phase, IPage page, Func<Task<T>> operation, CancellationToken ct)
    {
        page.SetDefaultTimeout(settings.ActionTimeoutSeconds * 1000);
        try { return await operation(); }
        catch (VacancyUnavailableException) { throw; }
        catch (ModelProviderException) { throw; }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            var message = ex.Message[..Math.Min(1400, ex.Message.Length)];
            var id = Guid.NewGuid();
            var directory = Path.Combine(Storage.Root, "browser-diagnostics", id.ToString("D"));
            var saved = false;
            try
            {
                Directory.CreateDirectory(directory);
                var screenshot = false;
                string text = "";
                JsonElement? controls = null;
                if (!page.IsClosed)
                {
                    try
                    {
                        await page.ScreenshotAsync(new() { Path = Path.Combine(directory, "screen.jpg"), Type = ScreenshotType.Jpeg,
                            Quality = 65, Timeout = 3000, Mask = new[] { page.Locator("input[type=password]") } });
                        screenshot = true;
                    }
                    catch (Exception captureError) when (captureError is PlaywrightException or TimeoutException) { }
                    // Password/input values and raw HTML are not retained.
                    try { text = await page.Locator("body").InnerTextAsync(new() { Timeout = 1500 }); }
                    catch (Exception captureError) when (captureError is PlaywrightException or TimeoutException) { }
                    try
                    {
                        controls = await page.EvaluateAsync<JsonElement>("""
                            () => Array.from(document.querySelectorAll('[data-jobflow-agent-id]')).slice(0,250).map(el=>({
                              id:el.getAttribute('data-jobflow-agent-id'),tag:el.tagName,role:el.getAttribute('role'),qa:el.getAttribute('data-qa'),
                              label:(el.getAttribute('aria-label')||el.innerText||el.name||'').trim().slice(0,180),
                              expanded:el.getAttribute('aria-expanded'),popup:el.getAttribute('aria-haspopup'),href:el.href||''}))
                            """);
                    }
                    catch (Exception captureError) when (captureError is PlaywrightException or TimeoutException) { }
                }
                await File.WriteAllTextAsync(Path.Combine(directory, "failure.json"), JsonSerializer.Serialize(new
                {
                    id, workflowId = workflow.Id, phase, createdAt = DateTime.UtcNow, url = page.Url,
                    error = message, text = text[..Math.Min(16000, text.Length)], controls, screenshot,
                    screenshotUrl = screenshot ? $"/api/browser-diagnostics/{id}/screen" : null
                }, Json), CancellationToken.None);
                saved = true;
            }
            catch (Exception captureError) when (captureError is IOException or UnauthorizedAccessException or PlaywrightException or TimeoutException) { }
            var details = $"Browser {phase}: {message}" + (saved ? $" Diagnostics: /api/browser-diagnostics/{id}" : " (Diagnostics could not be saved.)");
            db.Audit.Add(new AuditEvent { EntityId = workflow.Id, EntityType = nameof(WorkflowDefinition), Action = "Browser/" + phase + "/Failed", Details = details });
            await db.SaveChangesAsync(CancellationToken.None);
            if (ex is HumanReviewRequiredException) throw new HumanReviewRequiredException(details);
            throw new InvalidOperationException(details, ex);
        }
    }

    private async Task ActAsync(IPage page, PageGuard guard, ObservedPage observed, BrowserAction action, string phase, string? resume, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (action.Action == "blocked") throw new HumanReviewRequiredException("Browser needs attention: " + action.Reason);
        if (action.Action == "wait") { await Task.Delay(750, ct); return; }
        if (action.Action == "navigate")
        {
            if (!guard.Allows(action.Url))
                throw new InvalidOperationException("Browser navigation rejected: requested URL is outside the selected platform or uses a forbidden scheme.");
            if (!observed.Snapshot.Controls.Any(x => x.Href == action.Url) && !(phase == "search" && guard.HasVisited(action.Url)))
                throw new BrowserInteractionException("Browser navigation rejected: requested URL is neither an observed link nor a previously visited search GET page. Use page.visitedPages to return to results.");
            await NavigateAsync(page, action.Url, ct); return;
        }
        var control = await GetControlAsync(page, guard, observed, action.Target, ct);
        switch (action.Action)
        {
            case "click":
                if (control.Info.IsSubmit && !(phase == "search" && Regex.IsMatch(control.Info.Label, "search|найти|поиск", RegexOptions.IgnoreCase)))
                    throw new InvalidOperationException("Final application submission requires the submit action.");
                if (phase == "search" && control.Info.IsSubmit)
                {
                    var actionUrl = await control.Locator.EvaluateAsync<string>("el => el.form?.action || ''");
                    if (!guard.Allows(actionUrl)) throw new InvalidOperationException("Search form action is outside the selected platform.");
                    guard.SearchPostUrl = actionUrl;
                    try { await control.Locator.ClickAsync(); await SettleAsync(page, ct); }
                    finally { guard.SearchPostUrl = ""; }
                    return;
                }
                await control.Locator.ClickAsync(); break;
            case "fill":
                if (control.Info.InputType is "password" or "file" or "hidden") throw new InvalidOperationException("This input cannot be filled by the browser agent.");
                await control.Locator.FillAsync(action.Value); break;
            case "select": await control.Locator.SelectOptionAsync(new[] { action.Value }); break;
            case "check":
                if (phase is not ("apply" or "search") || control.Info.InputType is not ("checkbox" or "radio")) throw new InvalidOperationException("Check requires a native checkbox or radio in search or application preparation.");
                await control.Locator.SetCheckedAsync(action.Value != "false"); break;
            case "upload":
                if (phase != "apply" || resume == null || control.Info.InputType != "file") throw new InvalidOperationException("Only the workflow resume can be uploaded to a file field.");
                guard.AllowWrites = true;
                try { await control.Locator.SetInputFilesAsync(resume); await Task.Delay(350, ct); }
                finally { guard.AllowWrites = false; }
                break;
            default: throw new InvalidOperationException("Browser action is not valid in this workflow phase.");
        }
        await SettleAsync(page, ct);
    }

    private static async Task<(BrowserControl Info, ILocator Locator)> GetControlAsync(IPage page, PageGuard guard, ObservedPage observed, string target, CancellationToken ct)
    {
        var info = observed.Snapshot.Controls.SingleOrDefault(x => x.Id == target);
        if (info == null || !observed.Targets.TryGetValue(target, out var locator))
            throw new BrowserInteractionException("The browser agent selected a stale or unknown control. Use the refreshed snapshot.");
        if (page.Url != observed.Snapshot.Url)
            throw new BrowserInteractionException("The page URL changed while the agent was deciding. Inspect the refreshed page before acting.");
        // Query immediately, without waiting for an obsolete ID to reappear. A separate
        // Count + ElementHandle waiter can race a rerender and fail on a slow machine.
        var originals = await locator.ElementHandlesAsync();
        try
        {
            var fresh = await ObserveAsync(page, guard, ct);
            var matches = fresh.Snapshot.Controls.Where(x => x.Role == info.Role && x.InputType == info.InputType &&
                x.Label == info.Label && x.Href == info.Href && x.Name == info.Name && x.TestId == info.TestId &&
                x.IsSubmit == info.IsSubmit && x.MaySubmit == info.MaySubmit && x.Expanded == info.Expanded).ToArray();
            if (originals.Count == 1)
                foreach (var match in matches)
                    if (await fresh.Targets[match.Id].EvaluateAsync<bool>("(el, previous) => el === previous", originals[0]))
                        return (match, fresh.Targets[match.Id]);
            if (matches.Length == 1 && (info.Label.Length > 0 || info.TestId.Length > 0 || info.Name.Length > 0 || info.Href.Length > 0))
                return (matches[0], fresh.Targets[matches[0].Id]);
            throw new BrowserInteractionException($"Target {ControlLabel(info)} changed, disappeared or became ambiguous while the agent was deciding. No click was performed. Inspect the refreshed snapshot and previous action outcomes.");
        }
        finally { foreach (var handle in originals) await handle.DisposeAsync(); }
    }

    private static string ControlLabel(BrowserControl info) => string.IsNullOrWhiteSpace(info.Label) ? $"{info.Role}/{info.InputType} {info.TestId} {info.Name}".Trim() : info.Label;
    private static string Fingerprint(BrowserSnapshot snapshot) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        JsonSerializer.Serialize(new { snapshot.Url, snapshot.ActiveDialog, snapshot.Text, snapshot.Viewport, controls = snapshot.Controls.Select(x => new { x.Label, x.Role, x.InputType, x.Href, x.Value, x.Checked, x.Disabled }) }, Json))));

    private async Task NavigateAsync(IPage page, string url, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await page.GotoAsync(url, new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = settings.NavigationTimeoutSeconds * 1000 }).WaitAsync(ct);
                break;
            }
            catch (TimeoutException) when (!ct.IsCancellationRequested && attempt < settings.NavigationRetries)
            {
                // Retry only this GET navigation, never a form submission or workflow.
                // A timed-out navigation can still be loading. Stop it before the next GET,
                // otherwise its eventual response may abort the new navigation.
                var cdp = await page.Context.NewCDPSessionAsync(page);
                try { await cdp.SendAsync("Page.stopLoading").WaitAsync(ct); }
                finally { await cdp.DetachAsync(); }
                await Task.Delay(500, ct);
            }
        }
        await SettleAsync(page, ct);
    }

    private static async Task SettleAsync(IPage page, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try { await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded, new() { Timeout = 3000 }); }
        catch (TimeoutException) { }
        await Task.Delay(200, ct);
    }

    private static async Task<ObservedPage> ObserveAsync(IPage page, PageGuard guard, CancellationToken ct)
    {
        for (var retries = 0; ; retries++)
        {
            try { return await CapturePageAsync(page, guard, ct); }
            catch (Exception ex) when (retries < 2 && !ct.IsCancellationRequested && !page.IsClosed &&
                (ex is SnapshotNavigationException || ex is PlaywrightException playwrightError && IsNavigationInterruption(playwrightError)))
            {
                // Discard the incomplete snapshot. Re-read the new document without replaying the action.
                await SettleAsync(page, ct);
            }
        }
    }

    private static bool IsNavigationInterruption(PlaywrightException ex) =>
        ex.Message.Contains("Execution context was destroyed", StringComparison.OrdinalIgnoreCase) ||
        ex.Message.Contains("Frame was detached", StringComparison.OrdinalIgnoreCase) ||
        ex.Message.Contains("Frame has been detached", StringComparison.OrdinalIgnoreCase) ||
        ex.Message.Contains("Cannot find context with specified id", StringComparison.OrdinalIgnoreCase);

    private sealed class SnapshotNavigationException() : InvalidOperationException("The page changed during observation.");

    private static async Task<ObservedPage> CapturePageAsync(IPage page, PageGuard guard, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        await guard.WaitForReadyAsync(ct);
        var url = page.Url;
        if (!guard.Allows(url)) throw new InvalidOperationException("Browser left the selected platform. Log in manually and restart the workflow.");
        var controls = new List<BrowserControl>();
        var targets = new Dictionary<string, ILocator>();
        var text = new StringBuilder();
        var activeDialog = "";
        foreach (var frame in page.Frames.Where(x => guard.Allows(x.Url)).OrderBy(x => x == page.MainFrame ? 0 : 1).Take(10))
        {
            // A main-frame modal also covers background child frames.
            if (frame != page.MainFrame && activeDialog.Length > 0) continue;
            var prefix = Guid.NewGuid().ToString("N")[..8];
            var serialized = await frame.EvaluateAsync<string>("""
                prefix => {
                  const visible = el => !!el.getClientRects().length && getComputedStyle(el).visibility !== 'hidden' && getComputedStyle(el).display !== 'none';
                  const dialogs = Array.from(document.querySelectorAll('dialog[open],[role=dialog],[aria-modal=true],[data-qa=modal-overlay]')).filter(visible);
                  const dialog = dialogs.reverse().sort((a,b) => (parseInt(getComputedStyle(b).zIndex)||0)-(parseInt(getComputedStyle(a).zIndex)||0))[0];
                  const scope = dialog || document.body;
                  const reachable = el => {
                    const r=el.getBoundingClientRect();
                    if(r.bottom<=0 || r.top>=innerHeight || r.right<=0 || r.left>=innerWidth) return true;
                    const x=Math.max(0,Math.min(innerWidth-1,(Math.max(0,r.left)+Math.min(innerWidth,r.right))/2));
                    const y=Math.max(0,Math.min(innerHeight-1,(Math.max(0,r.top)+Math.min(innerHeight,r.bottom))/2));
                    const hit=document.elementFromPoint(x,y);
                    return !!hit && (hit===el || el.contains(hit));
                  };
                  const core = 'a[href],button,input,textarea,select,[role=button],[role=checkbox],[role=radio],[role=combobox],[role=listbox],[role=option],[role=menuitem],[role=menuitemradio],[aria-haspopup],[tabindex]:not([tabindex="-1"]),[contenteditable=true],label';
                  const accepted = el => visible(el) && reachable(el) && !['hidden','password'].includes(el.type) &&
                    (el.tagName !== 'LABEL' || ['checkbox','radio'].includes(el.control?.type));
                  const candidates = new Set(Array.from(scope?.querySelectorAll(core) || []).filter(accepted));
                  // Some sites expose clickable dropdowns as plain DIV/SPAN with a pointer
                  // cursor, without roles or tabindex. Keep one short, outermost target.
                  for (const el of scope?.querySelectorAll('div,span') || []) {
                    const label=(el.innerText||'').trim(),rect=el.getBoundingClientRect();
                    if(!label || label.length>160 || rect.bottom<=0 || rect.top>=innerHeight || rect.right<=0 || rect.left>=innerWidth ||
                      getComputedStyle(el).cursor!=='pointer' || el.closest(core) || el.querySelector(core) || !accepted(el)) continue;
                    let target=el;
                    while(target.parentElement && target.parentElement!==scope && getComputedStyle(target.parentElement).cursor==='pointer' &&
                      (target.parentElement.innerText||'').trim()===label && !target.parentElement.matches(core)) target=target.parentElement;
                    candidates.add(target);
                  }
                  const priority = el => {
                    const r=el.getBoundingClientRect(),onScreen=r.bottom>0&&r.top<innerHeight&&r.right>0&&r.left<innerWidth;
                    return (onScreen?0:20)+(/option|menuitem/.test(el.getAttribute('role')||'')?0:el.hasAttribute('aria-haspopup')?1:el.tagName==='A'?5:3);
                  };
                  const elements=Array.from(candidates).sort((a,b)=>priority(a)-priority(b)).slice(0,250);
                  const Controls = elements.map((el,i) => {
                    const Id = prefix+'-'+i; el.setAttribute('data-jobflow-agent-id', Id);
                    const labelledBy=(el.getAttribute('aria-labelledby')||'').split(/\s+/).map(id=>document.getElementById(id)?.textContent||'').join(' ').trim();
                    const Label = (el.getAttribute('aria-label') || labelledBy || Array.from(el.labels || [],x=>x.textContent).join(' ') || el.innerText || el.placeholder || el.name || '').trim().slice(0,350);
                    const InputType = el.type || el.tagName.toLowerCase();
                    const MaySubmit = ['BUTTON','INPUT','A'].includes(el.tagName) && /apply|send|submit|отклик|отправ|candidat|postul|solicitar|bewerb|aplik/i.test(Label);
                    const IsSubmit = ((InputType === 'submit' || InputType === 'image') && !!el.form) || (el.tagName === 'BUTTON' && !!el.form && MaySubmit);
                    return {Id, Label, Name:el.name || '', TestId:el.getAttribute('data-qa') || el.getAttribute('data-testid') || '', Role:el.getAttribute('role') || el.tagName.toLowerCase(), InputType, IsSubmit, MaySubmit,
                      Expanded:el.hasAttribute('aria-expanded')?el.getAttribute('aria-expanded')==='true':null, Popup:el.getAttribute('aria-haspopup')||'',
                      Checked:el.type === 'checkbox' || el.type === 'radio' ? el.checked : null, Required:!!el.required, Disabled:!!el.disabled,
                      Href:el.href || '', Value:el.value == null ? '' : String(el.value).slice(0,500),
                      Options:el.tagName === 'SELECT' ? Array.from(el.options,x=>({Value:x.value,Label:x.label})).slice(0,100) : []};
                  });
                  return JSON.stringify({Text:(scope?.innerText || '').slice(0,40000), Controls,
                    ActiveDialog:dialog ? (dialog.getAttribute('aria-label') || dialog.innerText || 'Modal dialog is open').slice(0,2000) : ''});
                }
                """, prefix);
            var part = JsonSerializer.Deserialize<SnapshotPart>(serialized, Json)!;
            text.AppendLine(part.Text);
            if (frame == page.MainFrame) activeDialog = part.ActiveDialog;
            foreach (var item in part.Controls)
            {
                controls.Add(item); targets[item.Id] = frame.Locator("[data-jobflow-agent-id='" + item.Id + "']");
            }
        }
        var title = await page.TitleAsync();
        var viewport = await page.EvaluateAsync<int[]>("() => [innerWidth, innerHeight, scrollX, scrollY]");
        if (page.Url != url) throw new SnapshotNavigationException();
        guard.RememberPage(url, title);
        return new(new(url, title, text.ToString()[..Math.Min(60000, text.Length)], controls.ToArray(), guard.LastBlocked, activeDialog, "",
            guard.VisitedPages.Where(x => x.Url != url).Reverse().ToArray()) { Viewport = viewport }, targets);
    }

    private static string? FindConfirmation(string text, string before)
    {
        foreach (Match match in Confirmation.Matches(text))
            if (!before.Contains(match.Value, StringComparison.OrdinalIgnoreCase))
                return text.Substring(Math.Max(0, match.Index - 30), Math.Min(250, text.Length - Math.Max(0, match.Index - 30))).Trim();
        return null;
    }

    private static readonly Regex Closed = new(@"no\s+longer\s+accepting\s+applications|position\s+(?:has\s+been\s+)?filled|job\s+(?:has\s+)?expired|вакансия\s+(?:находится\s+)?в\s+архиве|вакансия\s+закрыта|при[её]м\s+откликов\s+заверш[её]н", RegexOptions.IgnoreCase);
    public static bool IsClosed(string text) => Closed.IsMatch(text);
    private const string FreshnessSchema = """{"type":"object","properties":{"publishedAt":{"type":"string"},"publicationEvidence":{"type":"string"},"availability":{"type":"string","enum":["Open","Closed","Unknown"]},"availabilityEvidence":{"type":"string"}},"required":["publishedAt","publicationEvidence","availability","availabilityEvidence"],"additionalProperties":false}""";
    public static IReadOnlyDictionary<string, string> FreshnessEvidenceChoices(string evidence, IEnumerable<string> actionLabels)
    {
        var statements = evidence.Split('\n').Where(line => Regex.IsMatch(line, @"(?i)отклик|закрыт|архив|принима|accept|closed|archiv|expired|filled|\bapply\b"));
        return actionLabels.Concat(statements).Where(x => !string.IsNullOrWhiteSpace(x)).Select(EvidenceText.Normalize)
            .Where(x => x.Length is > 0 and <= 240 && EvidenceText.Contains(evidence, x) &&
                !Regex.IsMatch(x, "^\\s*[\\{\\[]|^\\s*\"[^\"]+\"\\s*:"))
            .Distinct().Take(120).Select((quote, index) => (Id: "e" + (index + 1), Quote: quote))
            .ToDictionary(x => x.Id, x => x.Quote);
    }
    public static string FreshnessSchemaFor(string evidence, IEnumerable<string> actionLabels)
    {
        var schema = JsonNode.Parse(FreshnessSchema)!;
        schema["properties"]!["availabilityEvidence"]!["enum"] = JsonSerializer.SerializeToNode(FreshnessEvidenceChoices(evidence, actionLabels).Keys.Prepend(""));
        return schema.ToJsonString();
    }
    private async Task VerifyFreshnessAsync(WorkflowDefinition workflow, IPage page, Vacancy vacancy, string text, CancellationToken ct)
    {
        var structured = await page.Locator("script[type='application/ld+json']").AllTextContentsAsync();
        var evidence = text + "\nStructured JobPosting data:\n" + string.Join("\n", structured).Substring(0, Math.Min(20000, string.Join("\n", structured).Length));
        vacancy.VerifiedAt = DateTime.UtcNow;
        if (IsClosed(text)) { vacancy.Availability = "Closed"; throw new VacancyUnavailableException("closed", "Вакансия закрыта или перенесена работодателем в архив."); }
        var sources = PublicationDates.Sources(text, structured, page.Url, DateTime.UtcNow);
        if (PublicationDates.Conflicting(sources))
        {
            vacancy.Availability = "Unknown";
            vacancy.PublishedAt = null;
            vacancy.PublicationEvidence = JsonSerializer.Serialize(sources, Json);
            db.Audit.Add(new AuditEvent { EntityId = vacancy.Id, EntityType = nameof(Vacancy), Action = "PublicationDateConflict",
                Details = JsonSerializer.Serialize(new { workflowId = workflow.Id, vacancy.Url, workflow.FreshnessDays, sources }, Json) });
            await db.SaveChangesAsync(ct);
            throw new HumanReviewRequiredException("Даты публикации противоречат друг другу: " + string.Join("; ", sources.Select(x => $"{x.Source}: {x.CalendarDate:yyyy-MM-dd}")) + ". Нужно уточнение.");
        }
        var actionLabels = await page.Locator("button,a,[role=button]").AllInnerTextsAsync();
        var availabilityEvidenceChoices = FreshnessEvidenceChoices(text, actionLabels);
        var outputSchema = FreshnessSchemaFor(text, actionLabels);
        var response = await agents.ExecuteAsync(new(workflow.BrowserAgentId!.Value, JsonSerializer.Serialize(new { currentUtc = DateTime.UtcNow, url = page.Url, workflow.FreshnessDays, publicationSources = sources, evidence, availabilityEvidenceChoices }, Json),
            "Verify this specific job's publication date and whether it accepts applications. Use datePosted, a clearly labelled publication date, and visible Apply controls or explicit open status. Do not use another job's date, copyright year, dateModified, updated date or crawl time. Relative publication dates may be resolved against currentUtc. Return ISO UTC publishedAt only if supported, else empty. publicationEvidence must be an exact nonempty quote from evidence. For availabilityEvidence return ONLY the ID (e1, e2, etc.) of one entry in availabilityEvidenceChoices that supports the status, never its text; use empty if none supports the status. Structured directApply=false means an external application process, not a closed vacancy. Unknown facts must remain Unknown. validThrough before currentUtc means Closed.", outputSchema), ct);
        await CoverLetterService.RetainOutputAsync(db, response, ct);
        using var doc = JsonDocument.Parse(response.Output);
        if (!global::Json.Schema.JsonSchema.FromText(outputSchema).Evaluate(doc.RootElement).IsValid) throw new HumanReviewRequiredException("Агент не выбрал точное доказательство статуса со страницы вакансии.");
        var root = doc.RootElement;
        var quote = root.GetProperty("publicationEvidence").GetString()!;
        var statusQuote = availabilityEvidenceChoices.GetValueOrDefault(root.GetProperty("availabilityEvidence").GetString()!) ?? "";
        var modelAvailability = root.GetProperty("availability").GetString()!;
        var hasStatusEvidence = EvidenceText.Contains(evidence, statusQuote);
        var confirmsClosure = hasStatusEvidence && IsClosed(statusQuote);
        vacancy.Availability = (modelAvailability, hasStatusEvidence, confirmsClosure) switch
        {
            ("Closed", true, true) => "Closed",
            ("Open", true, false) => "Open",
            _ => "Unknown"
        };
        var resolved = ResolvePublicationDate(quote, DateTime.UtcNow);
        var modelPublishedAt = root.GetProperty("publishedAt").GetString();
        var authoritative = sources.FirstOrDefault(x => x.Source == "JobPosting.datePosted" && x.PublishedAtUtc.HasValue);
        vacancy.PublishedAt = authoritative?.PublishedAtUtc ?? (DateTimeOffset.TryParse(modelPublishedAt, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal, out var date) && resolved.HasValue && resolved.Value.Date == date.UtcDateTime.Date &&
            EvidenceText.Contains(evidence, quote) ? date.UtcDateTime : null);
        if (authoritative != null) quote = authoritative.Evidence;
        vacancy.PublicationEvidence = quote;
        var validationFailures = new List<string>();
        if (vacancy.PublishedAt == null) validationFailures.Add("No supported publication date; model date or its exact evidence quote could not be verified.");
        if (vacancy.Availability != "Open") validationFailures.Add("Vacancy is not confirmed open: " + vacancy.Availability);
        if (!hasStatusEvidence) validationFailures.Add("Open-status evidence is not an exact nonempty source quote.");
        if (vacancy.PublishedAt > DateTime.UtcNow.AddMinutes(5)) validationFailures.Add("Publication date is in the future.");
        var stale = !workflow.IgnorePublicationAge && workflow.FreshnessDays is int ageLimit &&
            vacancy.PublishedAt < DateTime.UtcNow.AddDays(-ageLimit);
        if (stale) validationFailures.Add("Publication date exceeds the configured age limit.");
        db.Audit.Add(new AuditEvent { EntityId = vacancy.Id, EntityType = nameof(Vacancy), Action = "VacancyFreshnessChecked",
            Details = JsonSerializer.Serialize(new { workflowId = workflow.Id, vacancy.Url, workflow.FreshnessDays, workflow.IgnorePublicationAge, vacancy.Availability, vacancy.PublishedAt,
                publicationEvidence = quote, dateSource = authoritative?.Source ?? "Agent evidence", modelPublishedAt, modelAvailability,
                statusEvidence = statusQuote, sources, validationFailures, response.ExecutionId }, Json) });
        if (vacancy.Availability == "Closed")
            throw new VacancyUnavailableException("closed", "Вакансия больше не принимает отклики.");
        if (stale)
            throw new VacancyUnavailableException("stale", $"Вакансия опубликована {vacancy.PublishedAt:dd.MM.yyyy}; лимит актуальности — {workflow.FreshnessDays} дн.");
        if (validationFailures.Count > 0)
            throw new HumanReviewRequiredException("Не удалось подтвердить актуальность или открытый статус вакансии. " + string.Join(" ", validationFailures));
    }
    public static DateTime? ResolvePublicationDate(string evidence, DateTime now) => PublicationDates.Resolve(evidence, now);

    private void Trace(WorkflowDefinition workflow, string phase, BrowserAction action, BrowserSnapshot snapshot) =>
        db.Audit.Add(new AuditEvent { EntityId = workflow.Id, EntityType = nameof(WorkflowDefinition),
            Action = "Browser/" + phase + "/" + action.Action, Details = JsonSerializer.Serialize(new
            {
                snapshot.Url, action.Target, browserMode = sessions.GetMode(workflow.BrowserProfileId ?? Guid.Empty), requestedUrl = action.Action == "navigate" ? action.Url : "", label = snapshot.Controls.FirstOrDefault(x => x.Id == action.Target) is { } control ? ControlLabel(control) : "",
                reason = action.Reason[..Math.Min(2000, action.Reason.Length)]
            }, Json) });

    private sealed record BrowserAction(string Action, string Target, string Value, string Url, string Reason, BrowserVacancy[] Vacancies);
    private sealed record BrowserVacancy(string Title, string Employer, string Url, string Description);
    private sealed record BrowserOption(string Value, string Label);
    private sealed record BrowserControl(string Id, string Label, string Name, string TestId, string Role, string InputType, bool IsSubmit, bool MaySubmit, bool? Expanded, string Popup, bool? Checked, bool Required, bool Disabled, string Href, string Value, BrowserOption[] Options);
    private sealed record SnapshotPart(string Text, BrowserControl[] Controls, string ActiveDialog);
    private sealed record BrowserSnapshot(string Url, string Title, string Text, BrowserControl[] Controls, string LastBlockedRequest, string ActiveDialog, string LastActionError, VisitedPage[] VisitedPages)
    {
        public int[] Viewport { get; init; } = [];
    }
    private sealed record VisitedPage(string Url, string Title);
    private sealed record ObservedPage(BrowserSnapshot Snapshot, Dictionary<string, ILocator> Targets);

    private sealed class PageGuard(IPage page, Uri platform) : IAsyncDisposable
    {
        public bool AllowWrites { get; set; }
        public string SearchPostUrl { get; set; } = "";
        public string LastBlocked { get; private set; } = "";
        public List<VisitedPage> VisitedPages { get; } = [];
        private string documentMethod = "";
        private readonly System.Collections.Concurrent.ConcurrentDictionary<IRequest, byte> pending = new();
        private void OnRequest(object? sender, IRequest request)
        {
            if (request.ResourceType is "document" or "fetch" or "xhr" && Allows(request.Url)) pending.TryAdd(request, 0);
        }
        private void OnRequestDone(object? sender, IRequest request) => pending.TryRemove(request, out _);
        public async Task WaitForReadyAsync(CancellationToken ct)
        {
            var deadline = DateTime.UtcNow.AddSeconds(4);
            string? previous = null;
            while (DateTime.UtcNow < deadline)
            {
                ct.ThrowIfCancellationRequested();
                if (page.IsClosed || !Allows(page.Url)) return;
                var state = await page.EvaluateAsync<string>("""
                    () => document.readyState==='loading'||Array.from(document.querySelectorAll('[aria-busy=true],[role=progressbar]')).some(el=>el.getClientRects().length&&getComputedStyle(el).visibility!=='hidden')?'':location.href+'\n'+(document.body?.innerText||'').slice(0,60000)
                    """);
                if (pending.IsEmpty && state.Length > 0 && state == previous) return;
                previous = pending.IsEmpty ? state : null;
                await Task.Delay(300, ct);
            }
        }
        private void OnResponse(object? sender, IResponse response)
        {
            if (response.Request.IsNavigationRequest && response.Request.Frame == page.MainFrame)
                documentMethod = response.Request.Method;
        }
        public void RememberPage(string url, string title)
        {
            if (documentMethod != "GET" || !Allows(url)) return;
            VisitedPages.RemoveAll(x => x.Url == url);
            VisitedPages.Add(new(url, title[..Math.Min(180, title.Length)]));
            if (VisitedPages.Count > 20) VisitedPages.RemoveAt(0);
        }
        public bool HasVisited(string url) => VisitedPages.Any(x => x.Url == url);
        private Func<IRoute, Task>? handler;
        private static string Host(Uri uri) => uri.IdnHost.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? uri.IdnHost[4..] : uri.IdnHost;
        public bool Allows(string url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
            uri.UserInfo.Length == 0 && (uri.Scheme == "https" || platform.Scheme == "http" && platform.IsLoopback && uri.Scheme == "http") &&
            (Host(uri).Equals(Host(platform), StringComparison.OrdinalIgnoreCase) || Host(uri).EndsWith("." + Host(platform), StringComparison.OrdinalIgnoreCase)) &&
            (platform.IsDefaultPort ? uri.IsDefaultPort : uri.Port == platform.Port);

        public static async Task<PageGuard> CreateAsync(IPage page, string url)
        {
            var guard = new PageGuard(page, new Uri(url));
            guard.handler = async route =>
            {
                var readPost = route.Request.Method == "POST" && guard.Allows(route.Request.Url) &&
                    (guard.SearchPostUrl == route.Request.Url || IsReadQuery(route.Request.PostData));
                if (route.Request.IsNavigationRequest && !guard.Allows(route.Request.Url) ||
                    route.Request.Method is not ("GET" or "HEAD" or "OPTIONS") && (!guard.AllowWrites && !readPost || !guard.Allows(route.Request.Url)))
                {
                    guard.LastBlocked = "Blocked navigation or write outside the current browser phase: " + route.Request.Method;
                    await route.AbortAsync();
                }
                else await route.FallbackAsync();
            };
            await page.RouteAsync("**/*", guard.handler);
            page.Response += guard.OnResponse;
            page.Request += guard.OnRequest;
            page.RequestFinished += guard.OnRequestDone;
            page.RequestFailed += guard.OnRequestDone;
            return guard;
        }
        private static bool IsReadQuery(string? body)
        {
            if (body == null || body.Length > 100000) return false;
            try
            {
                using var json = JsonDocument.Parse(body);
                return json.RootElement.ValueKind == JsonValueKind.Object && json.RootElement.TryGetProperty("query", out var query) &&
                    query.ValueKind == JsonValueKind.String && Regex.IsMatch(query.GetString()!.TrimStart(), @"^(query(?:\s|\(|\{)|\{)", RegexOptions.CultureInvariant);
            }
            catch (JsonException) { return false; }
        }
        public async ValueTask DisposeAsync()
        {
            page.Response -= OnResponse; page.Request -= OnRequest; page.RequestFinished -= OnRequestDone; page.RequestFailed -= OnRequestDone;
            if (!page.IsClosed && handler != null) await page.UnrouteAsync("**/*", handler);
        }
    }
}

public static class VacancyIdentity
{
    public static string CanonicalUrl(string value)
    {
        var uri = new Uri(value, UriKind.Absolute);
        var host = uri.IdnHost.ToLowerInvariant();
        var hhVacancy = Regex.IsMatch(uri.AbsolutePath, "^/vacancy/[0-9]+/?$");
        if (host == "www.hh.ru" || hhVacancy && host.EndsWith(".hh.ru", StringComparison.Ordinal)) host = "hh.ru";
        if (host == "boards.greenhouse.io") host = "job-boards.greenhouse.io";
        if (host.EndsWith(".linkedin.com", StringComparison.Ordinal) && uri.AbsolutePath.StartsWith("/jobs/view/", StringComparison.Ordinal))
            host = "linkedin.com";
        var query = string.Join("&", uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Where(x => !x.StartsWith("utm_", StringComparison.OrdinalIgnoreCase) && !x.StartsWith("gh_src=", StringComparison.OrdinalIgnoreCase) && !x.StartsWith("from=", StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.Ordinal));
        if (hhVacancy && host is "hh.ru" or "rabota.by") query = "";
        if (host == "linkedin.com" && uri.AbsolutePath.StartsWith("/jobs/view/", StringComparison.Ordinal)) query = "";
        return host + (uri.IsDefaultPort ? "" : ":" + uri.Port) + uri.AbsolutePath.TrimEnd('/') + (query.Length == 0 ? "" : "?" + query);
    }
    public static string ExternalId(string url)
    {
        var uri = new Uri(url);
        var match = Regex.Match(uri.AbsolutePath, @"/(?:vacancy|jobs)/([0-9]+)(?:/|$)");
        return match.Success ? match.Groups[1].Value : "url-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(CanonicalUrl(url))));
    }
    public static string SubmissionKey(Guid profileId, string url) =>
        "browser-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(profileId + ":" + CanonicalUrl(url))));
}
