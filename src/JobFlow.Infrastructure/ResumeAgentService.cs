using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using JobFlow.Application;
using JobFlow.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Playwright;
using UglyToad.PdfPig;

namespace JobFlow.Infrastructure;

public sealed class ResumeAgentService(JobFlowDb db, IAgentExecutor agents)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public const string SelectionSchema = """{"type":"object","properties":{"resumeVersionId":{"type":"string"},"reason":{"type":"string"}},"required":["resumeVersionId","reason"],"additionalProperties":false}""";
    private const string LegacyAdaptSchema = """{"type":"object","properties":{"title":{"type":"string"},"sections":{"type":"array","items":{"type":"object","properties":{"heading":{"type":"string"},"text":{"type":"string"},"evidence":{"type":"array","items":{"type":"string"}}},"required":["heading","text","evidence"],"additionalProperties":false}}},"required":["title","sections"],"additionalProperties":false}""";
    public const string AdaptSchema = """
        {
          "type":"object",
          "properties":{
            "title":{"type":"string"},
            "sections":{"type":"array","items":{
              "type":"object",
              "properties":{
                "heading":{"type":"string"},
                "claims":{"type":"array","items":{
                  "type":"object",
                  "properties":{
                    "text":{"type":"string"},
                    "source":{"type":"string","enum":["dossier","sourceResume"]},
                    "quote":{"type":"string"}
                  },
                  "required":["text","source","quote"],
                  "additionalProperties":false
                }}
              },
              "required":["heading","claims"],
              "additionalProperties":false
            }}
          },
          "required":["title","sections"],
          "additionalProperties":false
        }
        """;
    public const string VerifySchema = """{"type":"object","properties":{"supported":{"type":"boolean"},"issues":{"type":"array","items":{"type":"string"}}},"required":["supported","issues"],"additionalProperties":false}""";
    public const string DossierSchema = """{"type":"object","properties":{"experience":{"type":"string"},"skills":{"type":"string"},"education":{"type":"string"},"languages":{"type":"string"},"facts":{"type":"array","items":{"type":"string"}}},"required":["experience","skills","education","languages","facts"],"additionalProperties":false}""";
    private const string PreserveMeaning = """
        Preserve product names, identifiers, acronyms and precise engineering terminology. Use a conventional equivalent only if it has the same meaning; otherwise keep the original technical term in the localized sentence. In particular, "smoke tests" may be "smoke-тесты" but does not imply "checks after deployment". Accept exact semantic equivalents and original technical terms, not just literal matches across languages.
        Preserve which employer, project, action or component each technology, qualifier or result belongs to. Do not infer timing or sequencing, even from neighboring activities. Do not move a language or tool from one task to another, or attach a compatibility constraint to a different component. Preserve negation and singular/plural when they affect the fact.
        """;

    public static string PathFor(ResumeVersion version) => Path.Combine(Storage.Root, "resumes", version.StorageKey);
    public static string Dossier(CandidateProfile profile) => string.Join("\n", new[] { profile.Name, profile.Email, profile.Experience, profile.Skills, profile.Education, profile.Languages, profile.Dossier });
    public static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    public static string Extract(string path)
    {
        using var pdf = PdfDocument.Open(path);
        if (pdf.NumberOfPages > 50) throw new InvalidOperationException("Resume exceeds 50 pages.");
        var text = string.Join("\n", pdf.GetPages().Select(p => string.Join(" ", p.GetWords().Select(w => w.Text))));
        if (text.Trim().Length < 20) throw new InvalidOperationException("PDF has no readable text. Supply a text PDF or fill the dossier manually; scanned PDFs require OCR.");
        if (text.Length > 80000) throw new InvalidOperationException("Resume text exceeds 80000 characters.");
        return text;
    }
    public async Task<string> TextAsync(ResumeVersion version, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(version.ExtractedText))
        {
            version.ExtractedText = Extract(PathFor(version));
            await db.SaveChangesAsync(ct);
        }
        return version.ExtractedText;
    }
    public async Task<DossierProposal> ProposeAsync(Guid candidateId, Guid versionId, Guid agentId, CancellationToken ct)
    {
        await RequireAgentAsync(agentId, candidateId, ct);
        var version = await OwnedVersionAsync(candidateId, versionId, ct);
        var text = await TextAsync(version, ct);
        var result = await agents.ExecuteAsync(new(agentId, JsonSerializer.Serialize(new { resumeText = text }, Json),
            "Extract a candidate dossier from this resume. Do not infer proficiency, dates or achievements. facts must be exact source quotes. Return empty fields for absent facts. This is a proposal requiring candidate confirmation.", DossierSchema), ct);
        using var doc = Parse(result.Output, DossierSchema);
        var facts = doc.RootElement.GetProperty("facts").EnumerateArray().Select(x => x.GetString()!).ToArray();
        if (facts.Length == 0 || facts.Any(x => string.IsNullOrWhiteSpace(x) || !text.Contains(x, StringComparison.Ordinal)))
            throw new InvalidOperationException("The dossier proposal cites unsupported source facts.");
        var proposal = new DossierProposal { CandidateProfileId = candidateId, ResumeVersionId = versionId, AgentExecutionId = result.ExecutionId,
            FactsJson = JsonSerializer.Serialize(facts), ProfileJson = doc.RootElement.GetRawText() };
        db.Add(proposal); await db.SaveChangesAsync(ct); return proposal;
    }
    public async Task ConfirmAsync(Guid proposalId, CancellationToken ct)
    {
        var proposal = await db.Set<DossierProposal>().SingleAsync(x => x.Id == proposalId, ct);
        if (proposal.ConfirmedAt != null) return;
        var profile = await db.Profiles.SingleAsync(x => x.Id == proposal.CandidateProfileId, ct);
        using var doc = JsonDocument.Parse(proposal.ProfileJson);
        var value = doc.RootElement;
        profile.Experience = value.GetProperty("experience").GetString()!;
        profile.Skills = value.GetProperty("skills").GetString()!;
        profile.Education = value.GetProperty("education").GetString()!;
        profile.Languages = value.GetProperty("languages").GetString()!;
        profile.Dossier = string.Join("\n", JsonSerializer.Deserialize<string[]>(proposal.FactsJson)!);
        proposal.ConfirmedAt = DateTime.UtcNow;
        db.Audit.Add(new AuditEvent { EntityId = profile.Id, EntityType = nameof(CandidateProfile), Action = "DossierConfirmed", Details = proposal.Id.ToString() });
        await db.SaveChangesAsync(ct);
    }
    public async Task PrepareAsync(WorkflowDefinition workflow, CandidateProfile candidate, Vacancy vacancy, JobApplication application, CancellationToken ct)
    {
        if (workflow.ResumeMode == "fixed") { application.ResumeVersionId = workflow.ResumeVersionId; return; }
        var agentId = workflow.ResumeAgentId ?? throw new InvalidOperationException("Select a resume agent.");
        var previousResume = await db.ResumeVersions.AsNoTracking().SingleOrDefaultAsync(x => x.Id == application.ResumeVersionId, ct);
        await RequireAgentAsync(agentId, candidate.Id, ct);
        var versions = await (from version in db.ResumeVersions join resume in db.Resumes on version.ResumeId equals resume.Id
                              where resume.CandidateProfileId == candidate.Id && version.Origin == "Uploaded"
                              orderby version.CreatedAt descending select version).ToListAsync(ct);
        if (versions.Count is < 1 or > 30) throw new InvalidOperationException("Resume selection requires 1–30 uploaded versions.");
        var choices = new List<object>(); var sourceText = new Dictionary<Guid, string>();
        foreach (var v in versions) { var text = await TextAsync(v, ct); sourceText[v.Id] = text; choices.Add(new { resumeVersionId = v.Id, v.OriginalName, text }); }
        if (sourceText.Values.Sum(x => x.Length) > 180000) throw new InvalidOperationException("Resume selection exceeds the text budget. Reduce the number of source resume versions.");
        var selected = await agents.ExecuteAsync(new(agentId, JsonSerializer.Serialize(new { dossier = Dossier(candidate), vacancy, resumes = choices, workflow.Instructions }, Json),
            "Select the most suitable uploaded resume for this vacancy using only supplied evidence. Return an exact resumeVersionId from the available choices and explain why. Never select an invented ID.", SelectionSchema), ct);
        using var selection = Parse(selected.Output, SelectionSchema);
        if (!Guid.TryParse(selection.RootElement.GetProperty("resumeVersionId").GetString(), out var id) || !sourceText.ContainsKey(id))
            throw new InvalidOperationException("Resume agent selected a version outside this candidate's uploaded resumes.");
        var source = versions.Single(x => x.Id == id);
        application.ResumeVersionId = source.Id;
        db.Audit.Add(new AuditEvent { EntityType = nameof(JobApplication), EntityId = application.Id, Action = "ResumeSelected", Details = source.Id + ": " + selection.RootElement.GetProperty("reason").GetString() });
        if (workflow.ResumeMode == "select") { await db.SaveChangesAsync(ct); return; }
        var sources = new { dossier = Dossier(candidate), sourceResume = sourceText[id] };
        var normalizedDossier = EvidenceText.Normalize(sources.dossier);
        var normalizedResume = EvidenceText.Normalize(sources.sourceResume);
        AgentExecutionResult result = null!;
        JsonDocument? document = null;
        JsonElement? previousDraft = null;
        if (previousResume?.Origin == "AgentGenerated" && previousResume.AdaptationJson.Length > 0)
        {
            // Old drafts are context for edits, never evidence or a renderable document.
            try { using var saved = Parse(previousResume.AdaptationJson, AdaptSchema, LegacyAdaptSchema); previousDraft = saved.RootElement.Clone(); }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException) { }
        }
        string[] issues = [];
        var supported = false;
        try
        {
            for (var attempt = 1; attempt <= 2; attempt++)
            {
                result = await agents.ExecuteAsync(new(agentId, JsonSerializer.Serialize(new { sources, vacancy, workflow.Instructions,
                    correctionIssues = issues, previousDraft = document?.RootElement ?? previousDraft }, Json),
                    """
                    Adapt the resume by selecting and reordering supplied facts, never adding new facts. Use only claims for the section body; do not return section-wide prose or evidence.
                    For each claim, FIRST select one contiguous quote from the named source (dossier or sourceResume) that supports the entire claim as a unit, including its context. THEN write one plain-text resume statement based on that quote. Keep the quote in its original language. Split independent facts into separate claims with separate quotes. Neither the vacancy nor previousDraft is evidence.
                    Use the language explicitly requested by the vacancy, otherwise the language of its actual description, for the title, headings and claims. Use factual-neutral labels or supported names in the title and headings, not extra claims. Do not include review notes or eligibility questions.
                    If correctionIssues are supplied, keep supported claims and repair the flagged ones. Use the closest faithful wording to the original quote or omit the unsupported claim; do not invent a replacement.
                    """ + "\n" + PreserveMeaning + "\n" + ApplicationClarifications.EvidenceGuidance, AdaptSchema), ct);
                await CoverLetterService.RetainOutputAsync(db, result, ct);
                document?.Dispose(); document = null;
                try { document = Parse(result.Output, AdaptSchema); }
                catch (Exception ex) when (ex is JsonException or InvalidOperationException)
                { issues = ["Ответ адаптации не соответствует структуре резюме."]; }
                if (document != null)
                {
                    var draftSections = document.RootElement.GetProperty("sections").EnumerateArray().ToArray();
                    var evidenceIssues = new List<string>();
                    if (draftSections.Length is < 1 or > 20 || result.Output.Length > 80000) evidenceIssues.Add("Нужно 1–20 разделов и не более 80000 символов.");
                    foreach (var section in draftSections)
                    {
                        var heading = section.GetProperty("heading").GetString();
                        var claims = section.GetProperty("claims").EnumerateArray().ToArray();
                        if (claims.Length == 0) evidenceIssues.Add($"Раздел «{heading}»: нет утверждений с источниками.");
                        for (var index = 0; index < claims.Length; index++)
                        {
                            var claim = claims[index];
                            var sourceName = claim.GetProperty("source").GetString();
                            var sourceTextForClaim = sourceName == "dossier" ? normalizedDossier : normalizedResume;
                            var quote = EvidenceText.Normalize(claim.GetProperty("quote").GetString()!);
                            if (string.IsNullOrWhiteSpace(claim.GetProperty("text").GetString()) ||
                                quote.Length == 0 || !sourceTextForClaim.Contains(quote, StringComparison.Ordinal))
                                evidenceIssues.Add($"Раздел «{heading}», утверждение {index + 1}: нужен непустой текст и точная цитата из указанного источника ({sourceName}).");
                        }
                    }
                    issues = evidenceIssues.ToArray();
                    if (issues.Length == 0)
                    {
                        var check = await agents.ExecuteAsync(new(agentId, JsonSerializer.Serialize(new { sources, adapted = document.RootElement }, Json),
                            """
                            Verify every claim against its own quote and that quote's context in the named source. An exact quote is not enough: it must support the full meaning, employer, task and scope of the claim. Do not use evidence belonging to a different claim to make up for a missing link. Also check candidate assertions in the title and headings against the sources.
                            Do not block a faithful translation, harmless paraphrase, or original technical term. Return supported=false only for concrete unsupported factual assertions. Each issue must identify the affected wording, what changed compared to its source, and the smallest safe correction. No general review reminders or style edits.
                            """ + "\n" + PreserveMeaning + "\n" + ApplicationClarifications.EvidenceGuidance, VerifySchema), ct);
                        await CoverLetterService.RetainOutputAsync(db, check, ct);
                        using var verification = Parse(check.Output, VerifySchema);
                        issues = verification.RootElement.GetProperty("issues").EnumerateArray().Select(x => x.GetString()!).ToArray();
                        if (!verification.RootElement.GetProperty("supported").GetBoolean() && issues.Length == 0)
                            issues = ["Проверяющий не подтвердил фактические утверждения резюме."];
                    }
                }
                supported = issues.Length == 0;
                db.Audit.Add(new AuditEvent { EntityId = application.Id, EntityType = nameof(JobApplication), Action = "ResumeVerificationCompleted",
                    Details = JsonSerializer.Serialize(new { vacancyId = vacancy.Id, sourceResumeVersionId = source.Id,
                        adaptationExecutionId = result.ExecutionId, attempt, supported, issues }, Json) });
                await db.SaveChangesAsync(ct);
                if (supported) break;
            }
            if (!supported) throw new HumanReviewRequiredException("Нужно проверить факты в резюме после исправления: " + string.Join("; ", issues));
        }
        catch { document?.Dispose(); throw; }
        using var adapted = document!;
        var root = adapted.RootElement;
        var sections = root.GetProperty("sections").EnumerateArray().Select(section => (
            Heading: section.GetProperty("heading").GetString()!,
            Text: string.Join("\n", section.GetProperty("claims").EnumerateArray().Select(claim => claim.GetProperty("text").GetString())))).ToArray();
        var title = root.GetProperty("title").GetString()!;
        var textContent = title + "\n" + string.Join("\n\n", sections.Select(section => section.Heading + "\n" + section.Text));
        var bytes = await RenderAsync(title, sections, ct);
        var generated = new ResumeVersion { ResumeId = source.ResumeId, SourceResumeVersionId = source.Id, VacancyId = vacancy.Id, AgentExecutionId = result.ExecutionId,
            Origin = "AgentGenerated", OriginalName = "resume-" + vacancy.Id.ToString("N")[..8] + ".pdf", StorageKey = "generated/" + Guid.NewGuid().ToString("N") + ".pdf",
            Version = (await db.ResumeVersions.Where(x => x.ResumeId == source.ResumeId).MaxAsync(x => (int?)x.Version, ct) ?? 0) + 1,
            Sha256 = Convert.ToHexString(SHA256.HashData(bytes)), ExtractedText = textContent, AdaptationJson = result.Output,
            DossierHash = Hash(sources.dossier + "\nSource resume:\n" + sources.sourceResume) };
        var path = PathFor(generated); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, bytes, ct);
        try
        {
            db.Add(generated); application.ResumeVersionId = generated.Id;
            db.Audit.Add(new AuditEvent { EntityId = application.Id, EntityType = nameof(JobApplication), Action = "ResumePdfCreated",
                Details = JsonSerializer.Serialize(new { resumeVersionId = generated.Id, sourceResumeVersionId = source.Id, vacancyId = vacancy.Id, generated.Sha256, bytes = bytes.Length }, Json) });
            await db.SaveChangesAsync(ct);
        }
        catch { File.Delete(path); throw; }
    }
    private async Task RequireAgentAsync(Guid id, Guid candidate, CancellationToken ct)
    {
        if (!await db.Agents.AnyAsync(x => x.Id == id && x.CandidateProfileId == candidate && x.Enabled, ct)) throw new InvalidOperationException("Select an enabled agent belonging to this candidate.");
    }
    private async Task<ResumeVersion> OwnedVersionAsync(Guid candidate, Guid id, CancellationToken ct) => await
        (from v in db.ResumeVersions join r in db.Resumes on v.ResumeId equals r.Id where v.Id == id && r.CandidateProfileId == candidate select v).SingleAsync(ct);
    private static JsonDocument Parse(string output, string schema, string? legacySchema = null)
    {
        var doc = JsonDocument.Parse(output);
        if (global::Json.Schema.JsonSchema.FromText(schema).Evaluate(doc.RootElement).IsValid ||
            legacySchema != null && global::Json.Schema.JsonSchema.FromText(legacySchema).Evaluate(doc.RootElement).IsValid) return doc;
        doc.Dispose();
        throw new InvalidOperationException("Resume agent output does not match the task schema.");
    }
    public static async Task<byte[]> RenderAsync(string title, IEnumerable<(string Heading, string Text)> sections, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var playwright = await Playwright.CreateAsync();
        static string E(string text) => WebUtility.HtmlEncode(text).Replace("\n", "<br>");
        var html = "<!doctype html><meta charset=utf-8><style>@page{size:A4;margin:18mm}body{font:11pt Arial,sans-serif;line-height:1.4;color:#111}h1{font-size:19pt}h2{font-size:13pt;break-after:avoid}p{margin:0 0 12pt;overflow-wrap:anywhere}</style><h1>" + E(title) + "</h1>" + string.Join("", sections.Select(s => "<section><h2>" + E(s.Heading) + "</h2><p>" + E(s.Text) + "</p></section>"));
        for (var attempt = 0; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true, Timeout = 90000 }).WaitAsync(ct);
                var page = await browser.NewPageAsync().WaitAsync(TimeSpan.FromSeconds(90), ct);
                await page.RouteAsync("**/*", r => r.AbortAsync());
                await page.SetContentAsync(html, new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 90000 }).WaitAsync(ct);
                var bytes = await page.PdfAsync(new() { Format = "A4", PreferCSSPageSize = true, Tagged = true }).WaitAsync(TimeSpan.FromSeconds(90), ct);
                ct.ThrowIfCancellationRequested(); return bytes;
            }
            catch (Exception ex) when (attempt == 0 && !ct.IsCancellationRequested && (ex is PlaywrightException or TimeoutException))
            {
                // Retry only offline rendering in a fresh browser; never repeat agent calls or submission.
            }
        }
    }
}
