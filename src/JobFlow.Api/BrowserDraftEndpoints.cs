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
        app.MapPost("/api/applications/{id:guid}/browser-draft/open", async
            (Guid id, JobFlowDb db, BrowserSessions sessions, CancellationToken ct) =>
        {
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
                await sessions.OpenDraftAsync(id, profiles[0], url.AbsoluteUri);
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
}
