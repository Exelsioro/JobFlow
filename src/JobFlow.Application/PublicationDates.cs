using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace JobFlow.Application;

public sealed record PublicationDateSource(string Source, string Evidence, DateTime CalendarDate, DateTime? PublishedAtUtc = null);

public static class PublicationDates
{
    // Compare publication calendar days, preserving an explicit datePosted offset. A UTC
    // conversion can otherwise manufacture a conflict for a publication near midnight.
    public static PublicationDateSource[] Sources(string visibleText, IEnumerable<string> scripts, string url, DateTime now)
    {
        var structured = new List<(string Quote, DateTimeOffset Date)>();
        foreach (var script in scripts)
        {
            try
            {
                using var doc = JsonDocument.Parse(script);
                Read(doc.RootElement);
            }
            catch (JsonException) { /* Malformed metadata is not publication evidence. */ }
        }
        void Read(JsonElement node)
        {
            if (node.ValueKind == JsonValueKind.Array) { foreach (var child in node.EnumerateArray()) Read(child); return; }
            if (node.ValueKind != JsonValueKind.Object) return;
            if (node.TryGetProperty("@graph", out var graph)) Read(graph);
            if (!node.TryGetProperty("@type", out var type) ||
                !(type.ValueKind == JsonValueKind.String && type.GetString() == "JobPosting" ||
                  type.ValueKind == JsonValueKind.Array && type.EnumerateArray().Any(x => x.ValueKind == JsonValueKind.String && x.GetString() == "JobPosting"))) return;
            if (node.TryGetProperty("url", out var jobUrl) && jobUrl.ValueKind == JsonValueKind.String &&
                Uri.TryCreate(jobUrl.GetString(), UriKind.Absolute, out var other) &&
                Uri.TryCreate(url, UriKind.Absolute, out var current) &&
                !string.Equals(other.GetLeftPart(UriPartial.Path).TrimEnd('/'), current.GetLeftPart(UriPartial.Path).TrimEnd('/'), StringComparison.OrdinalIgnoreCase)) return;
            if (node.TryGetProperty("datePosted", out var posted) && posted.ValueKind == JsonValueKind.String &&
                DateTimeOffset.TryParse(posted.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date))
                structured.Add((posted.GetRawText(), date));
        }
        var result = structured.Select(x => new PublicationDateSource("JobPosting.datePosted", x.Quote, x.Date.Date, x.Date.UtcDateTime)).ToList();
        var reference = structured.Count > 0 ? new DateTimeOffset(DateTime.SpecifyKind(now, DateTimeKind.Utc)).ToOffset(structured[0].Date.Offset).DateTime : now;
        foreach (Match match in Regex.Matches(visibleText,
            @"(?:вакансия\s+опубликована|\bpublished\b|\bposted\b)(?:\s+on)?[\s:–-]*(?<date>\d{4}-\d{2}-\d{2}|\d{2}\.\d{2}\.\d{4}|\d{1,2}\s+\p{L}+\s+\d{4}|[A-Za-z]+\s+\d{1,2},?\s+\d{4}|today|yesterday|сегодня|вчера|\d{1,3}\s+(?:days? ago|дн(?:я|ей|ь) назад))",
            RegexOptions.IgnoreCase))
            if (Resolve(match.Groups["date"].Value, reference) is { } day)
                result.Add(new("Visible publication label", match.Value, day.Date, DateTime.SpecifyKind(day, DateTimeKind.Utc)));
        return result.Distinct().ToArray();
    }

    public static bool Conflicting(IEnumerable<PublicationDateSource> sources) => sources.Select(x => x.CalendarDate).Distinct().Skip(1).Any();

    public static DateTime? Resolve(string evidence, DateTime now)
    {
        if (string.IsNullOrWhiteSpace(evidence) || Regex.IsMatch(evidence, "updated|modified|обновлен|обновлён|copyright", RegexOptions.IgnoreCase)) return null;
        var iso = Regex.Match(evidence, @"\b\d{4}-\d{2}-\d{2}(?:T[0-9:.]+(?:Z|[+-]\d{2}:?\d{2})?)?");
        if (iso.Success && DateTimeOffset.TryParse(iso.Value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)) return parsed.UtcDateTime;
        var local = Regex.Match(evidence, @"\b\d{2}\.\d{2}\.\d{4}\b");
        if (local.Success && DateTime.TryParseExact(local.Value, "dd.MM.yyyy", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var day)) return day.ToUniversalTime();
        var words = Regex.Match(evidence, @"\b(?:\d{1,2}\s+[\p{L}]+\s+\d{4}|[A-Za-z]+\s+\d{1,2},?\s+\d{4})\b");
        if (words.Success)
            foreach (var culture in new[] { "ru-RU", "en-US" })
                if (DateTime.TryParse(words.Value.Replace('\u00a0', ' ').Replace('\u202f', ' '), CultureInfo.GetCultureInfo(culture), DateTimeStyles.AssumeUniversal, out var namedDate)) return namedDate.ToUniversalTime();
        if (Regex.IsMatch(evidence, @"\byesterday\b|вчера", RegexOptions.IgnoreCase)) return now.Date.AddDays(-1);
        if (Regex.IsMatch(evidence, @"\btoday\b|сегодня", RegexOptions.IgnoreCase)) return now.Date;
        var relative = Regex.Match(evidence, @"\b(\d{1,3})\s*(?:days? ago|дн(?:я|ей|ь) назад)\b", RegexOptions.IgnoreCase);
        return relative.Success ? now.Date.AddDays(-int.Parse(relative.Groups[1].Value)) : null;
    }
}
