using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace JobFlow.Infrastructure;

internal static partial class GmailContent
{
    public static string Extract(JsonElement payload, string snippet)
    {
        var text = new StringBuilder();
        Visit(payload, 0);
        return text.Length == 0 ? snippet : text.ToString();

        void Visit(JsonElement part, int depth)
        {
            if (depth > 12 || text.Length >= 12000 || part.ValueKind != JsonValueKind.Object)
                return;
            if (part.TryGetProperty("filename", out var name) && !string.IsNullOrEmpty(name.GetString()))
                return;
            var mime = part.TryGetProperty("mimeType", out var type) ? type.GetString() : null;
            if (mime is "text/plain" or "text/html" && part.TryGetProperty("body", out var body) &&
                body.TryGetProperty("data", out var data) && data.GetString() is { Length: > 0 and <= 262144 } encoded)
            {
                var base64 = encoded.Replace('-', '+').Replace('_', '/').PadRight((encoded.Length + 3) / 4 * 4, '=');
                try
                {
                    var value = Encoding.UTF8.GetString(Convert.FromBase64String(base64));
                    if (mime == "text/html")
                    {
                        var links = Href().Matches(value).Take(100).Select(match => WebUtility.HtmlDecode(match.Groups[1].Value));
                        value = WebUtility.HtmlDecode(Tag().Replace(value, " ")) + "\n" + string.Join("\n", links);
                    }
                    text.Append(value.AsSpan(0, Math.Min(value.Length, 12000 - text.Length)));
                    if (text.Length < 12000) text.Append('\n');
                }
                catch (FormatException) { }
            }
            if (mime?.StartsWith("multipart/", StringComparison.OrdinalIgnoreCase) == true &&
                part.TryGetProperty("parts", out var parts))
                foreach (var child in parts.EnumerateArray()) Visit(child, depth + 1);
        }
    }

    [GeneratedRegex("<[^>]*>", RegexOptions.NonBacktracking)]
    private static partial Regex Tag();

    [GeneratedRegex("""\bhref\s*=\s*["'](https?://[^"']+)["']""", RegexOptions.IgnoreCase | RegexOptions.NonBacktracking)]
    private static partial Regex Href();
}
