using JobFlow.Application;
using JobFlow.Domain;
using Microsoft.EntityFrameworkCore;

namespace JobFlow.Infrastructure;

public sealed class BrowserSubmissionService(JobFlowDb db, IBrowserWorkflowDriver browser)
{
    internal const string SubmissionWorkflow = "BrowserSubmissionWorkflow";
    public async Task<ApplicationAttempt?> SubmitAsync(WorkflowDefinition workflow, JobApplication application,
        JobPlatform platform, CandidateProfile candidate, Vacancy vacancy, CancellationToken ct,
        Func<Func<Task>, Task<BrowserResult>>? externalSubmit = null, string? externalVacancyUrl = null)
    {
        if (workflow.ExecutionMode != "browser" || !workflow.AutoSubmit || application.IsTest || application.State != ApplicationState.ReadyToSubmit)
            throw new InvalidOperationException("Automatic browser submission requires an enabled browser workflow and a ready application.");
        var effectiveUrl = externalVacancyUrl ?? vacancy.Url;
        var key = VacancyIdentity.SubmissionKey(candidate.Id, effectiveUrl);
        var previous = await db.Set<ApplicationAttempt>().AsNoTracking().SingleOrDefaultAsync(x => x.IdempotencyKey == key, ct);
        if (previous != null)
        {
            Block(application, "An application attempt already exists for this candidate and vacancy URL. Do not submit again.");
            await db.SaveChangesAsync(ct); return previous;
        }
        var related = await (from app in db.Applications.AsNoTracking()
                             join job in db.Vacancies.AsNoTracking() on app.VacancyId equals job.Id
                             where app.CandidateProfileId == candidate.Id && app.Id != application.Id && !app.IsTest &&
                                 (app.State == ApplicationState.Submitted || app.State == ApplicationState.EmployerResponded ||
                                  app.State == ApplicationState.Submitting || app.State == ApplicationState.SubmissionUnknown || app.State == ApplicationState.Closed)
                             select job.Url).ToListAsync(ct);
        var history = await (from app in db.Set<HistoricalApplication>().AsNoTracking()
                             join job in db.Vacancies.AsNoTracking() on app.VacancyId equals job.Id
                             where app.CandidateProfileId == candidate.Id select job.Url).ToListAsync(ct);
        var identity = VacancyIdentity.CanonicalUrl(effectiveUrl);
        var viaAnotherBoard = await (from referral in db.Audit
            join app in db.Applications on referral.EntityId equals app.Id
            where referral.Action == ExternalApplicationDraft.PlatformReferralAction && referral.Details == identity &&
                app.Id != application.Id && app.CandidateProfileId == candidate.Id &&
                (app.State == ApplicationState.Submitted || app.State == ApplicationState.EmployerResponded ||
                 app.State == ApplicationState.Submitting || app.State == ApplicationState.SubmissionUnknown || app.State == ApplicationState.Closed)
            select referral.Id).AnyAsync(ct);
        if (viaAnotherBoard || related.Concat(history).Any(url => Uri.TryCreate(url, UriKind.Absolute, out _) && VacancyIdentity.CanonicalUrl(url) == identity))
        {
            Block(application, "Application history for this vacancy URL blocks a duplicate submission.");
            await db.SaveChangesAsync(ct); return null;
        }
        var pendingJournals = workflow.SendsApprovedOnly() ? JournalApproval.Pending(db) :
            db.Set<Integration>().Where(x => (x.Kind == "csv" || x.Kind == "google-sheets") && !x.ImportConfirmed);
        if (await pendingJournals.AnyAsync(x => x.CandidateProfileId == candidate.Id, ct))
            throw new InvalidOperationException("Confirm journal imports before automatic submission.");
        var version = await (from v in db.ResumeVersions join r in db.Resumes on v.ResumeId equals r.Id
                             where v.Id == application.ResumeVersionId && r.CandidateProfileId == candidate.Id select v).SingleAsync(ct);
        var resume = Path.Combine(Storage.Root, "resumes", version.StorageKey);
        if (version.Origin == "AgentGenerated")
        {
            var source = await db.ResumeVersions.SingleAsync(x => x.Id == version.SourceResumeVersionId, ct);
            var dossier = ResumeAgentService.Dossier(candidate) + "\nSource resume:\n" + source.ExtractedText;
            if (version.VacancyId != vacancy.Id || version.DossierHash != ResumeAgentService.Hash(dossier))
                throw new InvalidOperationException("The generated PDF belongs to another vacancy or the dossier changed. Review and prepare the application again.");
        }
        if (!File.Exists(resume)) throw new InvalidOperationException("The selected resume file is missing.");
        ApplicationAttempt? attempt = null;
        try
        {
            async Task BeforeSubmit()
            {
                ct.ThrowIfCancellationRequested();
                // Persist the unique attempt and application state before any final submission click.
                await using var tx = await db.Database.BeginTransactionAsync(ct);
                var expectedRevision = application.Revision;
                var claimed = await db.Applications.Where(x => x.Id == application.Id && x.Revision == expectedRevision && x.State == ApplicationState.ReadyToSubmit)
                    .ExecuteUpdateAsync(s => s.SetProperty(x => x.State, ApplicationState.Submitting).SetProperty(x => x.Revision, x => x.Revision + 1), ct);
                if (claimed != 1) throw new DbUpdateConcurrencyException("Application changed before the browser submission.");
                var pending = new ApplicationAttempt { JobApplicationId = application.Id, IdempotencyKey = key, Outcome = "Pending" };
                db.Add(pending);
                if (application.WorkflowDefinitionId != workflow.Id)
                    db.Audit.Add(new AuditEvent { EntityId = application.Id, EntityType = nameof(JobApplication), Action = SubmissionWorkflow, Details = workflow.Id.ToString() });
                await db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
                await db.Entry(application).ReloadAsync(CancellationToken.None);
                attempt = pending;
            }
            var result = externalSubmit == null
                ? await browser.ApplyAsync(workflow, platform, candidate, vacancy, application, resume, BeforeSubmit, ct)
                : await externalSubmit(BeforeSubmit);
            if (attempt == null) throw new InvalidOperationException("Browser returned without recording a submission attempt.");
            attempt.Outcome = result.Confirmed ? "Confirmed" : "SubmissionUnknown";
            attempt.Evidence = result.Evidence[..Math.Min(2000, result.Evidence.Length)];
            Workflow.Transition(application, result.Confirmed ? ApplicationState.Submitted : ApplicationState.SubmissionUnknown);
        }
        catch (ModelProviderException ex)
        {
            if (attempt != null)
            {
                attempt.Outcome = "SubmissionUnknown";
                attempt.Evidence = "Подтверждение отправки потеряно из-за сбоя CLI. Не отправлять повторно. " + ex.Message;
                Workflow.Transition(application, ApplicationState.SubmissionUnknown);
                db.Audit.Add(new AuditEvent { EntityId = application.Id, EntityType = nameof(JobApplication), Action = "BrowserSubmission/SubmissionUnknown", Details = attempt.Evidence });
                await db.SaveChangesAsync(CancellationToken.None);
            }
            throw;
        }
        catch (Exception ex)
        {
            if (attempt == null)
            {
                foreach (var entry in db.ChangeTracker.Entries<ApplicationAttempt>().Where(x => x.State == EntityState.Added).ToArray()) entry.State = EntityState.Detached;
                await db.Entry(application).ReloadAsync(CancellationToken.None);
                if (application.State == ApplicationState.ReadyToSubmit) Block(application, ex is OperationCanceledException ? "Browser preparation was cancelled." : ex.Message);
                await db.SaveChangesAsync(CancellationToken.None);
                throw;
            }
            attempt.Outcome = "SubmissionUnknown";
            var evidence = "Submission click may have occurred, but confirmation was lost. Do not retry automatically. " +
                (ex is OperationCanceledException ? "Cancelled or timed out." : ex.Message);
            attempt.Evidence = evidence[..Math.Min(4000, evidence.Length)];
            Workflow.Transition(application, ApplicationState.SubmissionUnknown);
        }
        db.Audit.Add(new AuditEvent { EntityId = application.Id, EntityType = nameof(JobApplication), Action = "BrowserSubmission/" + attempt.Outcome, Details = attempt.Evidence });
        await db.SaveChangesAsync(CancellationToken.None);
        return attempt;
    }

    private void Block(JobApplication application, string reason)
    {
        Workflow.Transition(application, ApplicationState.Blocked);
        db.Audit.Add(new AuditEvent { EntityId = application.Id, EntityType = nameof(JobApplication), Action = "BrowserNeedsAttention", Details = reason[..Math.Min(2000, reason.Length)] });
    }
}
