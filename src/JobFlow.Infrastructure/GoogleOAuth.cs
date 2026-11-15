using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using JobFlow.Domain;

namespace JobFlow.Infrastructure;

public sealed class GoogleOAuth(SecretStore secrets)
{
    public const string RedirectUri = "http://127.0.0.1:5271/api/google/oauth/callback";
    private const string Sheets = "https://www.googleapis.com/auth/spreadsheets";
    private const string Gmail = "https://www.googleapis.com/auth/gmail.readonly";
    private static readonly Uri TokenEndpoint = new("https://oauth2.googleapis.com/token");
    private static readonly SemaphoreSlim RefreshLock = new(1);
    private readonly ConcurrentDictionary<string, PendingGrant> pending = new();
    private readonly object grantLock = new();

    public string Start(Integration integration)
    {
        var scope = Scope(integration.Kind);
        var config = Config();
        var state = RandomToken();
        var verifier = RandomToken();
        lock (grantLock)
        {
            foreach (var expired in pending.Where(x => x.Value.ExpiresAt <= DateTimeOffset.UtcNow))
                pending.TryRemove(expired.Key, out _);
            if (pending.Count >= 64) throw new InvalidOperationException("Too many pending Google authorizations.");
            pending[state] = new(integration.Id, scope, verifier, DateTimeOffset.UtcNow.AddMinutes(10));
        }
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        return "https://accounts.google.com/o/oauth2/v2/auth?" + Query(new()
        {
            ["client_id"] = config.Id, ["redirect_uri"] = RedirectUri, ["scope"] = scope,
            ["response_type"] = "code", ["access_type"] = "offline", ["prompt"] = "consent",
            ["state"] = state, ["code_challenge"] = challenge, ["code_challenge_method"] = "S256"
        });
    }

    public async Task CompleteAsync(string state, string? code, HttpClient http, CancellationToken ct)
    {
        if (!pending.TryRemove(state, out var grant) || grant.ExpiresAt <= DateTimeOffset.UtcNow ||
            string.IsNullOrWhiteSpace(code) || code.Length > 4096)
            throw new ArgumentException("Google authorization expired or was cancelled. Start again.");
        var values = await ExchangeAsync(http, new()
        {
            ["grant_type"] = "authorization_code", ["code"] = code,
            ["redirect_uri"] = RedirectUri, ["code_verifier"] = grant.Verifier
        }, grant.Scope, null, ct);
        await secrets.SaveAsync(grant.IntegrationId, values, ct);
    }

    public async Task<Dictionary<string, string>> HeadersAsync(Integration integration, HttpClient http, CancellationToken ct)
    {
        var values = await secrets.ReadAsync(integration.Id, ct);
        if (!values.ContainsKey("oauth_refresh_token")) return values;
        var scope = Scope(integration.Kind);
        if (values.GetValueOrDefault("oauth_scope") != scope)
            throw new InvalidOperationException("Google permissions changed. Connect Google again.");

        await RefreshLock.WaitAsync(ct);
        try
        {
            values = await secrets.ReadAsync(integration.Id, ct);
            if (!values.TryGetValue("oauth_refresh_token", out var refresh)) return values;
            if (values.GetValueOrDefault("oauth_scope") != scope)
                throw new InvalidOperationException("Google permissions changed. Connect Google again.");
            if (!long.TryParse(values.GetValueOrDefault("oauth_expires_at"), out var seconds) ||
                seconds <= DateTimeOffset.UtcNow.AddMinutes(1).ToUnixTimeSeconds())
            {
                values = await ExchangeAsync(http, new() { ["grant_type"] = "refresh_token", ["refresh_token"] = refresh },
                    scope, refresh, ct);
                await secrets.SaveAsync(integration.Id, values, ct);
            }
            return new(StringComparer.OrdinalIgnoreCase) { ["Authorization"] = "Bearer " + values["oauth_access_token"] };
        }
        finally { RefreshLock.Release(); }
    }

    private static async Task<Dictionary<string, string>> ExchangeAsync(HttpClient http,
        Dictionary<string, string> fields, string scope, string? oldRefresh, CancellationToken ct)
    {
        var config = Config();
        fields["client_id"] = config.Id;
        if (!string.IsNullOrEmpty(config.Secret)) fields["client_secret"] = config.Secret;
        using var request = new HttpRequestMessage(HttpMethod.Post, TokenEndpoint) { Content = new FormUrlEncodedContent(fields) };
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException("Google rejected authorization. Check client settings or reconnect.");
        await response.Content.LoadIntoBufferAsync(65536, ct);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(ct));
        var token = doc.RootElement;
        string? Read(string name) => token.TryGetProperty(name, out var field) && field.ValueKind == JsonValueKind.String
            ? field.GetString() : null;
        var access = Read("access_token");
        var refresh = Read("refresh_token") ?? oldRefresh;
        var scopes = Read("scope");
        if (string.IsNullOrWhiteSpace(access) || string.IsNullOrWhiteSpace(refresh) ||
            !string.Equals(Read("token_type"), "Bearer", StringComparison.OrdinalIgnoreCase) ||
            (scopes != null && !scopes.Split(' ').Contains(scope, StringComparer.Ordinal)) ||
            !token.TryGetProperty("expires_in", out var expires) || !expires.TryGetInt32(out var seconds) || seconds <= 0)
            throw new InvalidOperationException("Google did not grant the required offline access.");
        return new()
        {
            ["oauth_access_token"] = access, ["oauth_refresh_token"] = refresh, ["oauth_scope"] = scope,
            ["oauth_expires_at"] = DateTimeOffset.UtcNow.AddSeconds(seconds).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)
        };
    }

    private static (string Id, string? Secret) Config()
    {
        var id = Environment.GetEnvironmentVariable("JOBFLOW_GOOGLE_CLIENT_ID");
        if (string.IsNullOrWhiteSpace(id)) throw new InvalidOperationException("Set JOBFLOW_GOOGLE_CLIENT_ID in API and Worker.");
        return (id, Environment.GetEnvironmentVariable("JOBFLOW_GOOGLE_CLIENT_SECRET"));
    }

    private static string Scope(string kind) => kind switch
    {
        "gmail" => Gmail,
        "google-sheets" => Sheets,
        _ => throw new ArgumentException("Google OAuth supports Gmail and Google Sheets.")
    };

    private static string Query(Dictionary<string, string> fields) =>
        string.Join("&", fields.Select(x => Uri.EscapeDataString(x.Key) + "=" + Uri.EscapeDataString(x.Value)));

    private static string RandomToken() => Base64Url(RandomNumberGenerator.GetBytes(32));
    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private sealed record PendingGrant(Guid IntegrationId, string Scope, string Verifier, DateTimeOffset ExpiresAt);
}
