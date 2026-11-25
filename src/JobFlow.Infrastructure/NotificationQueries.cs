using JobFlow.Domain;
using Microsoft.EntityFrameworkCore;
using System.Text.RegularExpressions;

namespace JobFlow.Infrastructure;

public static class NotificationQueries
{
    public static async Task<object[]> ReadAsync(JobFlowDb db, CancellationToken ct)
    {
        var notifications = await db.Set<Notification>().AsNoTracking().OrderByDescending(x => x.CreatedAt).Take(300).ToListAsync(ct);
        var applications = await db.Applications.AsNoTracking().ToDictionaryAsync(x => x.Id, ct);
        var vacancies = await db.Vacancies.AsNoTracking().ToDictionaryAsync(x => x.Id, ct);
        var assessments = await db.Set<VacancyEvaluation>().AsNoTracking().OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.Id).ToListAsync(ct);
        var taskRuns = await db.Runs.AsNoTracking().ToDictionaryAsync(x => x.Id, ct);
        var tasks = await db.Tasks.AsNoTracking().ToDictionaryAsync(x => x.Id, ct);
        var runs = await db.Set<WorkflowRun>().AsNoTracking().OrderBy(x => x.CreatedAt).ToListAsync(ct);
        return notifications.Select(n =>
        {
            applications.TryGetValue(n.RelatedId ?? Guid.Empty, out var application);
            taskRuns.TryGetValue(n.RelatedId ?? Guid.Empty, out var taskRun);
            var task = taskRun == null ? null : tasks.GetValueOrDefault(taskRun.ScheduledTaskId);
            if (application == null && task?.VacancyId is Guid target)
                application = applications.Values.FirstOrDefault(x => x.VacancyId == target && x.CandidateProfileId == n.CandidateProfileId);
            var vacancy = application != null ? vacancies.GetValueOrDefault(application.VacancyId) : vacancies.Values.FirstOrDefault(x => n.Url.Length > 0 && x.Url == n.Url);
            var assessment = vacancy == null ? null : assessments.FirstOrDefault(x => x.VacancyId == vacancy.Id && x.CandidateProfileId == n.CandidateProfileId);
            var run = task?.WorkflowDefinitionId == null ? null : runs.FirstOrDefault(x => x.WorkflowDefinitionId == task.WorkflowDefinitionId &&
                x.CreatedAt >= taskRun!.CreatedAt && x.CreatedAt <= (taskRun.FinishedAt ?? DateTime.UtcNow));
            var (kind, title, message) = Present(n, vacancy);
            return (object)new { n.Id, n.CandidateProfileId, Kind = kind, Title = title, Message = message, n.Url, n.RelatedId, n.ReadAt, n.CreatedAt,
                rawMessage = n.Message, vacancyId = vacancy?.Id ?? task?.VacancyId, applicationId = application?.Id,
                evaluationId = assessment?.Id, workflowRunId = run?.Id, applicationState = application?.State.ToString() };
        }).ToArray();
    }

    public static (string Kind, string Title, string Message) Present(Notification notification, Vacancy? vacancy)
    {
        var message = notification.Message;
        var kind = notification.Kind;
        var title = notification.Title;
        if (message.Contains("Publication date exceeds the configured age limit", StringComparison.Ordinal))
            return ("Information", "Вакансия пропущена", (vacancy?.Title ?? "Вакансия") + ": дата публикации выходит за лимит актуальности workflow.");
        if (message.Contains("Vacancy is not confirmed open: Closed", StringComparison.Ordinal) || message.Contains("Vacancy is closed or archived", StringComparison.Ordinal))
        {
            if (vacancy?.Availability == "Unknown")
                return ("Review", "Статус вакансии не подтверждён", vacancy.Title + ": требуется повторная проверка страницы.");
            return ("Information", "Вакансия закрыта", (vacancy?.Title ?? "Вакансия") + ": работодатель больше не принимает отклики.");
        }
        if (kind == "Attention")
        {
            var review = message.Contains("verification", StringComparison.OrdinalIgnoreCase) || message.Contains("unsupported or missing evidence", StringComparison.Ordinal) || message.StartsWith("NeedsAttention:", StringComparison.Ordinal) ||
                message.Contains("freshness or open status", StringComparison.Ordinal) || message.Contains("Browser needs attention", StringComparison.Ordinal) || title.Contains("ответ", StringComparison.OrdinalIgnoreCase);
            kind = review ? "Review" : "Error";
        }
        if (kind == "Review" && !message.StartsWith("NeedsAttention:", StringComparison.Ordinal))
        {
            var context = vacancy?.Title ?? "Отклик";
            if (message.Contains("Resume verification", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("Adapted resume has unsupported", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("Cover letter evidence verification", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("AI cited facts not present", StringComparison.OrdinalIgnoreCase))
                return (kind, "Документы не прошли проверку", context + ": проверка нашла проблему в подготовленных материалах. Причина указана в подробностях.");
            return (kind, "Подготовка требует проверки", context + ": обработка остановилась. Причина указана в подробностях.");
        }
        if (kind == "Error") return (kind, "Ошибка выполнения", (vacancy?.Title ?? "Задание") + ": не удалось выполнить действие. Откройте подробности и диагностику.");
        message = Regex.Replace(message, @"\s*Diagnostics:\s*/api/browser-diagnostics/[0-9a-f-]+", "", RegexOptions.IgnoreCase);
        message = message.Replace("NeedsAttention:", "Завершено, нужна проверка:").Replace("Succeeded:", "Завершено:").Replace("Failed:", "Не удалось выполнить:");
        return (kind, title, message);
    }
}
