using System.Security.Cryptography;
using System.Text.Json;
using JobFlow.Domain;
using JobFlow.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Playwright;

namespace JobFlow.Api;

public static class BrowserDraftEndpoints
{
    public static void MapBrowserDraftEndpoints(this WebApplication app)
    {
        app.MapGet("/api/applications/{id:guid}/browser-draft/review", async (Guid id, JobFlowDb db, BrowserSessions sessions, HttpContext context, CancellationToken ct) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            var report = await ExternalApplicationDraft.ReadAsync(db, id, ct) ?? throw new InvalidOperationException("Внешняя форма для этого отклика ещё не подготовлена.");
            return Results.Ok(new { report, live = sessions.HasDraft(id), hasScreenshot = File.Exists(ExternalApplicationDraft.ScreenshotPath(id)) });
        });
        app.MapGet("/api/applications/{id:guid}/browser-draft/screen", (Guid id, HttpContext context) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            var path = ExternalApplicationDraft.ScreenshotPath(id);
            return File.Exists(path) ? Results.File(path, "image/jpeg") : Results.NotFound();
        });
        app.MapPost("/api/applications/{id:guid}/browser-draft/release", async
            (Guid id, ReleaseRequest input, JobFlowDb db, BrowserSessions sessions, CancellationToken ct) =>
        {
            var application = await db.Applications.SingleAsync(x => x.Id == id, ct);
            var report = await ExternalApplicationDraft.ReadAsync(db, id, ct) ?? throw new InvalidOperationException("Сначала подготовьте внешнюю форму.");
            if (!report.Verified || application.IsTest || application.State is not (ApplicationState.ReadyForReview or ApplicationState.ReadyToSubmit) ||
                input.Revision != application.Revision || input.Fingerprint != report.Fingerprint ||
                report.MaterialHash != ExternalApplicationDraft.Materials(application))
                throw new InvalidOperationException("Отклик или резюме изменились. Подготовьте и проверьте форму заново.");
            var page = sessions.GetDraftPage(id);
            var capture = await ExternalApplicationDraft.CaptureAsync(page);
            if (page.Url != report.Url || capture.Fingerprint != report.Fingerprint)
                throw new InvalidOperationException("Открытая форма изменилась после заполнения. Проверьте её вручную через браузерный профиль.");
            // This is a human takeover, not permission for the workflow to submit an external application.
            var browser = await sessions.ReleaseDraftAsync(id);
            if (application.State == ApplicationState.ReadyForReview) JobFlow.Application.Workflow.Transition(application, ApplicationState.ReadyToSubmit);
            db.Audit.Add(new AuditEvent { EntityId = id, EntityType = nameof(JobApplication), Action = "ExternalApplicationHumanTakeover",
                Details = JsonSerializer.Serialize(new { report.Url, report.Verified, report.Fingerprint }) });
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { browserId = browser.BrowserId, tab = browser.Tab, report.Url });
        });
        app.MapPost("/api/applications/{id:guid}/browser-draft/open", async
            (Guid id, JobFlowDb db, BrowserSessions sessions, CancellationToken ct) =>
        {
            if (await ExternalApplicationDraft.ReadAsync(db, id, ct) != null)
            {
                if (!sessions.HasDraft(id)) throw new InvalidOperationException("Заполненная вкладка закрыта. Сохранённый снимок доступен в проверке внешней формы; откройте сайт вручную через браузерный профиль.");
                return Results.Ok(new { opened = true });
            }
            var draft = await LoadAsync(id, db, ct);
            if (!Uri.TryCreate(draft.Vacancy.Url, UriKind.Absolute, out var url) ||
                url.Scheme != "https" || !string.IsNullOrEmpty(url.UserInfo))
                throw new ArgumentException("The vacancy needs an HTTPS URL.");
            var profiles = await db.Set<PlatformAccount>().Where(x => x.CandidateProfileId == draft.Application.CandidateProfileId &&
                x.JobPlatformId == draft.Vacancy.JobPlatformId && x.BrowserProfileId.HasValue)
                .Select(x => x.BrowserProfileId!.Value).Distinct().Take(2).ToListAsync(ct);
            if (profiles.Count != 1)
                throw new ArgumentException("Bind one browser profile to this candidate and platform in Platform Accounts.");
            await BrowserOperation(async () =>
            {
                await sessions.OpenDraftAsync(id, profiles[0], url.AbsoluteUri, "headed");
                return true;
            });
            return Results.Ok(new { opened = true });
        });

        app.MapPost("/api/applications/{id:guid}/browser-draft/inspect", async
            (Guid id, JobFlowDb db, BrowserSessions sessions, CancellationToken ct) =>
        {
            var draft = await LoadAsync(id, db, ct);
            var preview = await BrowserOperation(() => BrowserDraftFiller.InspectAsync(sessions.GetDraftPage(id)));
            var profile = await db.Profiles.AsNoTracking().SingleAsync(x => x.Id == draft.Application.CandidateProfileId, ct);
            return Results.Ok(new { preview.Url, preview.Forms, contactName = profile.Name, contactEmail = profile.Email, token = Fingerprint(preview, draft.Application.Revision) });
        });

        app.MapPost("/api/applications/{id:guid}/browser-draft/fill", async
            (Guid id, FillRequest input, JobFlowDb db, BrowserSessions sessions, CancellationToken ct) =>
        {
            var draft = await LoadAsync(id, db, ct);
            var profile = await db.Profiles.SingleAsync(x => x.Id == draft.Application.CandidateProfileId, ct);
            if (input.ContactName?.Length > 250) throw new ArgumentException("Contact name is too long.");
            var page = sessions.GetDraftPage(id);
            var filled = await BrowserOperation(async () =>
            {
                var preview = await BrowserDraftFiller.InspectAsync(page);
                if (input.Token != Fingerprint(preview, draft.Application.Revision))
                    throw new InvalidOperationException("The form or application changed. Inspect it again.");
                ct.ThrowIfCancellationRequested();
                return await BrowserDraftFiller.FillAsync(page, preview, input.FormIndex, new Dictionary<string, string>
                {
                    ["name"] = input.ContactName?.Trim() ?? "", ["email"] = profile.Email, ["letter"] = draft.Application.CoverLetter
                });
            });
            db.Audit.Add(new AuditEvent { EntityId = id, EntityType = nameof(JobApplication), Action = "BrowserDraftFilled", Details = $"Fields: {filled}" });
            await db.SaveChangesAsync(CancellationToken.None);
            return Results.Ok(new { filled });
        });
    }

    private static async Task<(JobApplication Application, Vacancy Vacancy)> LoadAsync(Guid id, JobFlowDb db, CancellationToken ct)
    {
        var application = await db.Applications.AsNoTracking().SingleAsync(x => x.Id == id, ct);
        if (application.IsTest || application.State != ApplicationState.ReadyToSubmit)
            throw new InvalidOperationException("Approve the real-platform draft before opening its browser form.");
        var vacancy = await db.Vacancies.AsNoTracking().SingleAsync(x => x.Id == application.VacancyId, ct);
        return (application, vacancy);
    }

    private static string Fingerprint(BrowserDraftFiller.Preview preview, int revision) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { preview, revision })));

    private static async Task<T> BrowserOperation<T>(Func<Task<T>> operation)
    {
        try { return await operation(); }
        catch (PlaywrightException) { throw new InvalidOperationException("The browser tab is unavailable or changed. Open or inspect the form again."); }
    }

    public sealed record FillRequest(string Token, int FormIndex, string? ContactName);
    public sealed record ReleaseRequest(int Revision, string Fingerprint);
}
