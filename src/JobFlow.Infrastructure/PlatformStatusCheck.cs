using System.Text.Json;
using JobFlow.Domain;
using Microsoft.EntityFrameworkCore;

namespace JobFlow.Infrastructure;

public sealed class PlatformStatusCheck(JobFlowDb db)
{
    public static Observation[] ReadFeed(JsonElement json) =>
        json.Deserialize<Observation[]>(new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new ArgumentException("Expected a status array.");

    public async Task<int> ApplyAsync(Integration source, Observation[] observations, CancellationToken ct)
    {
        if (observations.Length > 5000 || observations.Any(x => string.IsNullOrWhiteSpace(x.ExternalVacancyId) ||
            x.Status is not ("Pending" or "Viewed" or "Invitation" or "Rejection" or "Offer" or "Hired") ||
            x.ObservedAt == default || x.ObservedAt > DateTime.UtcNow.AddMinutes(5)))
            throw new ArgumentException("Invalid status response.");

        var latest = observations.GroupBy(x => x.ExternalVacancyId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(x => x.ObservedAt).First());
        var applications = await (from application in db.Applications
            join vacancy in db.Vacancies on application.VacancyId equals vacancy.Id
            where application.CandidateProfileId == source.CandidateProfileId &&
                  vacancy.JobPlatformId == source.JobPlatformId && !application.IsTest
            select new { App = application, vacancy.ExternalId }).ToListAsync(ct);
        var count = 0;
        foreach (var entry in applications)
        {
            if (!latest.TryGetValue(entry.ExternalId, out var status) || status.ObservedAt < entry.App.CreatedAt ||
                entry.App.ResponseStatusAt >= status.ObservedAt) continue;
            FeedbackProcessing.UpdateStatus(entry.App, status.Status, status.ObservedAt);
            db.Audit.Add(new AuditEvent
            {
                EntityId = entry.App.Id, EntityType = nameof(JobApplication),
                Action = "PlatformStatusObserved", Details = status.Status
            });
            count++;
        }
        return count;
    }

    public static IEnumerable<Observation> ReadHeadHunter(JsonElement json)
    {
        foreach (var item in json.GetProperty("items").EnumerateArray())
        {
            if (!item.TryGetProperty("vacancy", out var vacancy) || vacancy.ValueKind != JsonValueKind.Object ||
                vacancy.GetProperty("id").GetString() is not string id) continue;
            var viewed = item.TryGetProperty("viewed_by_opponent", out var flag) && flag.ValueKind == JsonValueKind.True;
            var status = item.GetProperty("state").GetProperty("id").GetString() switch
            {
                "response" => viewed ? "Viewed" : "Pending",
                "invitation" or "interview" => "Invitation",
                "discard" or "discard_after_interview" or "discard_after_offer" or "discard_by_employer" => "Rejection",
                "offer" => "Offer",
                "hired" => "Hired",
                _ => null
            };
            if (status != null && DateTimeOffset.TryParse(item.GetProperty("updated_at").GetString(), out var timestamp))
                yield return new(id, status, timestamp.UtcDateTime);
        }
    }

    public sealed record Observation(string ExternalVacancyId, string Status, DateTime ObservedAt);
}
