using JobFlow.Domain;
using JobFlow.Infrastructure;
using Microsoft.EntityFrameworkCore;
namespace JobFlow.Worker;
public sealed class ServiceRuntime(string role)
{
    public string Role { get; } = role;
    public string Status { get; set; } = "Starting";
}
public sealed class ServiceHeartbeat(IServiceScopeFactory scopes, ServiceRuntime runtime, ILogger<ServiceHeartbeat> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    using var scope = scopes.CreateScope();
                    var db = scope.ServiceProvider.GetRequiredService<JobFlowDb>();
                    var state = await db.Set<ServiceState>().SingleOrDefaultAsync(x => x.Role == runtime.Role, stoppingToken);
                    if (state == null) { state = new() { Role = runtime.Role }; db.Add(state); }
                    state.HeartbeatAt = DateTime.UtcNow; state.ProcessId = Environment.ProcessId;
                    state.Status = state.Enabled ? runtime.Status : "Paused";
                    await db.SaveChangesAsync(stoppingToken);
                }
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested) { logger.LogError(ex, "Service heartbeat failed"); }
                await Task.Delay(3000, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally
        {
            using var scope = scopes.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<JobFlowDb>();
            await db.Set<ServiceState>().Where(x => x.Role == runtime.Role).ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, "Stopped"));
        }
    }
}
