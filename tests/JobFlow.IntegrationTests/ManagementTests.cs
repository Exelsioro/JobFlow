using System.Net;
using System.Net.Http.Json;
using JobFlow.Domain;
using JobFlow.Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
namespace JobFlow.IntegrationTests;
public class ManagementTests
{
    [Fact] public async Task PrimaryResumeAndFeedbackBindingsRespectOwnershipAndAreIdempotent()
    {
        var directory=Path.Combine(Path.GetTempPath(),"jobflow-management-"+Guid.NewGuid());
        var previous=Environment.GetEnvironmentVariable("JOBFLOW_DATA_DIR");
        Environment.SetEnvironmentVariable("JOBFLOW_DATA_DIR",directory);
        try
        {
            await using var factory=new WebApplicationFactory<Program>();
            using var client=factory.CreateClient();client.DefaultRequestHeaders.Add("X-JobFlow-Client","local-ui");
            using var scope=factory.Services.CreateScope();var db=scope.ServiceProvider.GetRequiredService<JobFlowDb>();
            var user=new User();var profile=new CandidateProfile{UserId=user.Id};var other=new CandidateProfile{UserId=user.Id};
            var first=new Resume{CandidateProfileId=profile.Id,IsPrimary=true};var second=new Resume{CandidateProfileId=profile.Id};
            var version=new ResumeVersion{ResumeId=first.Id,Version=1};var otherResume=new Resume{CandidateProfileId=other.Id};
            var otherVersion=new ResumeVersion{ResumeId=otherResume.Id,Version=1};
            var platform=new JobPlatform();var vacancy=new Vacancy{JobPlatformId=platform.Id,ExternalId="one"};
            var app=new JobApplication{CandidateProfileId=profile.Id,VacancyId=vacancy.Id,ResumeVersionId=version.Id,State=ApplicationState.Submitted};
            var otherApp=new JobApplication{CandidateProfileId=other.Id,VacancyId=vacancy.Id,ResumeVersionId=otherVersion.Id,State=ApplicationState.Submitted};
            var integration=new Integration{CandidateProfileId=profile.Id,Kind="gmail"};
            var feedback=new FeedbackEvent{CandidateProfileId=profile.Id,IntegrationId=integration.Id,SourceEventId="message-one",Subject="Synthetic response"};
            db.AddRange(user,profile,other,first,second,version,otherResume,otherVersion,platform,vacancy,app,otherApp,integration,feedback);await db.SaveChangesAsync();
            (await client.PostAsJsonAsync($"/api/resumes/{second.Id}/primary",new{})).EnsureSuccessStatusCode();
            db.ChangeTracker.Clear();
            Assert.Equal(second.Id,(await db.Resumes.SingleAsync(x=>x.IsPrimary)).Id);
            var invalid=await client.PostAsJsonAsync($"/api/feedback/{feedback.Id}/link",new{applicationId=otherApp.Id});
            Assert.Equal(HttpStatusCode.BadRequest,invalid.StatusCode);
            for(var i=0;i<2;i++)(await client.PostAsJsonAsync($"/api/feedback/{feedback.Id}/link",new{applicationId=app.Id})).EnsureSuccessStatusCode();
            db.ChangeTracker.Clear();
            Assert.Single(await db.Set<EmployerInteraction>().ToListAsync());
            Assert.Equal(ApplicationState.EmployerResponded,(await db.Applications.SingleAsync(x=>x.Id==app.Id)).State);
            Assert.False((await db.Set<FeedbackEvent>().SingleAsync()).NeedsAction);
            (await client.PostAsJsonAsync("/api/accounts",new{candidateProfileId=profile.Id,jobPlatformId=platform.Id,externalResumeId="manual-id"})).EnsureSuccessStatusCode();
            var invalidAccount=await client.PostAsJsonAsync("/api/accounts",new{candidateProfileId=profile.Id,jobPlatformId=platform.Id,browserProfileId=Guid.NewGuid()});
            Assert.Equal(HttpStatusCode.BadRequest,invalidAccount.StatusCode);
        }
        finally {Environment.SetEnvironmentVariable("JOBFLOW_DATA_DIR",previous);Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();Directory.Delete(directory,true);}
    }
}
