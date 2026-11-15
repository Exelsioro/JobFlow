using JobFlow.Application;
using JobFlow.Domain;
using JobFlow.Infrastructure;
using Microsoft.EntityFrameworkCore;
namespace JobFlow.IntegrationTests;
public class SubmissionTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DurableAttemptIsNotRepeatedEvenWithLostConfirmation(bool confirmed)
    {
        var directory=Path.Combine(Path.GetTempPath(),"jobflow-submit-"+Guid.NewGuid());
        var previous=Environment.GetEnvironmentVariable("JOBFLOW_DATA_DIR");
        Environment.SetEnvironmentVariable("JOBFLOW_DATA_DIR",directory);
        Directory.CreateDirectory(Path.Combine(directory,"resumes"));
        try
        {
            await using var db=new JobFlowDb(new DbContextOptionsBuilder<JobFlowDb>().UseSqlite(Storage.ConnectionString+";Pooling=False").Options);
            await db.Database.MigrateAsync();
            var user=new User();var profile=new CandidateProfile{UserId=user.Id};
            var resume=new Resume{CandidateProfileId=profile.Id};
            var version=new ResumeVersion{ResumeId=resume.Id,Version=1,StorageKey="fixture.pdf"};
            await File.WriteAllTextAsync(Path.Combine(directory,"resumes","fixture.pdf"),"%PDF-1.4");
            var platform=new JobPlatform{Adapter="test-form"};
            var vacancy=new Vacancy{JobPlatformId=platform.Id,ExternalId="fixture"};
            var app=new JobApplication{CandidateProfileId=profile.Id,VacancyId=vacancy.Id,ResumeVersionId=version.Id,State=ApplicationState.ReadyToSubmit,CoverLetter="Verified"};
            db.AddRange(user,profile,resume,version,platform,vacancy,app);await db.SaveChangesAsync();
            var provider=new Adapter(confirmed);
            var service=new TestSubmissionService(db,provider);
            var first=await service.SubmitAsync(app.Id,"stable-key",0,default);
            Assert.Equal(confirmed?"TestConfirmed":"SubmissionUnknown",first.Outcome);
            var replay=await service.SubmitAsync(app.Id,"stable-key",0,default);
            Assert.Equal(first.Id,replay.Id);Assert.Equal(1,provider.Calls);
            await Assert.ThrowsAsync<InvalidOperationException>(()=>service.SubmitAsync(app.Id,"another-key",app.Revision,default));
            db.ChangeTracker.Clear();
            Assert.Single(await db.Set<ApplicationAttempt>().ToListAsync());
            Assert.Equal(confirmed?ApplicationState.Submitted:ApplicationState.SubmissionUnknown,(await db.Applications.SingleAsync()).State);
        }
        finally {Environment.SetEnvironmentVariable("JOBFLOW_DATA_DIR",previous);Directory.Delete(directory,true);}
    }
    [Fact]
    public async Task FailedTaskBackoffPreventsImmediateRetry()
    {
        var file=Path.Combine(Path.GetTempPath(),"jobflow-backoff-"+Guid.NewGuid()+".db");
        try
        {
            await using var db=new JobFlowDb(new DbContextOptionsBuilder<JobFlowDb>().UseSqlite($"Data Source={file};Pooling=False").Options);
            await db.Database.MigrateAsync();
            var user=new User();var profile=new CandidateProfile{UserId=user.Id};
            var task=new ScheduledTask{CandidateProfileId=profile.Id,Kind="Invalid",RunRequested=true,MaxRetries=1};
            db.AddRange(user,profile,task);await db.SaveChangesAsync();db.ChangeTracker.Clear();
            Assert.True(await new TaskProcessor(db).RunOneAsync(default));
            var persisted=await db.Tasks.AsNoTracking().SingleAsync();
            Assert.True(persisted.RunRequested);Assert.Equal(1,persisted.FailureCount);
            Assert.True(persisted.NextRunAt>DateTime.UtcNow);
            Assert.False(await new TaskProcessor(db).RunOneAsync(default));
            Assert.Equal("Failed",(await db.Runs.SingleAsync()).Status);
        }
        finally {File.Delete(file);}
    }
    private class Adapter(bool confirmed):IJobApplicationProvider
    {
        public int Calls;
        public Task<BrowserResult> SubmitAsync(JobApplication app,CancellationToken ct)
        {
            Calls++;
            if(!confirmed)throw new IOException("Lost confirmation");
            return Task.FromResult(new BrowserResult(true,"Synthetic confirmation"));
        }
    }
}
