using System.Text.RegularExpressions;
using JobFlow.Domain;
using Microsoft.EntityFrameworkCore;

namespace JobFlow.Infrastructure;

internal static class ApprovedApplicationDuplicates
{
    internal static async Task<Dictionary<Guid, string>> CheckAsync(JobFlowDb db, Guid candidateId, CancellationToken ct)
    {
        var applications = await (from app in db.Applications.AsNoTracking()
            join vacancy in db.Vacancies.AsNoTracking() on app.VacancyId equals vacancy.Id
            where app.CandidateProfileId == candidateId && !app.IsTest &&
                (app.State == ApplicationState.ReadyToSubmit || app.State == ApplicationState.Submitted ||
                 app.State == ApplicationState.EmployerResponded || app.State == ApplicationState.Submitting ||
                 app.State == ApplicationState.SubmissionUnknown || app.State == ApplicationState.Closed)
            select new { App = app, Vacancy = vacancy }).ToListAsync(ct);
        var ready = applications.Where(x => x.App.State == ApplicationState.ReadyToSubmit &&
            !x.Vacancy.IsArchived && x.Vacancy.Availability != "Closed").ToArray();
        if (ready.Length == 0) return [];

        var ids = applications.Select(x => x.App.Id).ToArray();
        var aliases = (await db.Audit.AsNoTracking().Where(x => ids.Contains(x.EntityId) &&
            x.Action == ExternalApplicationDraft.PlatformReferralAction).Select(x => new { x.EntityId, x.Details }).ToListAsync(ct))
            .ToLookup(x => x.EntityId, x => x.Details);
        var attempts = (await db.Set<ApplicationAttempt>().AsNoTracking().Where(x => ids.Contains(x.JobApplicationId))
            .Select(x => x.JobApplicationId).ToListAsync(ct)).ToHashSet();
        var history = await (from entry in db.Set<HistoricalApplication>().AsNoTracking()
            join vacancy in db.Vacancies.AsNoTracking() on entry.VacancyId equals vacancy.Id
            where entry.CandidateProfileId == candidateId
            select vacancy.Url).ToListAsync(ct);
        var imported = history.Select(Identity).OfType<string>().ToHashSet(StringComparer.Ordinal);
        var identities = applications.ToDictionary(x => x.App.Id, x => aliases[x.App.Id].Append(Identity(x.Vacancy.Url))
            .OfType<string>().ToHashSet(StringComparer.Ordinal));
        var checkedApplications = applications.Where(x => x.App.State != ApplicationState.ReadyToSubmit ||
            (!x.Vacancy.IsArchived && x.Vacancy.Availability != "Closed")).ToArray();
        var results = new Dictionary<Guid, string>();
        foreach (var item in ready)
        {
            if (attempts.Contains(item.App.Id) || identities[item.App.Id].Overlaps(imported))
            {
                results[item.App.Id] = "Проверка дублей: для этой вакансии уже есть попытка отправки или запись в импортированной истории.";
                continue;
            }
            var exact = checkedApplications.FirstOrDefault(other => other.App.Id != item.App.Id &&
                identities[item.App.Id].Overlaps(identities[other.App.Id]));
            if (exact != null)
            {
                results[item.App.Id] = "Проверка дублей: другая запись относится к той же вакансии (" + exact.Vacancy.Title + ").";
                continue;
            }
            var employer = Normalize(item.Vacancy.Employer);
            var title = Normalize(item.Vacancy.Title);
            if (employer.Length < 2 || title.Length < 4) continue;
            var possible = checkedApplications.FirstOrDefault(other => other.App.Id != item.App.Id &&
                other.Vacancy.JobPlatformId != item.Vacancy.JobPlatformId &&
                Normalize(other.Vacancy.Employer) == employer && Normalize(other.Vacancy.Title) == title);
            if (possible != null)
                results[item.App.Id] = "Возможный дубль: у того же работодателя есть отклик с таким же названием на другой площадке. Сверьте ссылки и выберите одну запись.";
        }
        return results;

        static string? Identity(string url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
            uri.Scheme is "http" or "https" ? VacancyIdentity.CanonicalUrl(url) : null;
        static string Normalize(string value) => Regex.Replace(value.Trim().ToLowerInvariant(), @"[^\p{L}\p{N}+#]+", " ").Trim();
    }
}
