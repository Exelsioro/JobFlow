using JobFlow.Application;
using JobFlow.Domain;
using JobFlow.Infrastructure;
using Microsoft.EntityFrameworkCore;
namespace JobFlow.IntegrationTests;

public class RecoveryTests
{
    [Fact] public async Task ExpiredManualRunIsRecoveredWithoutEnablingSchedule()
    {
        var file = Path.Combine(Path.GetTempPath(), $"jobflow-recovery-{Guid.NewGuid()}.db");
        var options = new DbContextOptionsBuilder<JobFlowDb>().UseSqlite($"Data Source={file};Pooling=False").Options;
        try
        {
            await using var db = new JobFlowDb(options); await db.Database.MigrateAsync();
            var user = new User(); var profile = new CandidateProfile { UserId = user.Id };
            var task = new ScheduledTask { CandidateProfileId = profile.Id, Enabled = false, NextRunAt = DateTime.UtcNow.AddDays(1),
                LeaseUntil = DateTime.UtcNow.AddMinutes(-1), LeaseToken = "dead-worker" };
            var oldRun = new TaskRun { ScheduledTaskId = task.Id };
            db.AddRange(user, profile, task, oldRun); await db.SaveChangesAsync(); db.ChangeTracker.Clear();
            Assert.True(await new TaskProcessor(db).RunOneAsync(default));
            var persisted = await db.Tasks.AsNoTracking().SingleAsync();
            Assert.False(persisted.Enabled); Assert.False(persisted.RunRequested); Assert.Null(persisted.LeaseUntil);
            var runs = await db.Runs.AsNoTracking().ToListAsync();
            Assert.Contains(runs, x => x.Status == "Interrupted");
            Assert.Contains(runs, x => x.Status == "Succeeded");
            Assert.False(await new TaskProcessor(db).RunOneAsync(default));
        }
        finally { File.Delete(file); }
    }
    [Fact] public async Task FailedProviderIsRecordedAndConfigurationSnapshotIsRetained()
    {
        var file = Path.Combine(Path.GetTempPath(), $"jobflow-agent-{Guid.NewGuid()}.db");
        var options = new DbContextOptionsBuilder<JobFlowDb>().UseSqlite($"Data Source={file};Pooling=False").Options;
        try
        {
            await using var db = new JobFlowDb(options); await db.Database.MigrateAsync();
            var user = new User(); var profile = new CandidateProfile { UserId = user.Id };
            var provider = new ModelProvider { Enabled = true };
            var agent = new AgentProfile { CandidateProfileId = profile.Id, ModelProviderId = provider.Id, Enabled = true, Version = 3 };
            db.AddRange(user, profile, provider, agent); await db.SaveChangesAsync();
            await Assert.ThrowsAsync<InvalidOperationException>(() => new AgentExecutor(db, new FailingProvider()).ExecuteAsync(new(agent.Id, "Synthetic input"), default));
            var execution = await db.Set<AgentExecution>().SingleAsync();
            Assert.Equal("Failed", execution.Status); Assert.Equal(3, execution.ConfigurationVersion);
            Assert.DoesNotContain("Synthetic input", execution.ConfigurationJson); Assert.Empty(execution.Output);
            var completion = await db.Audit.SingleAsync(x => x.Action == "AgentExecutionCompleted");
            Assert.Contains("Failed", completion.Details);
            Assert.DoesNotContain("Synthetic input", completion.Details);
        }
        finally { File.Delete(file); }
    }
    [Fact] public async Task ReadOnlyDatabaseCannotReportTaskSuccess()
    {
        var file = Path.Combine(Path.GetTempPath(), $"jobflow-readonly-{Guid.NewGuid()}.db");
        try
        {
            await using (var db = new JobFlowDb(new DbContextOptionsBuilder<JobFlowDb>().UseSqlite($"Data Source={file};Pooling=False").Options))
            {
                await db.Database.MigrateAsync(); var user = new User(); var profile = new CandidateProfile { UserId = user.Id };
                db.AddRange(user, profile, new ScheduledTask { CandidateProfileId = profile.Id, RunRequested = true });
                await db.SaveChangesAsync();
            }
            await using var readOnly = new JobFlowDb(new DbContextOptionsBuilder<JobFlowDb>().UseSqlite($"Data Source={file};Mode=ReadOnly;Pooling=False").Options);
            await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() => new TaskProcessor(readOnly).RunOneAsync(default));
            Assert.Empty(await readOnly.Runs.ToListAsync());
        }
        finally { File.Delete(file); }
    }
    private class FailingProvider : IModelProvider
    {
        public Task<ModelResponse> GenerateAsync(ModelRequest request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Synthetic provider failure");
    }
}
