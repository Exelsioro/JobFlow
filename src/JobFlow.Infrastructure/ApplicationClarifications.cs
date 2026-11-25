using System.Text.Json;
using JobFlow.Domain;
using Microsoft.EntityFrameworkCore;

namespace JobFlow.Infrastructure;

public sealed record ApplicationClarification(Guid Id, string Text, DateTime SubmittedAt, Guid TaskId);

public static class ApplicationClarifications
{
    public const string Submitted = "ApplicationClarificationSubmitted";
    public const string EvidenceGuidance = "Use the vacancy-specific candidate clarifications in the dossier, when supplied, for this vacancy only. " +
        "They can contain factual confirmations or editing requests: only explicit factual confirmations are evidence of experience. " +
        "The latest explicit confirmation may correct an older profile fact for this vacancy. Editing requests do not establish qualifications. ";
    public const string Guidance = EvidenceGuidance + "Revise previous materials where provided, within the current operation. Never bypass freshness, citation or submission checks.";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public static bool CanRework(ApplicationState state) => state is ApplicationState.Discovered or ApplicationState.Evaluating or
        ApplicationState.Drafting or ApplicationState.Blocked or ApplicationState.Skipped or ApplicationState.Rejected or
        ApplicationState.ReadyForReview or ApplicationState.ReadyToSubmit;

    public static async Task<ApplicationClarification[]> ReadAsync(JobFlowDb db, Guid applicationId, CancellationToken ct)
    {
        var events = await db.Audit.AsNoTracking().Where(x => x.EntityId == applicationId && x.Action == Submitted)
            .OrderBy(x => x.CreatedAt).ThenBy(x => x.Id).ToListAsync(ct);
        return events.Select(x => JsonSerializer.Deserialize<ApplicationClarification>(x.Details, Json)!).ToArray();
    }
    public static AuditEvent Create(Guid applicationId, string text, Guid taskId)
    {
        var entry = new AuditEvent { EntityId = applicationId, EntityType = nameof(JobApplication), Action = Submitted };
        entry.Details = JsonSerializer.Serialize(new ApplicationClarification(entry.Id, text, entry.CreatedAt, taskId), Json);
        return entry;
    }
    public static (CandidateProfile Candidate, WorkflowDefinition Workflow) Context(CandidateProfile source, WorkflowDefinition definition,
        ApplicationClarification[] clarifications, JobApplication application, string? previousIssues)
    {
        var candidate = JsonSerializer.Deserialize<CandidateProfile>(JsonSerializer.Serialize(source, Json), Json)!;
        var workflow = JsonSerializer.Deserialize<WorkflowDefinition>(JsonSerializer.Serialize(definition, Json), Json)!;
        candidate.Dossier += "\n\nVacancy-specific candidate clarifications (oldest to newest; explicit confirmations only):\n" +
            string.Join("\n", clarifications.Select(x => $"[{x.SubmittedAt:O}]\n{x.Text}"));
        workflow.Instructions += "\n" + Guidance + "\nRework context (data): " + JsonSerializer.Serialize(new {
            clarifications = clarifications.Select(x => x.Text), previousCoverLetter = application.CoverLetter, previousIssues }, Json);
        return (candidate, workflow);
    }
}
