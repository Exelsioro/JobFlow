using JobFlow.Application;
using JobFlow.Domain;
using Microsoft.EntityFrameworkCore;
namespace JobFlow.Infrastructure;

// Fixed UTC intervals avoid DST ambiguity; timezone is retained for display.
public class TaskProcessor(JobFlowDb db, IAgentExecutor? agents = null)
{
    public async Task<bool> RunOneAsync(CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var candidate = await db.Tasks.AsNoTracking().Where(x => (x.RunRequested || (x.Enabled && x.NextRunAt <= now) || (x.LeaseUntil != null && x.LeaseUntil < now)) &&
            (x.LeaseUntil == null || x.LeaseUntil < now)).OrderBy(x => x.NextRunAt).FirstOrDefaultAsync(cancellationToken);
        if (candidate == null) return false;
        var token = Guid.NewGuid().ToString("N");
        // Atomic compare-and-set works across API/worker processes.
        var claimed = await db.Tasks.Where(x => x.Id == candidate.Id && (x.RunRequested || (x.Enabled && x.NextRunAt <= now) || (x.LeaseUntil != null && x.LeaseUntil < now)) &&
            (x.LeaseUntil == null || x.LeaseUntil < now))
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.LeaseUntil, now.AddMinutes(20)).SetProperty(x => x.LeaseToken, token).SetProperty(x => x.RunRequested, false), cancellationToken);
        if (claimed == 0) return false;
        await db.Runs.Where(x => x.ScheduledTaskId == candidate.Id && x.Status == "Running")
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, "Interrupted").SetProperty(x => x.FinishedAt, now)
                .SetProperty(x => x.Message, "Worker lease expired; previous run was interrupted."), cancellationToken);
        var run = new TaskRun { ScheduledTaskId = candidate.Id };
        db.Add(run); await db.SaveChangesAsync(cancellationToken);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(candidate.Kind == "Workflow" ? 900 : 45));
            run.Message = candidate.Kind switch
            {
                "HealthCheck" => await db.Database.CanConnectAsync(timeout.Token) ? "Database reachable." : throw new InvalidOperationException("Database unreachable."),
                "BrowserDryRun" => await RunBrowser(timeout.Token),
                "Workflow" => (await new JobSearchWorkflow(db, agents ?? new AgentExecutor(db, new CodexCliProvider())).RunAsync(candidate.WorkflowDefinitionId ?? throw new InvalidOperationException("Workflow configuration missing."), timeout.Token)).Status,
                _ => throw new InvalidOperationException("Task kind is not supported.")
            };
            run.Status = "Succeeded";
        }
        catch (Exception ex)
        {
            run.Status = cancellationToken.IsCancellationRequested ? "Interrupted" : "Failed";
            run.Message = ex is OperationCanceledException ? "Cancelled or timed out." : ex.Message;
        }
        run.FinishedAt = DateTime.UtcNow;
        await using var transaction = await db.Database.BeginTransactionAsync(CancellationToken.None);
        var released = await db.Tasks.Where(x => x.Id == candidate.Id && x.LeaseToken == token)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.LeaseUntil, (DateTime?)null).SetProperty(x => x.LeaseToken, (string?)null)
                .SetProperty(x => x.NextRunAt, DateTime.UtcNow.AddMinutes(candidate.IntervalMinutes)), CancellationToken.None);
        if (released != 1) throw new InvalidOperationException("Task lease was lost.");
        await db.SaveChangesAsync(CancellationToken.None);
        await transaction.CommitAsync(CancellationToken.None);
        return true;
    }
    private static async Task<string> RunBrowser(CancellationToken cancellationToken)
    {
        var result = await new PlaywrightBrowser().DryRunAsync(cancellationToken);
        if (!result.Confirmed) throw new InvalidOperationException("Test submission confirmation missing.");
        return result.Evidence;
    }
}
