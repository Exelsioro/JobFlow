using JobFlow.Application;
using JobFlow.Domain;
using Microsoft.EntityFrameworkCore;
namespace JobFlow.Infrastructure;
public class TaskProcessor(JobFlowDb db, IAgentExecutor? agents = null)
{
    public async Task<bool> RunOneAsync(CancellationToken cancellationToken)
    {
        var now=DateTime.UtcNow;
        var candidate=await db.Tasks.AsNoTracking().Where(x=>((x.RunRequested||x.Enabled)&&x.NextRunAt<=now || x.LeaseUntil!=null&&x.LeaseUntil<now)&&
            (x.LeaseUntil==null||x.LeaseUntil<now)).OrderBy(x=>x.NextRunAt).FirstOrDefaultAsync(cancellationToken);
        if(candidate==null)return false;
        var token=Guid.NewGuid().ToString("N");
        var claimed=await db.Tasks.Where(x=>x.Id==candidate.Id&&((x.RunRequested||x.Enabled)&&x.NextRunAt<=now || x.LeaseUntil!=null&&x.LeaseUntil<now)&&
            (x.LeaseUntil==null||x.LeaseUntil<now)).ExecuteUpdateAsync(s=>s.SetProperty(x=>x.LeaseUntil,now.AddMinutes(20)).SetProperty(x=>x.LeaseToken,token)
                .SetProperty(x=>x.RunRequested,false).SetProperty(x=>x.CancelRequested,false),cancellationToken);
        if(claimed==0)return false;
        await db.Runs.Where(x=>x.ScheduledTaskId==candidate.Id&&x.Status=="Running").ExecuteUpdateAsync(s=>s.SetProperty(x=>x.Status,"Interrupted")
            .SetProperty(x=>x.FinishedAt,now).SetProperty(x=>x.Message,"Expired worker lease; previous run interrupted."),cancellationToken);
        var run=new TaskRun{ScheduledTaskId=candidate.Id};db.Add(run);await db.SaveChangesAsync(cancellationToken);
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(candidate.TimeoutSeconds));
        using var monitorStop=new CancellationTokenSource();
        var userCancelled=false;
        var monitor=MonitorAsync();
        try
        {
            run.Message=candidate.Kind switch
            {
                "HealthCheck"=>await db.Database.CanConnectAsync(timeout.Token)?"Database reachable.":throw new InvalidOperationException("Database unavailable."),
                "BrowserDryRun"=>await RunBrowser(timeout.Token),
                "Workflow"=>(await new JobSearchWorkflow(db,agents??new AgentExecutor(db,new CodexCliProvider())).RunAsync(candidate.WorkflowDefinitionId??throw new InvalidOperationException("Workflow missing."),timeout.Token)).Status,
                "Integration"=>await PullIntegration(candidate.IntegrationId??throw new InvalidOperationException("Integration missing."),timeout.Token),
                _=>throw new InvalidOperationException("Task kind unsupported.")
            };
            timeout.Token.ThrowIfCancellationRequested();run.Status="Succeeded";
        }
        catch(Exception ex)
        {
            run.Status=userCancelled?"Cancelled":cancellationToken.IsCancellationRequested?"Interrupted":"Failed";
            run.Message=ex is OperationCanceledException?"Cancelled or timed out.":ex.Message;
        }
        finally { monitorStop.Cancel();await monitor; }
        run.FinishedAt=DateTime.UtcNow;
        var failures=run.Status=="Failed"?candidate.FailureCount+1:0;
        var retry=run.Status=="Failed"&&failures<=candidate.MaxRetries;
        var next=DateTime.UtcNow.AddMinutes(retry?Math.Pow(2,failures-1):candidate.IntervalMinutes);
        await using var tx=await db.Database.BeginTransactionAsync(CancellationToken.None);
        var released=await db.Tasks.Where(x=>x.Id==candidate.Id&&x.LeaseToken==token).ExecuteUpdateAsync(s=>
            s.SetProperty(x=>x.LeaseUntil,(DateTime?)null).SetProperty(x=>x.LeaseToken,(string?)null)
                .SetProperty(x=>x.RunRequested,x=>retry&&!x.CancelRequested).SetProperty(x=>x.NextRunAt,next).SetProperty(x=>x.FailureCount,retry?failures:0),CancellationToken.None);
        if(released!=1)throw new InvalidOperationException("Task lease was lost.");
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
    private async Task<string> PullIntegration(Guid id,CancellationToken ct)
    {
        using var http=new HttpClient(new HttpClientHandler{AllowAutoRedirect=false});
        await new RemoteIntegrationService(db,new SecretStore(),http).PullAsync(id,ct);
        return "Source read completed. Check feedback or pending journal import.";
    }
}

