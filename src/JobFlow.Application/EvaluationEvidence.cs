using System.Text;
using System.Text.RegularExpressions;
using JobFlow.Domain;

namespace JobFlow.Application;

public sealed record SkillEvidenceCheck(string ClaimedSkill, string Status, string? CandidateSkill,
    string? SourceField, string? SourceValue, string[] MentionedIn, string Reason);
public sealed record EvaluationValidation(Guid AgentExecutionId, string AgentDecision, string AgentRationale,
    string[] AgentMatches, SkillEvidenceCheck[] Skills, bool IsValid, bool Truncated, string[] Warnings);

public static class EvaluationEvidence
{
    private static readonly Dictionary<string, string> Aliases = BuildAliases();
    public static string[] CandidateSkills(CandidateProfile profile) => profile.Skills
        .Split([',', ';', '\r', '\n'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
        .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    // Equivalence of names only: never infer a version, proficiency, duration or a related technology.
    public static string SkillKey(string value)
    {
        var key = Regex.Replace(value.Normalize(NormalizationForm.FormKC).Trim(), @"\s+", " ").ToUpperInvariant();
        if (Aliases.TryGetValue(key, out var canonical)) return canonical;
        var version = Regex.Match(key, @"^(?:\.NET|DOTNET|NET)\s*V?(\d+)(?:\.0)?$");
        return version.Success ? ".NET " + version.Groups[1].Value : key;
    }

    public static EvaluationResult Validate(CandidateProfile profile, EvaluationResult proposed, Guid executionId)
    {
        var candidates = CandidateSkills(profile);
        var truncated = proposed.Matches.Length > 100 || proposed.Matches.Any(x => x.Length > 200);
        var checks = proposed.Matches.Take(100).Select(claim =>
        {
            var name = claim.Length <= 200 ? candidates.FirstOrDefault(x => string.Equals(x.Trim(), claim.Trim(), StringComparison.OrdinalIgnoreCase))
                ?? candidates.FirstOrDefault(x => SkillKey(x) == SkillKey(claim)) : null;
            var mentions = name == null ? MentionedFields(profile, claim) : [];
            return new SkillEvidenceCheck(Bound(claim, 200), name == null ? "Unsupported" :
                string.Equals(name, claim.Trim(), StringComparison.OrdinalIgnoreCase) ? "Exact" : "EquivalentName",
                name, name == null ? null : nameof(CandidateProfile.Skills), name, mentions,
                name != null ? "Подтверждено списком навыков профиля; уровень и стаж не выводились из названия." :
                mentions.Length > 0 ? "Название упомянуто в других полях досье. Упоминание не подтверждает опыт или уровень; требуется проверка." :
                "Подтверждение в списке навыков профиля отсутствует; требуется проверка.");
        }).ToArray();
        var unsupported = checks.Where(x => x.Status == "Unsupported").Select(x => x.ClaimedSkill).Distinct().ToArray();
        var valid = unsupported.Length == 0 && !truncated;
        var warnings = new List<string>();
        if (!valid) warnings.Add("Не подтверждены навыки: " + string.Join(", ", unsupported.Take(12)) +
            (truncated ? ". Ответ превышает лимит 100 навыков / 200 символов; диагностика сокращена." : "."));
        if (proposed.Decision == "Proceed" && (proposed.MissingSkills.Length > 0 || proposed.UnverifiedRequirements.Length > 0))
            warnings.Add("В оценке остались недостающие навыки или неподтверждённые требования; автоматическая отправка запрещена.");
        var notes = checks.Where(x => x.Status != "Exact").Select(x => x.CandidateSkill != null
            ? $"Навык «{x.ClaimedSkill}» сопоставлен с «{x.CandidateSkill}» из поля «Навыки» профиля."
            : $"Навык «{x.ClaimedSkill}»: {x.Reason}");
        var decision = warnings.Count == 0 ? proposed.Decision : "NeedsReview";
        return proposed with
        {
            Decision = decision,
            Rationale = Bound(proposed.Rationale, 8000) + (warnings.Count > 0 ? "\nПроверка JobFlow: " + string.Join(" ", warnings) : ""),
            Matches = checks.Where(x => x.CandidateSkill != null).Select(x => x.CandidateSkill!).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            UnverifiedRequirements = proposed.UnverifiedRequirements.Concat(unsupported.Select(x => "Не подтверждён навык: " + x)).Distinct().ToArray(),
            Clarifications = proposed.Clarifications.Concat(notes).Concat(warnings).Distinct().ToArray(),
            Confidence = !valid || decision != proposed.Decision ? "Low" : proposed.Confidence,
            Validation = new(executionId, proposed.Decision, Bound(proposed.Rationale, 8000),
                proposed.Matches.Take(100).Select(x => Bound(x, 200)).ToArray(), checks, valid, truncated, warnings.ToArray())
        };
    }

    private static string[] MentionedFields(CandidateProfile profile, string claim)
    {
        if (claim.Length is < 2 or > 200) return [];
        var pattern = @"(?<![\p{L}\p{N}+#])" + Regex.Escape(claim.Trim()) + @"(?![\p{L}\p{N}+#])";
        return new[] { (nameof(profile.Experience), profile.Experience), (nameof(profile.Dossier), profile.Dossier),
            (nameof(profile.Education), profile.Education), (nameof(profile.Languages), profile.Languages) }
            .Where(x => Regex.IsMatch(x.Item2, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            .Select(x => x.Item1).ToArray();
    }
    private static string Bound(string value, int length) => value[..Math.Min(value.Length, length)];
    private static Dictionary<string, string> BuildAliases()
    {
        string[][] groups = [
            ["C#", "CSharp", "C Sharp", "C-Sharp"], [".NET", "dotnet", "dot net", "NET"],
            [".NET Core", "NET Core", "dotnet core"], [".NET Framework", "NET Framework", "dotnet framework"],
            ["ASP.NET", "ASP NET", "ASPNET"], ["ASP.NET Core", "ASP NET Core", "ASPNET Core"],
            ["ASP.NET Core Web API", "ASP.NET Core WebAPI"],
            ["EF Core", "Entity Framework Core", "Microsoft Entity Framework Core"], ["Entity Framework", "EF"],
            ["MS SQL Server", "Microsoft SQL Server", "SQL Server", "MSSQL", "SQLServer", "MS SQL"],
            ["T-SQL", "T SQL", "Transact-SQL"], ["JavaScript", "JS"], ["TypeScript", "TS"],
            ["WPF", "Windows Presentation Foundation"], ["DI", "Dependency Injection"], ["SOLID", "S.O.L.I.D."]
        ];
        return groups.SelectMany(g => g.Select(x => (Name: x.ToUpperInvariant(), Canonical: g[0].ToUpperInvariant())))
            .ToDictionary(x => x.Name, x => x.Canonical, StringComparer.Ordinal);
    }
}
