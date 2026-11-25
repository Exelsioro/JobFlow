using JobFlow.Domain;
using Microsoft.EntityFrameworkCore;

namespace JobFlow.Infrastructure;

public sealed class PreparationQueue(JobFlowDb db)
{
    public async Task<ScheduledTask> EnqueueAsync(Guid vacancyId, Guid workflowId, CancellationToken ct, bool outsideAgeLimit = false)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var task = await EnqueueCoreAsync(vacancyId, workflowId, ct, outsideAgeLimit);
        await transaction.CommitAsync(ct); return task;
    }

    public async Task<ScheduledTask> EnqueueClarificationAsync(Guid applicationId, string text, int revision, CancellationToken ct, bool outsideAgeLimit = false)
    {
        text = text.Trim();
        if (text.Length is < 1 or > 6000) throw new ArgumentException("Укажите уточнение длиной от 1 до 6000 символов.");
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var application = await db.Applications.SingleAsync(x => x.Id == applicationId, ct);
        if (application.Revision != revision) throw new InvalidOperationException("Отклик изменился. Обновите состояние перед передачей уточнения.");
        if (!ApplicationClarifications.CanRework(application.State) || await db.Set<ApplicationAttempt>().AnyAsync(x => x.JobApplicationId == applicationId, ct))
            throw new ArgumentException("Отправленный отклик или отклик с попыткой отправки нельзя вернуть на автоматическую доработку.");
        var notes = await ApplicationClarifications.ReadAsync(db, applicationId, ct);
        if (notes.Sum(x => x.Text.Length) + text.Length > 24000) throw new ArgumentException("Уточнения этой вакансии превысили лимит 24000 символов.");
        var workflowId = application.WorkflowDefinitionId ?? throw new ArgumentException("Для доработки отклик должен быть связан с workflow.");
        var previous = application.State;
        // Explicit revision of unsent materials invalidates prior approval and forces a NEW evaluation.
        application.State = ApplicationState.Evaluating; application.Revision++;
        var task = await EnqueueCoreAsync(application.VacancyId, workflowId, ct, outsideAgeLimit, isClarification: true);
        db.Audit.Add(ApplicationClarifications.Create(applicationId, text, task.Id));
        if (await db.Audit.AnyAsync(x => x.EntityId == applicationId && x.Action == ExternalApplicationDraft.AuditAction, ct))
            db.Audit.Add(new AuditEvent { EntityId = applicationId, EntityType = nameof(JobApplication), Action = ExternalApplicationDraft.InvalidatedAction,
                Details = "Материалы возвращены на доработку с уточнением. Внешнюю форму нужно подготовить заново." });
        db.Audit.Add(new AuditEvent { EntityId = applicationId, EntityType = nameof(JobApplication), Action = "StateChanged", Details = previous + " -> " + application.State });
        await db.SaveChangesAsync(ct); await transaction.CommitAsync(ct); return task;
    }

    private async Task<ScheduledTask> EnqueueCoreAsync(Guid vacancyId, Guid workflowId, CancellationToken ct, bool outsideAgeLimit, bool isClarification = false)
    {
        var vacancy = await db.Vacancies.SingleAsync(x => x.Id == vacancyId, ct);
        var workflow = await db.Set<WorkflowDefinition>().SingleAsync(x => x.Id == workflowId, ct);
        if (!workflow.Enabled || workflow.ExecutionMode != "browser" || workflow.SendsApprovedOnly() || !workflow.SelectedPlatforms().Contains(vacancy.JobPlatformId))
            throw new ArgumentException("Выберите включённый браузерный workflow для этой площадки.");
        if (vacancy.IsArchived || vacancy.Availability == "Closed") throw new ArgumentException("Вакансия удалена или закрыта. Подготовка недоступна.");
        if (!outsideAgeLimit && workflow.FreshnessDays is int days && vacancy.PublishedAt < DateTime.UtcNow.AddDays(-days))
            throw new ArgumentException("Вакансия старше лимита актуальности выбранного workflow.");
        var now = DateTime.UtcNow;
        var task = await db.Tasks.SingleOrDefaultAsync(x => x.Kind == "PrepareApplication" && x.CandidateProfileId == workflow.CandidateProfileId && x.VacancyId == vacancyId, ct);
        if (task != null && (task.RunRequested || task.LeaseUntil > now)) throw new InvalidOperationException("Подготовка этой вакансии уже выполняется или поставлена в очередь.");
        // A clarification revises the card immediately. A running batch could be editing that
        // same card; a confirmed single-vacancy run for a different card cannot.
        if (isClarification && (
            await db.Tasks.AnyAsync(x => x.WorkflowDefinitionId == workflowId && x.Kind == "Workflow" && x.LeaseUntil > now, ct) ||
            workflow.LeaseUntil > now && !await db.Set<WorkflowRun>().AnyAsync(x => x.WorkflowDefinitionId == workflowId &&
                x.Status == "Running" && x.PreparationOnly && x.TargetVacancyId != null && x.TargetVacancyId != vacancyId, ct)))
            throw new InvalidOperationException("Идёт общий прогон workflow. Чтобы не изменить отклик во время обработки, передайте уточнение после его завершения.");
        if (await db.Set<HistoricalApplication>().AnyAsync(x => x.CandidateProfileId == workflow.CandidateProfileId && x.VacancyId == vacancyId, ct))
            throw new InvalidOperationException("Для этой вакансии уже есть история отправки. Повторная подготовка запрещена.");
        var application = await db.Applications.SingleOrDefaultAsync(x => x.CandidateProfileId == workflow.CandidateProfileId && x.VacancyId == vacancyId, ct);
        if (application != null && (application.WorkflowDefinitionId != workflowId || application.State is not
            (ApplicationState.Discovered or ApplicationState.Evaluating or ApplicationState.Drafting or ApplicationState.Blocked or ApplicationState.Skipped)))
            throw new InvalidOperationException("Отклик уже подготовлен, отправлен, завершён или относится к другому workflow. Откройте существующий отклик.");
        if (task == null)
        {
            task = new ScheduledTask { Kind = "PrepareApplication", CandidateProfileId = workflow.CandidateProfileId, VacancyId = vacancyId };
            db.Add(task);
        }
        task.WorkflowDefinitionId = workflowId; task.Name = vacancy.Title; task.Enabled = false; task.RunRequested = true;
        task.PrepareOutsideAgeLimit = outsideAgeLimit;
        task.NextRunAt = DateTime.UtcNow; task.TimeoutSeconds = 2400; task.MaxRetries = 0; task.FailureCount = 0; task.CancelRequested = false;
        db.Audit.Add(new AuditEvent { EntityId = vacancyId, EntityType = nameof(Vacancy), Action = "ApplicationPreparationQueued",
            Details = System.Text.Json.JsonSerializer.Serialize(new { workflowId, taskId = task.Id, autoSubmit = false, discovery = false, outsideAgeLimit }) });
        await db.SaveChangesAsync(ct); return task;
    }
}
