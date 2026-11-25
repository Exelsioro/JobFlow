using JobFlow.Application;
using JobFlow.Domain;
using Microsoft.EntityFrameworkCore;
namespace JobFlow.Infrastructure;
public class TaskProcessor(JobFlowDb db, IAgentExecutor? agents = null, string role = "all", IBrowserWorkflowDriver? browser = null)
{
    public async Task<bool> RunOneAsync(CancellationToken cancellationToken)
    {
        var now=DateTime.UtcNow;
        var ready = db.Tasks.Where(x =>
            ((x.RunRequested || x.Enabled) && x.NextRunAt <= now || x.LeaseUntil < now) &&
            (x.LeaseUntil == null || x.LeaseUntil < now) &&
            (x.WorkflowDefinitionId == null ||
                (!db.Tasks.Any(other => other.WorkflowDefinitionId == x.WorkflowDefinitionId && other.Id != x.Id && other.LeaseUntil >= now) &&
                 !db.Set<WorkflowDefinition>().Any(workflow => workflow.Id == x.WorkflowDefinitionId && workflow.LeaseUntil >= now))));
        var candidate=await ready.AsNoTracking()
            .Where(x=>role == "all" || role == "monitor" && x.Kind == "Integration" || role == "search" && x.Kind != "Integration")
            .OrderBy(x=>x.NextRunAt).ThenBy(x=>x.Id).FirstOrDefaultAsync(cancellationToken);
        if(candidate==null)return false;
        var token=Guid.NewGuid().ToString("N");
        var leaseUntil = now + TaskExecutionLimits.LeaseDuration(candidate.TimeoutSeconds);
        var claimed=await ready.Where(x=>x.Id==candidate.Id).ExecuteUpdateAsync(s=>s.SetProperty(x=>x.LeaseUntil,leaseUntil).SetProperty(x=>x.LeaseToken,token)
                .SetProperty(x=>x.RunRequested,false).SetProperty(x=>x.CancelRequested,false),cancellationToken);
        if(claimed==0)return false;
        await db.Runs.Where(x=>x.ScheduledTaskId==candidate.Id&&x.Status=="Running").ExecuteUpdateAsync(s=>s.SetProperty(x=>x.Status,"Interrupted")
            .SetProperty(x=>x.FinishedAt,now).SetProperty(x=>x.Message,"Expired worker lease; previous run interrupted."),cancellationToken);
        var run=new TaskRun{ScheduledTaskId=candidate.Id};db.Add(run);await db.SaveChangesAsync(cancellationToken);
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(candidate.TimeoutSeconds));
        using var monitorStop=new CancellationTokenSource();
        var userCancelled=false;
        var workflowTechnicalErrors=0;
        var providerRetryAllowed=true;
        var monitor=MonitorAsync();
        try
        {
            run.Message=candidate.Kind switch
            {
                "HealthCheck"=>await db.Database.CanConnectAsync(timeout.Token)?"Database reachable.":throw new InvalidOperationException("Database unavailable."),
                "BrowserDryRun"=>await RunBrowser(timeout.Token),
                "Workflow"=>await RunWorkflow(candidate.WorkflowDefinitionId??throw new InvalidOperationException("Workflow missing."),timeout.Token),
                "PrepareApplication"=>await RunWorkflow(candidate.WorkflowDefinitionId??throw new InvalidOperationException("Workflow missing."),timeout.Token,candidate.VacancyId,true,candidate.PrepareOutsideAgeLimit),
                "Integration"=>await PullIntegration(candidate.IntegrationId??throw new InvalidOperationException("Integration missing."),timeout.Token),
                _=>throw new InvalidOperationException("Task kind unsupported.")
            };
            timeout.Token.ThrowIfCancellationRequested();run.Status="Succeeded";
            if (candidate.Kind is "Workflow" or "PrepareApplication")
            {
                var workflowResult = await db.Set<JobFlow.Domain.WorkflowRun>().AsNoTracking()
                    .Where(x => x.WorkflowDefinitionId == candidate.WorkflowDefinitionId && x.CreatedAt >= run.CreatedAt)
                    .OrderByDescending(x => x.CreatedAt).FirstOrDefaultAsync(timeout.Token);
                if (workflowResult?.Status == "NeedsAttention") run.Status = "NeedsAttention";
                workflowTechnicalErrors=workflowResult?.Errors??0;
            }
        }
        catch(Exception ex)
        {
            if (ModelProviderException.Find(ex) is { } providerError) providerRetryAllowed=providerError.Retryable;
            run.Status=userCancelled?"Cancelled":cancellationToken.IsCancellationRequested?"Interrupted":"Failed";
            run.Message=ex is OperationCanceledException?"Cancelled or timed out.":ex.Message;
        }
        finally { monitorStop.Cancel();await monitor; }
        run.FinishedAt=DateTime.UtcNow;
        var failures=run.Status=="Failed"?candidate.FailureCount+1:0;
        var retry=run.Status=="Failed"&&providerRetryAllowed&&failures<=candidate.MaxRetries;
        var next=retry ? DateTime.UtcNow.AddMinutes(Math.Pow(2,failures-1)) : TaskSchedule.Next(candidate, DateTime.UtcNow);
        await using var tx=await db.Database.BeginTransactionAsync(CancellationToken.None);
        var released=await db.Tasks.Where(x=>x.Id==candidate.Id&&x.LeaseToken==token).ExecuteUpdateAsync(s=>
            s.SetProperty(x=>x.LeaseUntil,(DateTime?)null).SetProperty(x=>x.LeaseToken,(string?)null)
                .SetProperty(x=>x.RunRequested,x=>(x.RunRequested||retry)&&!x.CancelRequested)
                .SetProperty(x=>x.NextRunAt,x=>x.RunRequested?x.NextRunAt:next)
                .SetProperty(x=>x.FailureCount,x=>x.RunRequested?0:retry?failures:0)
                .SetProperty(x=>x.PrepareOutsideAgeLimit,x=>retry&&x.PrepareOutsideAgeLimit),CancellationToken.None);
        if(released!=1)throw new InvalidOperationException("Task lease was lost.");
        db.Add(new Notification { CandidateProfileId = candidate.CandidateProfileId, Kind = run.Status == "Succeeded" ? "Information" : run.Status == "NeedsAttention" && workflowTechnicalErrors==0 ? "Review" : "Error",
            Title = (candidate.Kind == "Integration" ? "Мониторинг: " : candidate.Kind == "PrepareApplication" ? "Подготовка: " : "Поиск: ") + candidate.Name, Message = run.Status + ": " + run.Message, RelatedId = run.Id });
        await db.SaveChangesAsync(CancellationToken.None);await tx.CommitAsync(CancellationToken.None);return true;
        async Task MonitorAsync()
        {
            try
            {
                while(!monitorStop.IsCancellationRequested)
                {
                    await Task.Delay(300,monitorStop.Token);
                    await using var monitorDb=new JobFlowDb(new DbContextOptionsBuilder<JobFlowDb>().UseSqlite(db.Database.GetConnectionString()).Options);
                    var current=await monitorDb.Tasks.AsNoTracking().SingleAsync(x=>x.Id==candidate.Id,monitorStop.Token);
                    if(current.CancelRequested||current.LeaseToken!=token){userCancelled=true;timeout.Cancel();return;}
                }
            }
            catch(OperationCanceledException) when(monitorStop.IsCancellationRequested) { }
            catch { timeout.Cancel(); }
        }
    }
    private static async Task<string> RunBrowser(CancellationToken ct)
    {
        var result=await new PlaywrightBrowser().DryRunAsync(ct);
        if(!result.Confirmed)throw new InvalidOperationException("Browser confirmation missing.");
        return result.Evidence;
    }
    private async Task<string> RunWorkflow(Guid id, CancellationToken ct, Guid? vacancyId = null, bool preparationOnly = false, bool outsideAgeLimit = false)
    {
        var mode = await db.Set<JobFlow.Domain.WorkflowDefinition>().Where(x => x.Id == id).Select(x => x.ExecutionMode).SingleAsync(ct);
        var executor = agents ?? new AgentExecutor(db, new CodexCliProvider());
        var run = mode == "browser" && role == "all" ? await BrowserWorkflowClient.RunAsync(id, ct, vacancyId, preparationOnly, outsideAgeLimit) :
            await new JobSearchWorkflow(db, executor, browser, monitorWithinSearch: role == "all").RunAsync(id, ct, vacancyId, preparationOnly, outsideAgeLimit);
        return $"Найдено {run.Discovered}, проверено {run.Checked}, подготовлено {run.Prepared}, отправлено {run.Submitted}, пропущено {run.Skipped}, требуют уточнения {run.NeedsAttention}, технических ошибок {run.Errors}.";
    }
    private async Task<string> PullIntegration(Guid id,CancellationToken ct)
    {
        using var http=new HttpClient(new HttpClientHandler{AllowAutoRedirect=false});
        var result = await new RemoteIntegrationService(db,new SecretStore(),http,agents).PullAsync(id,ct);
        return "Source read completed: " + System.Text.Json.JsonSerializer.Serialize(result);
    }
}

