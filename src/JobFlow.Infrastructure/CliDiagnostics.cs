using System.Text.Json;
using System.Text.RegularExpressions;
using JobFlow.Application;

namespace JobFlow.Infrastructure;

public static class CliDiagnostics
{
    public static string Extract(string stdout, string stderr)
    {
        var errors = new List<string>();
        foreach (var line in stdout.Split('\n'))
        {
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("type", out var type) || type.GetString() is not ("error" or "turn.failed")) continue;
                if (root.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String) errors.Add(message.GetString()!);
                else if (root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object && error.TryGetProperty("message", out message)) errors.Add(message.GetString()!);
            }
            catch (JsonException) { }
        }
        // Never retain the echoed prompt, tool results or model response from stderr.
        foreach (var line in stderr.Split('\n'))
            if (Regex.IsMatch(line, @"^(?:Failed to read prompt from stdin:|error:|ERROR:|Error:|error\[|\d{4}-\d\d-\d\dT\S+\s+ERROR\b)", RegexOptions.CultureInvariant)) errors.Add(line.Trim());
        var text = string.Join("\n", errors.Distinct());
        if (string.IsNullOrWhiteSpace(text)) text = "CLI завершился без структурированного сообщения об ошибке.";
        text = Regex.Replace(text, @"(?i)\b(?:sk-|sess-)[a-z0-9_-]{12,}|\bBearer\s+\S+", "[REDACTED]");
        text = Regex.Replace(text, @"(?i)([\w.-]*(?:api[_-]?key|token|secret|password)[\w.-]*\s*[=:]\s*)[^\s,;]+", "$1[REDACTED]");
        text = Regex.Replace(text, @"[\w.+-]+@[\w.-]+\.[a-zA-Z]{2,}", "[EMAIL]");
        return text[..Math.Min(6000, text.Length)];
    }

    public static (string Code, bool Retryable) Classify(string message)
    {
        if (Regex.IsMatch(message, @"(?i)not.valid.UTF-8|invalid.UTF-8|Failed.to.read.prompt.from.stdin")) return ("encoding", false);
        if (Regex.IsMatch(message, @"(?i)401|403|unauthori[sz]ed|authentication|log\s?in|refresh.token|invalid.grant")) return ("authentication", false);
        if (Regex.IsMatch(message, @"(?i)429|rate.limit|quota|usage.limit")) return ("rate_limit", false);
        if (Regex.IsMatch(message, @"(?i)invalid.*schema|schema.*invalid|unsupported|not.support|unknown.model|model.*not.found|invalid.*reasoning|unexpected.argument|unrecognized")) return ("configuration", false);
        if (Regex.IsMatch(message, @"(?i)context.length|context.window|too.many.tokens|input.*too.large")) return ("request", false);
        if (Regex.IsMatch(message, @"(?i)timeout|timed.out|connection|connect|network|503|502|504|temporar|stream.disconnected")) return ("transport", true);
        return ("cli_exit", false);
    }

    public static async Task<ModelProviderException> SaveAsync(string model, string reasoning, string executable, int? exitCode,
        string stdout, string stderr, string? forcedCode = null, bool? retryable = null)
    {
        var detail = Extract(stdout, stderr);
        var classification = Classify(detail);
        var id = Guid.NewGuid();
        var directory = Path.Combine(Storage.Root, "cli-diagnostics");
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, id + ".json"), JsonSerializer.Serialize(new
        {
            id, createdAt = DateTime.UtcNow, model, reasoning, executable, exitCode,
            code = forcedCode ?? classification.Code, retryable = retryable ?? classification.Retryable, error = detail
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web)), CancellationToken.None);
        return new ModelProviderException(forcedCode ?? classification.Code,
            $"Ошибка Codex CLI ({forcedCode ?? classification.Code}, exit={exitCode?.ToString() ?? "n/a"}): {detail[..Math.Min(1000, detail.Length)]} Диагностика: /api/cli-diagnostics/{id}",
            retryable ?? classification.Retryable, id);
    }
}
