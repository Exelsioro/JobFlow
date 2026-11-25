using System.Text.Json;
using JobFlow.Application;
using JobFlow.Domain;
using Microsoft.EntityFrameworkCore;

namespace JobFlow.Infrastructure;

public sealed class CoverLetterService(JobFlowDb db, IAgentExecutor agents)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<string> PrepareAsync(Guid writer, CandidateProfile candidate, Vacancy vacancy,
        EvaluationResult evaluation, WorkflowDefinition workflow, CancellationToken ct)
    {
        var factCatalog = LetterEvidence.Facts(candidate);
        string[] issues = [];
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var response = await agents.ExecuteAsync(new(writer,
                JsonSerializer.Serialize(new { candidate, factCatalog, facts = factCatalog.Select(x => x.Text), vacancy, evaluation,
                    workflow.Instructions, workflow.FreshnessDays, correctionIssues = issues }, Json),
                "Draft a vacancy-specific cover letter exclusively from the supplied factCatalog. Return citations with factId and an exact nonempty quote from that fact; a quote may be a substring, preserving its meaning and negation. " +
                "This operation's citations schema replaces older usedFacts instructions. Do not add qualifications, tenure, availability or experience. Unresolved requirements are questions, never candidate claims. " +
                "If correctionIssues are supplied, produce a corrected draft without repeating those issues.", AgentTemplates.LetterSchema), ct);
            await RetainOutputAsync(db, response, ct);
            LetterDraft? draft = null;
            try
            {
                using var doc = JsonDocument.Parse(response.Output);
                if (!global::Json.Schema.JsonSchema.FromText(AgentTemplates.LetterSchema).Evaluate(doc.RootElement).IsValid)
                    issues = ["Cover letter output does not match the citations schema."];
                else { draft = JsonSerializer.Deserialize<LetterDraft>(response.Output, Json)!; issues = LetterEvidence.Validate(draft, factCatalog); }
            }
            catch (JsonException) { issues = ["Cover letter output is not valid JSON."]; }
            if (issues.Length == 0 && draft != null)
            {
                var check = await agents.ExecuteAsync(new(writer,
                    JsonSerializer.Serialize(new { letter = draft.Letter, draft.Citations, factCatalog, candidate }, Json),
                    "Independently verify every factual candidate claim in the entire letter against factCatalog. Do not draft a letter for this operation; follow the verification schema. " +
                    "Valid citations alone are not proof: detect uncited or exaggerated claims, altered negations, invented skills, employers, tenure, proficiency, availability and achievements. " +
                    "Questions, aspirations, explicit unknowns and statements about the vacancy are not claims of candidate experience and MUST NOT appear in issues or unsupportedClaims. " +
                    "Do not flag an unresolved requirement merely because the letter asks about it. 7+ and at least seven are equivalent; use exact source wording for boundary-sensitive tenure. " +
                    "supported may be true only when all factual claims are supported. issues and unsupportedClaims must contain only concrete unsupported factual assertions, never general review reminders. " + ApplicationClarifications.EvidenceGuidance, AgentTemplates.LetterVerificationSchema), ct);
                await RetainOutputAsync(db, check, ct);
                try
                {
                    using var doc = JsonDocument.Parse(check.Output);
                    if (!global::Json.Schema.JsonSchema.FromText(AgentTemplates.LetterVerificationSchema).Evaluate(doc.RootElement).IsValid)
                        issues = ["Letter verification output does not match its schema."];
                    else
                    {
                        var result = JsonSerializer.Deserialize<Verification>(check.Output, Json)!;
                        issues = result.Issues.Concat(result.UnsupportedClaims).Distinct().ToArray();
                        if (!result.Supported && issues.Length == 0) issues = ["The verifier could not confirm the letter's factual claims."];
                    }
                }
                catch (JsonException) { issues = ["Letter verification output is not valid JSON."]; }
            }
            db.Audit.Add(new AuditEvent { EntityId = vacancy.Id, EntityType = nameof(Vacancy), Action = "CoverLetterValidated",
                Details = JsonSerializer.Serialize(new { workflowId = workflow.Id, vacancy.Url, attempt = attempt + 1,
                    response.ExecutionId, isValid = issues.Length == 0, issues }, Json) });
            await db.SaveChangesAsync(ct);
            if (issues.Length == 0) return draft!.Letter;
        }
        throw new HumanReviewRequiredException("Нужно проверить факты в письме после исправления: " + string.Join("; ", issues)[..Math.Min(1400, string.Join("; ", issues).Length)]);
    }

    public static async Task RetainOutputAsync(JobFlowDb db, AgentExecutionResult response, CancellationToken ct)
    {
        var execution = await db.Set<AgentExecution>().SingleOrDefaultAsync(x => x.Id == response.ExecutionId, ct);
        if (execution != null)
        {
            execution.Output = response.Output[..Math.Min(100000, response.Output.Length)];
            await db.SaveChangesAsync(ct);
        }
    }
    private sealed record Verification(bool Supported, string[] Issues, string[] UnsupportedClaims);
}
