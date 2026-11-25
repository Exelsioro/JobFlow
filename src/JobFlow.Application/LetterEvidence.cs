using System.Security.Cryptography;
using System.Text;
using JobFlow.Domain;

namespace JobFlow.Application;

public sealed record CandidateFact(string Id, string SourceField, string Text);
public sealed record FactCitation(string FactId, string Quote);
public sealed record LetterDraft(string Letter, FactCitation[] Citations);

public static class LetterEvidence
{
    public static CandidateFact[] Facts(CandidateProfile profile)
    {
        var fields = new[] { (nameof(profile.Name), profile.Name), (nameof(profile.Email), profile.Email),
            (nameof(profile.Experience), profile.Experience), (nameof(profile.Skills), profile.Skills),
            (nameof(profile.Education), profile.Education), (nameof(profile.Languages), profile.Languages),
            (nameof(profile.Dossier), profile.Dossier) };
        return fields.SelectMany(field => field.Item2.Split(['\r', '\n'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(text => new CandidateFact(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(field.Item1 + "\n" + text))), field.Item1, text)))
            .DistinctBy(x => x.Id).ToArray();
    }

    public static string[] Validate(LetterDraft draft, CandidateFact[] facts)
    {
        var issues = new List<string>();
        if (string.IsNullOrWhiteSpace(draft.Letter) || draft.Letter.Length > 30000) issues.Add("Letter must contain 1–30000 characters.");
        if (draft.Citations.Length is < 1 or > 100) issues.Add("Supply 1–100 references to candidate facts.");
        var catalog = facts.ToDictionary(x => x.Id, StringComparer.Ordinal);
        foreach (var citation in draft.Citations.Take(100))
            if (string.IsNullOrWhiteSpace(citation.Quote) || !catalog.TryGetValue(citation.FactId, out var fact) ||
                !fact.Text.Contains(citation.Quote, StringComparison.Ordinal))
                issues.Add("Citation must reference a supplied fact ID and an exact nonempty quote from that fact: " + citation.FactId);
        return issues.Distinct().ToArray();
    }
}
