using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using System.Text;

namespace JobFlow.Infrastructure;

public sealed record CliModelInfo(string Id, string DisplayName, bool IsDefault, string DefaultReasoningEffort, string[] SupportedReasoningEfforts);
public sealed record CliModels(IReadOnlyList<CliModelInfo> Models, DateTime FetchedAt)
{
    public string DefaultModel => Models.SingleOrDefault(x => x.IsDefault)?.Id ?? "";
}
public interface ICliModelCatalog
{
    Task<CliModels> GetAsync(bool refresh, CancellationToken ct);
}
public interface ICliModelSource
{
    Task<CliModels> ReadAsync(CancellationToken ct);
}

public sealed class CodexModelCatalog(ICliModelSource source) : ICliModelCatalog
{
    private readonly SemaphoreSlim gate = new(1);
    private CliModels? cached;
    public async Task<CliModels> GetAsync(bool refresh, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            if (!refresh && cached is { } value && DateTime.UtcNow - value.FetchedAt < TimeSpan.FromMinutes(5)) return value;
            var result = await source.ReadAsync(ct);
            if (result.Models.Count == 0) throw new InvalidOperationException("CLI вернул пустой список моделей. Проверьте авторизацию Codex CLI и обновите список.");
            if (result.Models.Count(x => x.IsDefault) > 1) throw new InvalidOperationException("CLI вернул неоднозначную модель по умолчанию. Обновите список моделей.");
            cached = result;
            return result;
        }
        finally { gate.Release(); }
    }
}

// Metadata only. This connection never starts a model turn or sends candidate data.
public sealed class CodexAppServerModelSource : ICliModelSource
{
    public async Task<CliModels> ReadAsync(CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        // Cold app-server startup can exceed 25 s on machines with limited RAM.
        deadline.CancelAfter(TimeSpan.FromSeconds(90));
        var token = deadline.Token;
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("JOBFLOW_CODEX_PATH") ?? "codex")
        {
            UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true,
            RedirectStandardError = true, CreateNoWindow = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        start.ArgumentList.Add("app-server");
        Process? process = null;
        Task? drain = null;
        try
        {
            process = Process.Start(start) ?? throw new InvalidOperationException("Не удалось запустить Codex CLI.");
            drain = DrainAsync(process.StandardError, token);
            async Task SendAsync(object value) => await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(value).AsMemory(), token);
            async Task<JsonElement> ResponseAsync(int id)
            {
                for (var i = 0; i < 2000; i++)
                {
                    var line = await process.StandardOutput.ReadLineAsync(token);
                    if (line == null) throw new InvalidOperationException("Codex CLI закрыл подключение к каталогу. Проверьте установку и авторизацию CLI.");
                    if (line.Length > 2_000_000) throw new InvalidOperationException("Ответ каталога CLI слишком большой.");
                    using var document = JsonDocument.Parse(line);
                    var root = document.RootElement;
                    if (!root.TryGetProperty("id", out var responseId) || !responseId.TryGetInt32(out var number) || number != id) continue;
                    if (root.TryGetProperty("error", out _)) throw new InvalidOperationException("Codex CLI не смог прочитать каталог моделей. Проверьте версию и авторизацию CLI.");
                    return root.GetProperty("result").Clone();
                }
                throw new InvalidOperationException("CLI не вернул ответ каталога моделей.");
            }
            await SendAsync(new { id = 1, method = "initialize", @params = new { clientInfo = new { name = "jobflow", version = "1.0.0" }, capabilities = new { experimentalApi = true } } });
            await ResponseAsync(1);
            await SendAsync(new { method = "initialized" });
            var models = new List<CliModelInfo>();
            string? cursor = null;
            for (var page = 0; page < 10; page++)
            {
                var id = page + 2;
                await SendAsync(new { id, method = "model/list", @params = new { includeHidden = false, cursor } });
                var result = await ResponseAsync(id);
                foreach (var item in result.GetProperty("data").EnumerateArray())
                {
                    static string Text(JsonElement element, string key) => element.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()! : "";
                    var model = Text(item, "model");
                    if (model.Length == 0) model = Text(item, "id");
                    if (model.Length == 0) continue;
                    var efforts = item.TryGetProperty("supportedReasoningEfforts", out var supported)
                        ? supported.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.String ? x.GetString()! : Text(x, "reasoningEffort")).Where(x => x.Length > 0).Distinct().ToArray() : [];
                    models.Add(new(model, Text(item, "displayName"), item.TryGetProperty("isDefault", out var defaultValue) && defaultValue.ValueKind == JsonValueKind.True, Text(item, "defaultReasoningEffort"), efforts));
                }
                cursor = result.TryGetProperty("nextCursor", out var next) && next.ValueKind == JsonValueKind.String ? next.GetString() : null;
                if (string.IsNullOrEmpty(cursor)) return new(models.DistinctBy(x => x.Id).ToArray(), DateTime.UtcNow);
            }
            throw new InvalidOperationException("CLI вернул слишком много страниц каталога моделей.");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { throw new InvalidOperationException("Истекло время загрузки моделей CLI. Проверьте Codex CLI и повторите обновление."); }
        catch (Exception ex) when (ex is Win32Exception or IOException or JsonException or KeyNotFoundException)
        { throw new InvalidOperationException("Не удалось прочитать модели Codex CLI. Проверьте установку, JOBFLOW_CODEX_PATH и авторизацию CLI."); }
        finally
        {
            if (process != null)
            {
                try { process.StandardInput.Close(); if (!process.HasExited) process.Kill(true); await process.WaitForExitAsync(CancellationToken.None); }
                catch (InvalidOperationException) { }
                catch (Win32Exception) { }
            }
            deadline.Cancel();
            if (drain != null) { try { await drain; } catch (OperationCanceledException) { } catch (IOException) { } }
            process?.Dispose();
        }
    }
    private static async Task DrainAsync(StreamReader reader, CancellationToken ct)
    {
        var buffer = new char[2048];
        while (await reader.ReadAsync(buffer.AsMemory(), ct) > 0) { }
    }
}
