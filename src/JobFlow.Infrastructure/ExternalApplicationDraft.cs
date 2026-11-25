using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using JobFlow.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Playwright;

namespace JobFlow.Infrastructure;

public sealed record ExternalDraftField(string Label, string Value, string Type);
public sealed record ExternalApplicationDraft(Guid ApplicationId, Guid BrowserProfileId, string SourceUrl, string Url,
    bool Verified, string Reason, ExternalDraftField[] Fields, string Fingerprint, int Revision, string MaterialHash, DateTime CreatedAt)
{
    public string EmployerEvidence { get; init; } = "";
    public string VacancyEvidence { get; init; } = "";
    public string ReferralUrl { get; init; } = "";
    public string RoutedPlatform { get; init; } = "";
    public string Outcome { get; init; } = "";
    public const string AuditAction = "ExternalApplicationReview";
    public const string InvalidatedAction = "ExternalApplicationReviewInvalidated";
    public const string PlatformReferralAction = "ExternalApplicationPlatformReferral";
    public static async Task<ExternalApplicationDraft?> ReadAsync(JobFlowDb db, Guid applicationId, CancellationToken ct = default)
    {
        var entry = await db.Audit.AsNoTracking().Where(x => x.EntityId == applicationId && (x.Action == AuditAction || x.Action == InvalidatedAction))
            .OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.Id).Select(x => new { x.Action, x.Details }).FirstOrDefaultAsync(ct);
        return entry?.Action != AuditAction ? null : JsonSerializer.Deserialize<ExternalApplicationDraft>(entry.Details);
    }
    public static string ScreenshotPath(Guid id) => Path.Combine(Storage.Root, "external-drafts", id.ToString("D"), "screen.jpg");
    public static string Materials(JobApplication app) => Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes(app.ResumeVersionId + "\n" + app.CoverLetter)));
    public static async Task<(ExternalDraftField[] Fields, string Fingerprint)> CaptureAsync(IPage page)
    {
        var fieldsJson = await page.Locator("input:not([type=hidden]):not([type=password]),textarea,select").EvaluateAllAsync<string>("""
            elements => JSON.stringify(elements.filter(el => el.getClientRects().length).slice(0,150).map(el => ({
              label: (el.getAttribute('aria-label') || Array.from(el.labels || [], l => l.innerText).join(' ') || el.name || el.type).trim(),
              type: el.type || el.tagName.toLowerCase(),
              value: el.type === 'file' ? Array.from(el.files || [], f => f.name).join(', ') :
                ['checkbox','radio'].includes(el.type) ? String(el.checked) : el.value || ''
            })))
            """);
        var fields = JsonSerializer.Deserialize<ExternalDraftField[]>(fieldsJson, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        // Include page text, form destinations and hidden anti-CSRF fields in the change detector, but never store their values.
        var identity = await page.EvaluateAsync<string>("""
            () => JSON.stringify({url:location.href,text:document.body?.innerText || '',
              forms:Array.from(document.forms, f=>({action:f.action,method:f.method})),
              fields:Array.from(document.querySelectorAll('input,textarea,select'), el=>({name:el.name,type:el.type,
                value:el.type==='password'?'':el.value,checked:el.checked,files:Array.from(el.files||[],f=>({name:f.name,size:f.size,lastModified:f.lastModified}))}))})
            """);
        return (fields, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))));
    }
}

public interface IExternalApplicationAddressResolver
{
    Task<IPAddress[]> ResolveAsync(string host, CancellationToken ct);
}

public sealed record ExternalApplicationDocument(int Status, Dictionary<string, string> Headers, byte[] Body);
public interface IExternalApplicationDocumentLoader
{
    Task<ExternalApplicationDocument> LoadAsync(IRoute route, CancellationToken ct);
}
public sealed class ExternalApplicationDocumentLoader : IExternalApplicationDocumentLoader
{
    public async Task<ExternalApplicationDocument> LoadAsync(IRoute route, CancellationToken ct)
    {
        var response = await route.FetchAsync(new() { MaxRedirects = 0, Timeout = 20000 }).WaitAsync(ct);
        try
        {
            return new(response.Status, response.Headers.Where(x => x.Key is not ("content-encoding" or "transfer-encoding" or "content-length"))
                .ToDictionary(x => x.Key, x => x.Value), await response.BodyAsync());
        }
        finally { await response.DisposeAsync(); }
    }
}

public static class ExternalApplicationDestination
{
    public static bool IsPublicAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) return IsPublicAddress(address.MapToIPv4());
        if (IPAddress.IsLoopback(address)) return false;
        var b = address.GetAddressBytes();
        if (b.Length == 16) return (b[0] & 0xe0) == 0x20; // Global unicast only; no ULA, link-local or multicast.
        return b[0] is not (0 or 10 or 127) && b[0] < 224 &&
            !(b[0] == 169 && b[1] == 254) && !(b[0] == 172 && b[1] is >= 16 and <= 31) &&
            !(b[0] == 192 && b[1] == 168) && !(b[0] == 100 && b[1] is >= 64 and <= 127);
    }
    public static async Task<bool> IsAllowedAsync(string value, CancellationToken ct,
        Func<string, CancellationToken, Task<IPAddress[]>>? resolve = null)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != "https" || !uri.IsDefaultPort ||
            uri.UserInfo.Length != 0 || uri.HostNameType != UriHostNameType.Dns || uri.IsLoopback ||
            !uri.IdnHost.Contains('.') || uri.IdnHost.EndsWith(".local", StringComparison.OrdinalIgnoreCase)) return false;
        try
        {
            var addresses = await (resolve ?? ((host, token) => Dns.GetHostAddressesAsync(host, token)))(uri.IdnHost, ct)
                .WaitAsync(TimeSpan.FromSeconds(5), ct);
            return addresses.Length > 0 && addresses.All(IsPublicAddress);
        }
        catch (System.Net.Sockets.SocketException) { return false; }
        catch (TimeoutException) { return false; }
    }
}
