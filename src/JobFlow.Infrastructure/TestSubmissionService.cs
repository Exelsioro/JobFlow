using JobFlow.Application;
using JobFlow.Domain;
using Microsoft.EntityFrameworkCore;
namespace JobFlow.Infrastructure;
public sealed class TestApplicationProvider(IBrowserAutomationProvider browser) : IJobApplicationProvider
{
    public Task<BrowserResult> SubmitAsync(JobApplication application,CancellationToken ct) => browser.DryRunAsync(ct);
}
public sealed class TestSubmissionService(JobFlowDb db,IJobApplicationProvider provider)
{
    public async Task<ApplicationAttempt> SubmitAsync(Guid id,string key,int revision,CancellationToken ct)
    {
        if(string.IsNullOrWhiteSpace(key)||key.Length>100)throw new ArgumentException("Idempotency key is required (max 100 characters).");
        JobApplication application;ApplicationAttempt attempt;
        await using(var tx=await db.Database.BeginTransactionAsync(ct))
        {
            var existing=await db.Set<ApplicationAttempt>().SingleOrDefaultAsync(x=>x.IdempotencyKey==key,ct);
            if(existing!=null)
            {
                if(existing.JobApplicationId!=id)throw new InvalidOperationException("Idempotency key belongs to another application.");
                return existing;
            }
            application=await db.Applications.SingleAsync(x=>x.Id==id,ct);
            if(application.Revision!=revision)throw new DbUpdateConcurrencyException("Application changed; refresh before submission.");
            if(application.State!=ApplicationState.ReadyToSubmit)throw new InvalidOperationException("Only reviewed ReadyToSubmit applications can run the test adapter.");
            var vacancy=await db.Vacancies.SingleAsync(x=>x.Id==application.VacancyId,ct);
            var platform=await db.Platforms.SingleAsync(x=>x.Id==vacancy.JobPlatformId,ct);
            if(platform.Adapter!="test-form")throw new InvalidOperationException("Real platform submission is disabled.");
            if(await db.Set<Integration>().AnyAsync(x=>x.CandidateProfileId==application.CandidateProfileId&&(x.Kind=="csv"||x.Kind=="google-sheets")&&!x.ImportConfirmed,ct)||
               await db.Set<HistoricalApplication>().AnyAsync(x=>x.CandidateProfileId==application.CandidateProfileId&&x.VacancyId==application.VacancyId,ct))
                throw new InvalidOperationException("Journal import or historical application blocks submission.");
            var resume=await (from v in db.ResumeVersions join r in db.Resumes on v.ResumeId equals r.Id
                where v.Id==application.ResumeVersionId&&r.CandidateProfileId==application.CandidateProfileId select v).SingleAsync(ct);
            if(!File.Exists(Path.Combine(Storage.Root,"resumes",resume.StorageKey)))throw new InvalidOperationException("Selected resume file is missing.");
            if(string.IsNullOrWhiteSpace(application.CoverLetter))throw new InvalidOperationException("Cover letter is required.");
            application.IsTest=true;
            Workflow.Transition(application,ApplicationState.Submitting);
            attempt=new ApplicationAttempt{JobApplicationId=id,IdempotencyKey=key,Outcome="Pending"};
            db.Add(attempt);Audit(application,"TestSubmissionStarted");await db.SaveChangesAsync(ct);await tx.CommitAsync(ct);
        }
        try
        {
            var result=await provider.SubmitAsync(application,ct);
            attempt.Evidence=result.Evidence;
            attempt.Outcome=result.Confirmed?"TestConfirmed":"SubmissionUnknown";
            Workflow.Transition(application,result.Confirmed?ApplicationState.Submitted:ApplicationState.SubmissionUnknown);
        }
        catch
        {
            attempt.Outcome="SubmissionUnknown";attempt.Evidence="Adapter failed or confirmation was lost. Do not retry.";
            Workflow.Transition(application,ApplicationState.SubmissionUnknown);
        }
        Audit(application,attempt.Outcome);await db.SaveChangesAsync(CancellationToken.None);return attempt;
    }
    public async Task MarkUnknownAsync(Guid id,CancellationToken ct)
    {
        var application=await db.Applications.SingleAsync(x=>x.Id==id,ct);
        if(application.State!=ApplicationState.Submitting)throw new InvalidOperationException("Only an interrupted Submitting operation can be reconciled as unknown.");
        Workflow.Transition(application,ApplicationState.SubmissionUnknown);
        foreach(var attempt in await db.Set<ApplicationAttempt>().Where(x=>x.JobApplicationId==id&&x.Outcome=="Pending").ToListAsync(ct))
        {attempt.Outcome="SubmissionUnknown";attempt.Evidence="User marked the interrupted operation as unknown.";}
        Audit(application,"SubmissionUnknown");await db.SaveChangesAsync(ct);
    }
    private void Audit(JobApplication application,string action)=>db.Audit.Add(new AuditEvent{EntityId=application.Id,EntityType=nameof(JobApplication),Action=action,Details="Isolated test adapter; no real application sent."});
}

