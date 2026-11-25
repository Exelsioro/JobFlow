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

    public async Task<SheetPreview> PreviewAsync(Guid id, string tab, CancellationToken ct, string? expected = null,
        Guid[]? platformIds = null, bool allPlatforms = false, string? spreadsheet = null, bool startNewJournal = false)
    {
        await ExportLock.WaitAsync(ct);
        try { return await PrepareAsync(id, tab, ct, expected, platformIds, allPlatforms, spreadsheet, startNewJournal); }
        finally { ExportLock.Release(); }
    }

    private async Task<SheetPreview> PrepareAsync(Guid id, string tab, CancellationToken ct, string? expected,
        Guid[]? platformIds, bool allPlatforms, string? spreadsheet, bool startNewJournal)
    {
        var explicitSelection = allPlatforms || platformIds != null;
        if (allPlatforms && platformIds is { Length: > 0 })
            throw new ArgumentException("Выберите либо все площадки, либо отдельные площадки.");
        var selected = platformIds?.Distinct().Order().ToArray() ?? [];
        if (!allPlatforms && platformIds != null && selected.Length == 0)
            throw new ArgumentException("Выберите хотя бы одну площадку.");
        if (selected.Length > 0 && await db.Platforms.CountAsync(x => selected.Contains(x.Id), ct) != selected.Length)
            throw new ArgumentException("В выборе есть неизвестная площадка. Обновите список.");
        string[] header = explicitSelection
            ? [$"JobFlow {id:D}", "Vacancy ID", "Title", "Employer", "State", "Vacancy URL", "Platform", "Platform ID"]
            : [$"JobFlow {id:D}", "Vacancy ID", "Title", "Employer", "State", "Vacancy URL"];
        var integration = await db.Set<Integration>().SingleAsync(x => x.Id == id, ct);
        if (integration.Kind != "google-sheets" || !integration.Enabled)
            throw new InvalidOperationException("Включите интеграцию Google Sheets перед выгрузкой.");
        if (!integration.ImportConfirmed && !startNewJournal)
            throw new InvalidOperationException("Подтвердите импорт истории или выберите «Начать новый журнал без импорта».");
        if (startNewJournal && string.IsNullOrWhiteSpace(spreadsheet))
            throw new ArgumentException("Укажите файл для нового журнала.");
        if (string.IsNullOrWhiteSpace(tab) || tab.Length > 60 || !Regex.IsMatch(tab, "^[A-Za-z0-9 _-]+$"))
            throw new ArgumentException("Enter a dedicated tab name (letters, digits, spaces, underscore or hyphen).");

        var spreadsheetId = string.IsNullOrWhiteSpace(spreadsheet)
            ? ImportSpreadsheetId(integration)
            : ParseSpreadsheetId(spreadsheet);
        var range = $"'{tab}'!A1:{(explicitSelection ? "H" : "F")}{MaxRows + 1}";
        var target = new Uri($"https://sheets.googleapis.com/v4/spreadsheets/{spreadsheetId}/values/{Uri.EscapeDataString(range)}");
        var headers = await new GoogleOAuth(secrets).HeadersAsync(integration, http, ct);
        var existing = await ReadAsync(target, headers, ct);
        if (existing.Length > 0 && !existing[0].SequenceEqual(header))
            throw new InvalidOperationException("Выберите пустую вкладку или вкладку, ранее выгруженную этой интеграцией в том же формате.");

        var applications = await (from app in db.Applications.AsNoTracking()
                                  join vacancy in db.Vacancies.AsNoTracking() on app.VacancyId equals vacancy.Id
                                  join platform in db.Platforms.AsNoTracking() on vacancy.JobPlatformId equals platform.Id
                                  where app.CandidateProfileId == integration.CandidateProfileId &&
                                        !app.IsTest && (allPlatforms || (explicitSelection
                                            ? selected.Contains(platform.Id) : platform.Id == integration.JobPlatformId))
                                  orderby app.CreatedAt, app.Id
                                  select new { App = app, Vacancy = vacancy, Platform = platform }).Take(MaxRows + 1).ToListAsync(ct);
        if (applications.Count > MaxRows) throw new InvalidOperationException("Export exceeds the 500-application tab limit.");
        string[][] rows = [header, .. applications.Select(x =>
        {
            string[] row = [x.App.Id.ToString(), x.Vacancy.ExternalId, x.Vacancy.Title, x.Vacancy.Employer, x.App.State.ToString(), x.Vacancy.Url];
            return explicitSelection ? [.. row, x.Platform.Name, x.Platform.Id.ToString()] : row;
        })];

        var digest = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { id, target, startNewJournal, allPlatforms, selected, existing, rows })));
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
            db.Audit.Add(new AuditEvent { EntityId = id, EntityType = nameof(Integration), Action = "SheetsExported", Details = startNewJournal ? tab + " (без импорта истории)" : tab });
            await db.SaveChangesAsync(ct);
        }
        return new(tab, digest, existing, rows);
    }

    private static string ImportSpreadsheetId(Integration integration)
    {
        var endpoint = new Uri(integration.Endpoint);
        RemoteIntegrationService.ValidateEndpoint(integration.Kind, endpoint);
        var match = Regex.Match(endpoint.AbsolutePath, "^/v4/spreadsheets/([A-Za-z0-9_-]+)/values/[^/]+$");
        if (!match.Success) throw new ArgumentException("Invalid spreadsheet ID or import range.");
        return match.Groups[1].Value;
    }

    private static string ParseSpreadsheetId(string value)
    {
        value = value.Trim();
        if (Regex.IsMatch(value, @"\A[A-Za-z0-9_-]{1,200}\z")) return value;
        if (Uri.TryCreate(value, UriKind.Absolute, out var url) && url.Scheme == Uri.UriSchemeHttps &&
            url.Host == "docs.google.com" && url.IsDefaultPort && url.UserInfo.Length == 0)
        {
            var match = Regex.Match(url.AbsolutePath, @"\A/spreadsheets/d/([A-Za-z0-9_-]{1,200})(?:/(?:edit|view))?/?\z");
            if (match.Success) return match.Groups[1].Value;
        }
        throw new ArgumentException("Укажите ссылку на файл Google Sheets или его ID.");
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
