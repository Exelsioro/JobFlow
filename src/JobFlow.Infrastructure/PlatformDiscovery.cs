using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using JobFlow.Application;
using JobFlow.Domain;
using Microsoft.EntityFrameworkCore;

namespace JobFlow.Infrastructure;

public sealed class PlatformDiscovery(JobFlowDb db, HttpClient http)
{
    public async Task<(int Discovered, int Added)> ImportAsync(JobPlatform platform, CancellationToken ct)
    {
        IJobDiscoveryProvider provider = platform.Adapter switch
        {
            "test-form" => new TestJobDiscoveryProvider(),
            "headhunter" => new HeadHunterDiscovery(http),
            "greenhouse" => new GreenhouseDiscovery(http),
            _ => throw new ArgumentException("This platform uses manual or journal import.")
        };
        var vacancies = await provider.DiscoverAsync(platform, ct);
        var added = 0;
        foreach (var vacancy in vacancies.DistinctBy(v => v.ExternalId))
        {
            if (await db.Vacancies.AnyAsync(v => v.JobPlatformId == platform.Id && v.ExternalId == vacancy.ExternalId, ct))
                continue;
            db.Add(vacancy);
            db.Audit.Add(new AuditEvent { EntityId = vacancy.Id, EntityType = nameof(Vacancy), Action = "Discovered" });
            added++;
        }
        await db.SaveChangesAsync(ct);
        return (vacancies.Count, added);
    }

    public static HttpClient CreateClient() => new(new HttpClientHandler { AllowAutoRedirect = false })
    {
        Timeout = TimeSpan.FromSeconds(30)
    };
}

public sealed class HeadHunterDiscovery(HttpClient http) : IJobDiscoveryProvider
{
    public async Task<IReadOnlyList<Vacancy>> DiscoverAsync(JobPlatform platform, CancellationToken ct)
    {
        var query = platform.SearchFilters.Trim();
        if (query.Length is < 1 or > 500)
            throw new ArgumentException("Enter a HeadHunter search query (1–500 characters).");
        using var request = new HttpRequestMessage(HttpMethod.Get,
            "https://api.hh.ru/vacancies?per_page=50&text=" + Uri.EscapeDataString(query));
        request.Headers.UserAgent.ParseAdd("JobFlow/1.0");
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"HeadHunter search returned HTTP {(int)response.StatusCode}.");
        await response.Content.LoadIntoBufferAsync(2 * 1024 * 1024, ct);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(ct));
        if (Property(json.RootElement, "items").ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("HeadHunter search did not return vacancies.");

        var vacancies = new List<Vacancy>();
        foreach (var item in json.RootElement.GetProperty("items").EnumerateArray().Take(50))
        {
            var id = Value(item, "id");
            var title = Value(item, "name");
            if (!Regex.IsMatch(id, "^[0-9]{1,24}$") || string.IsNullOrWhiteSpace(title))
                continue;
            var salary = Property(item, "salary");
            var upper = Property(salary, "to");
            var snippet = Property(item, "snippet");
            vacancies.Add(new Vacancy
            {
                JobPlatformId = platform.Id, ExternalId = id, Title = title,
                Url = "https://hh.ru/vacancy/" + id,
                Employer = Value(Property(item, "employer"), "name"),
                Location = Value(Property(item, "area"), "name"),
                Description = WebUtility.HtmlDecode(Regex.Replace(
                    Value(snippet, "requirement") + "\n" + Value(snippet, "responsibility"), "</?highlighttext>", "")),
                SalaryMax = upper.ValueKind == JsonValueKind.Number && upper.TryGetDouble(out var amount) ? amount : null,
                Currency = Value(salary, "currency"),
                PublishedAt = DateTimeOffset.TryParse(Value(item, "published_at"), out var published) ? published.UtcDateTime : null
            });
        }
        return vacancies;
    }

    private static JsonElement Property(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) ? value : default;

    private static string Value(JsonElement element, string name)
    {
        var property = Property(element, name);
        return property.ValueKind == JsonValueKind.String ? property.GetString() ?? "" : "";
    }
}
