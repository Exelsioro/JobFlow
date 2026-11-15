using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using JobFlow.Application;
using JobFlow.Domain;

namespace JobFlow.Infrastructure;

public sealed class GreenhouseDiscovery(HttpClient http) : IJobDiscoveryProvider
{
    public async Task<IReadOnlyList<Vacancy>> DiscoverAsync(JobPlatform platform, CancellationToken ct)
    {
        var board = platform.SearchFilters.Trim();
        if (!Regex.IsMatch(board, "^[A-Za-z0-9_-]{1,100}$"))
            throw new ArgumentException("Enter the Greenhouse board token.");
        using var response = await http.GetAsync(
            $"https://boards-api.greenhouse.io/v1/boards/{board}/jobs?content=true", HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Greenhouse returned HTTP {(int)response.StatusCode}.");
        await response.Content.LoadIntoBufferAsync(10 * 1024 * 1024, ct);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(ct));
        if (!json.RootElement.TryGetProperty("jobs", out var jobs) || jobs.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("Greenhouse did not return a job board.");

        var vacancies = new List<Vacancy>();
        foreach (var job in jobs.EnumerateArray())
        {
            if (!job.TryGetProperty("id", out var id) || !id.TryGetInt64(out var number) || number <= 0 ||
                !job.TryGetProperty("title", out var title) || string.IsNullOrWhiteSpace(title.GetString()))
                continue;
            var externalId = number.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var html = job.TryGetProperty("content", out var content) ? content.GetString() ?? "" : "";
            var location = job.TryGetProperty("location", out var area) && area.ValueKind == JsonValueKind.Object &&
                           area.TryGetProperty("name", out var name) ? name.GetString() ?? "" : "";
            vacancies.Add(new Vacancy
            {
                JobPlatformId = platform.Id, ExternalId = externalId, Title = title.GetString()!,
                Employer = platform.Name, Location = location,
                Url = $"https://job-boards.greenhouse.io/{board}/jobs/{externalId}",
                Description = Regex.Replace(WebUtility.HtmlDecode(html), "<[^>]*>", " ",
                    RegexOptions.NonBacktracking, TimeSpan.FromSeconds(1)).Trim()
            });
        }
        return vacancies;
    }
}
