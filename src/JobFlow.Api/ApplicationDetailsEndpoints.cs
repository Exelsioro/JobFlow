using System.Text.Json;
using JobFlow.Application;
using JobFlow.Domain;
using JobFlow.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace JobFlow.Api;

public static class ApplicationDetailsEndpoints
{
    public static void MapApplicationDetailsEndpoints(this WebApplication app)
    {
        app.MapPost("/api/applications/{id:guid}/clarifications", async (Guid id, ClarificationInput input, JobFlowDb db, JobSearchWorkflow workflow, CancellationToken ct) =>
        {
            var definitionId = await db.Applications.Where(x => x.Id == id).Select(x => x.WorkflowDefinitionId).SingleAsync(ct)
                ?? throw new ArgumentException("Для доработки нужен связанный workflow.");
            await workflow.ValidateAsync(await db.Set<WorkflowDefinition>().AsNoTracking().SingleAsync(x => x.Id == definitionId, ct), ct);
            var task = await new PreparationQueue(db).EnqueueClarificationAsync(id, input.Text ?? "", input.Revision, ct, input.OutsideAgeLimit);
            return Results.Ok(new { queued = true, taskId = task.Id, applicationId = id, autoSubmit = false });
        });
        app.MapGet("/api/applications/{id:guid}", async (Guid id, JobFlowDb db, HttpResponse response, CancellationToken ct) =>
        {
            response.Headers.CacheControl = "no-store";
            var application = await db.Applications.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
            if (application == null) return Results.NotFound(new { error = "Отклик не найден. Обновите список." });
            var vacancy = await db.Vacancies.AsNoTracking().SingleAsync(x => x.Id == application.VacancyId, ct);
            var resume = await db.ResumeVersions.AsNoTracking().SingleOrDefaultAsync(x => x.Id == application.ResumeVersionId, ct);
            var evaluation = await db.Set<VacancyEvaluation>().AsNoTracking()
                .Where(x => x.VacancyId == application.VacancyId && x.CandidateProfileId == application.CandidateProfileId)
                .OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.Id).FirstOrDefaultAsync(ct);
            var history = await db.Audit.AsNoTracking().Where(x => x.EntityId == id &&
                    (x.Action == "StateChanged" || x.Action == "BrowserApplicationBlocked" || x.Action == "ManualSubmissionRecorded"))
                .OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.Id).Take(16)
                .Select(x => new { x.Id, x.Action, x.Details, x.CreatedAt }).ToListAsync(ct);
            var attention = application.State == ApplicationState.Blocked
                ? await db.Audit.AsNoTracking().Where(x => x.EntityId == id && x.Action == "BrowserApplicationBlocked")
                    .OrderByDescending(x => x.CreatedAt).Select(x => x.Details).FirstOrDefaultAsync(ct) : null;
            if (application.State is ApplicationState.Skipped or ApplicationState.Closed)
            {
                var key = "\"applicationId\":\"" + id + "\"";
                var skipEvent = await db.Audit.AsNoTracking()
                    .Where(x => x.Action == "WorkflowVacancySkipped" && x.Details.Contains(key))
                    .OrderByDescending(x => x.CreatedAt).Select(x => x.Details).FirstOrDefaultAsync(ct);
                if (skipEvent != null)
                {
                    using var data = JsonDocument.Parse(skipEvent);
                    attention = data.RootElement.GetProperty("details").GetProperty("reason").GetString();
                }
            }
            var approvalBlockers = new List<string>();
            if (string.IsNullOrWhiteSpace(application.CoverLetter)) approvalBlockers.Add("Сначала подготовьте сопроводительное письмо.");
            var journalApprovalPending = await JournalApproval.Pending(db).AnyAsync(x => x.CandidateProfileId == application.CandidateProfileId, ct);
            if (journalApprovalPending)
                approvalBlockers.Add("Подтвердите импорт истории или отметьте, что старого журнала нет.");
            if (await db.Set<HistoricalApplication>().AnyAsync(x => x.CandidateProfileId == application.CandidateProfileId && x.VacancyId == application.VacancyId, ct))
                approvalBlockers.Add("В импортированной истории уже есть отклик на эту вакансию.");
            var allowedTransitions = Enum.GetValues<ApplicationState>().Where(state => Workflow.CanTransition(application.State, state) &&
                state is not (ApplicationState.Submitting or ApplicationState.Submitted or ApplicationState.SubmissionUnknown or ApplicationState.EmployerResponded))
                .Select(x => x.ToString()).ToArray();
            var external = await ExternalApplicationDraft.ReadAsync(db, id, ct);
            var externalDraft = external != null;
            var externalDraftNotice = external is { Outcome: "Suspicious" or "NotSent" or "Routed" }
                ? new { external.Outcome, external.Reason } : null;
            var clarifications = await ApplicationClarifications.ReadAsync(db, id, ct);
            var last = clarifications.LastOrDefault();
            ScheduledTask? task = last == null ? null : await db.Tasks.AsNoTracking().SingleOrDefaultAsync(x => x.Id == last.TaskId, ct);
            var taskRun = last == null ? null : await db.Runs.AsNoTracking().Where(x => x.ScheduledTaskId == last.TaskId && x.CreatedAt >= last.SubmittedAt)
                .OrderByDescending(x => x.CreatedAt).FirstOrDefaultAsync(ct);
            var clarificationStatus = task?.RunRequested == true ? "Queued" :
                task?.LeaseUntil > DateTime.UtcNow ? "Running" : taskRun?.Status ?? "Cancelled";
            if (taskRun is { Status: "Succeeded", FinishedAt: { } finishedAt } && clarificationStatus == "Succeeded")
            {
                var result = await db.Set<WorkflowRun>().AsNoTracking()
                    .Where(x => x.PreparationOnly && x.TargetVacancyId == application.VacancyId &&
                        x.WorkflowDefinitionId == application.WorkflowDefinitionId && x.CreatedAt >= taskRun.CreatedAt &&
                        x.FinishedAt <= finishedAt)
                    .OrderByDescending(x => x.CreatedAt).FirstOrDefaultAsync(ct);
                if (result is { Prepared: 0 })
                    clarificationStatus = result.Skipped > 0 ? "Skipped" : result.Rejected > 0 ? "Rejected" : "NotPrepared";
            }
            var clarification = last == null ? null : new { last.Text, last.SubmittedAt, last.TaskId, status = clarificationStatus };
            var evaluationIsCurrent = evaluation != null && (last == null || evaluation.CreatedAt >= last.SubmittedAt) &&
                application.State is not (ApplicationState.Skipped or ApplicationState.Closed);
            var canClarify = ApplicationClarifications.CanRework(application.State) && application.WorkflowDefinitionId.HasValue &&
                !await db.Set<ApplicationAttempt>().AnyAsync(x => x.JobApplicationId == id, ct);
            int? preparationAgeLimit = null;
            if (canClarify && !vacancy.IsArchived && vacancy.Availability != "Closed" && vacancy.PublishedAt is { } publishedAt)
            {
                var limit = await db.Set<WorkflowDefinition>().AsNoTracking().Where(x => x.Id == application.WorkflowDefinitionId)
                    .Select(x => x.FreshnessDays).SingleOrDefaultAsync(ct);
                if (limit is int days && publishedAt < DateTime.UtcNow.AddDays(-days))
                    preparationAgeLimit = days;
            }
            var ageOnlyAttention = attention?.StartsWith(
                "Browser read: Vacancy freshness or open status is unverified, closed, or outside the configured publication age. Publication date exceeds the configured age limit. Diagnostics:",
                StringComparison.Ordinal) == true;
            if (ageOnlyAttention)
                attention = "При прошлой проверке вакансия была старше лимита давности.";
            else if (attention?.Contains("freshness or open status is unverified", StringComparison.OrdinalIgnoreCase) == true &&
                     vacancy.Availability == "Unknown")
                attention = "Не удалось подтвердить, что вакансия открыта, или установить дату публикации. Вакансия не считается закрытой. Можно запустить повторную проверку.";
            return Results.Ok(new { application, vacancy, resume = resume == null ? null : new { resume.Id, resume.Version, resume.OriginalName,
                    resume.Origin, resume.SourceResumeVersionId, resume.CreatedAt }, evaluation, evaluationIsCurrent, attention, history, allowedTransitions, approvalBlockers, journalApprovalPending,
                externalDraft, externalDraftNotice, clarification, canClarify, preparationAgeLimit, ageOnlyAttention,
                clarificationHistory = clarifications.TakeLast(10), observedAt = DateTime.UtcNow });
        });
        app.MapGet("/api/resumes/versions/{id:guid}/preview", async (Guid id, JobFlowDb db, HttpResponse response, CancellationToken ct) =>
        {
            response.Headers.CacheControl = "no-store";
            var version = await db.ResumeVersions.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
            if (version == null || !File.Exists(ResumeAgentService.PathFor(version))) return Results.NotFound();
            return Results.File(ResumeAgentService.PathFor(version), "application/pdf", enableRangeProcessing: true);
        });
    }
    public sealed record ClarificationInput(string? Text, int Revision, bool OutsideAgeLimit = false);
}
