using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using JobFlow.Domain;
using Microsoft.EntityFrameworkCore;

namespace JobFlow.Infrastructure;

public sealed class SheetsWriteBack(JobFlowDb db, SecretStore secrets, HttpClient http)
{
    private static readonly SemaphoreSlim ExportLock = new(1);
    private const int MaxRows = 500;

    public async Task<SheetPreview> PreviewAsync(Guid id, string tab, CancellationToken ct, string? expected = null)
    {
        await ExportLock.WaitAsync(ct);
        try { return await PrepareAsync(id, tab, ct, expected); }
        finally { ExportLock.Release(); }
    }

    private async Task<SheetPreview> PrepareAsync(Guid id, string tab, CancellationToken ct, string? expected)
    {
        string[] header = [$"JobFlow {id:D}", "Vacancy ID", "Title", "Employer", "State", "Vacancy URL"];
        var integration = await db.Set<Integration>().SingleAsync(x => x.Id == id, ct);
        if (integration.Kind != "google-sheets" || !integration.Enabled || !integration.ImportConfirmed)
            throw new InvalidOperationException("Enable Sheets and confirm the initial journal import before exporting.");
        if (string.IsNullOrWhiteSpace(tab) || tab.Length > 60 || !Regex.IsMatch(tab, "^[A-Za-z0-9 _-]+$"))
            throw new ArgumentException("Enter a dedicated tab name (letters, digits, spaces, underscore or hyphen).");

        var endpoint = new Uri(integration.Endpoint);
        RemoteIntegrationService.ValidateEndpoint(integration.Kind, endpoint);
        var match = Regex.Match(endpoint.AbsolutePath, "^/v4/spreadsheets/([A-Za-z0-9_-]+)/values/[^/]+$");
        if (!match.Success) throw new ArgumentException("Invalid spreadsheet ID or import range.");
        var range = $"'{tab}'!A1:F{MaxRows + 1}";
        var target = new Uri($"https://sheets.googleapis.com/v4/spreadsheets/{match.Groups[1].Value}/values/{Uri.EscapeDataString(range)}");
        var headers = await new GoogleOAuth(secrets).HeadersAsync(integration, http, ct);
        var existing = await ReadAsync(target, headers, ct);
        if (existing.Length > 0 && !existing[0].SequenceEqual(header))
            throw new InvalidOperationException("Use an empty tab or a tab already exported by JobFlow.");

        var applications = await (from app in db.Applications.AsNoTracking()
                                  join vacancy in db.Vacancies.AsNoTracking() on app.VacancyId equals vacancy.Id
                                  where app.CandidateProfileId == integration.CandidateProfileId &&
                                        vacancy.JobPlatformId == integration.JobPlatformId && !app.IsTest
                                  orderby app.CreatedAt, app.Id
                                  select new { App = app, Vacancy = vacancy }).Take(MaxRows + 1).ToListAsync(ct);
        if (applications.Count > MaxRows) throw new InvalidOperationException("Export exceeds the 500-application tab limit.");
        string[][] rows = [header, .. applications.Select(x => new[]
        {
            x.App.Id.ToString(), x.Vacancy.ExternalId, x.Vacancy.Title, x.Vacancy.Employer, x.App.State.ToString(), x.Vacancy.Url
        })];

        var digest = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { id, target, existing, rows })));
        if (expected != null)
        {
            if (!string.Equals(digest, expected, StringComparison.Ordinal))
                throw new DbUpdateConcurrencyException("Sheet or local applications changed after preview.");
            var values = rows.Concat(Enumerable.Range(0, Math.Max(0, existing.Length - rows.Length))
                .Select(_ => Enumerable.Repeat("", header.Length).ToArray())).ToArray();
            using var request = CreateRequest(HttpMethod.Put, new Uri(target + "?valueInputOption=RAW"), headers);
            request.Content = JsonContent.Create(new { range, majorDimension = "ROWS", values });
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"Sheets export returned HTTP {(int)response.StatusCode}. Reload the preview before retrying.");
            db.Audit.Add(new AuditEvent { EntityId = id, EntityType = nameof(Integration), Action = "SheetsExported", Details = tab });
            await db.SaveChangesAsync(ct);
        }
        return new(tab, digest, existing, rows);
    }

    private async Task<string[][]> ReadAsync(Uri target, Dictionary<string, string> headers, CancellationToken ct)
    {
        using var request = CreateRequest(HttpMethod.Get, new Uri(target + "?valueRenderOption=FORMULA"), headers);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Sheets tab read returned HTTP {(int)response.StatusCode}. Check the tab name and access.");
        await response.Content.LoadIntoBufferAsync(5 * 1024 * 1024, ct);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(ct));
        if (!doc.RootElement.TryGetProperty("values", out var values)) return [];
        return values.EnumerateArray().Select(row => row.EnumerateArray().Select(cell => cell.ToString()).ToArray()).ToArray();
    }

    private static HttpRequestMessage CreateRequest(HttpMethod method, Uri uri, Dictionary<string, string> headers)
    {
        var request = new HttpRequestMessage(method, uri);
        if (headers.TryGetValue("Authorization", out var authorization))
            request.Headers.Authorization = System.Net.Http.Headers.AuthenticationHeaderValue.Parse(authorization);
        return request;
    }

    public sealed record SheetPreview(string Tab, string Digest, string[][] Existing, string[][] Rows);
}
