using JobFlow.Infrastructure;
using Microsoft.EntityFrameworkCore;
namespace JobFlow.Worker;
public class Worker(IServiceScopeFactory scopes, ILogger<Worker> logger, ServiceRuntime runtime) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                if (runtime.Role != "all" && !await scope.ServiceProvider.GetRequiredService<JobFlowDb>().Set<JobFlow.Domain.ServiceState>().AnyAsync(x => x.Role == runtime.Role && x.Enabled, stoppingToken))
                { await Task.Delay(1000, stoppingToken); continue; }
                runtime.Status = "Running";
                if (await scope.ServiceProvider.GetRequiredService<TaskProcessor>().RunOneAsync(stoppingToken)) continue;
                runtime.Status = "Idle";
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogError(ex, "Job processing failed; retrying poll"); }
            try { await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }
}
