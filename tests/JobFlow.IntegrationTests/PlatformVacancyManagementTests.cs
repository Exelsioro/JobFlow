using System.Net;
using System.Net.Http.Json;
using JobFlow.Domain;
using JobFlow.Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace JobFlow.IntegrationTests;

public class PlatformVacancyManagementTests
{
    [Theory]
    [InlineData("Workflow", 7200, true)]
    [InlineData("Workflow", 21600, true)]
    [InlineData("Workflow", 21601, false)]
    [InlineData("Workflow", 4, false)]
    [InlineData("Integration", 900, true)]
    [InlineData("Integration", 901, false)]
    [InlineData("Integration", 7200, false)]
    [InlineData("BrowserDryRun", 900, true)]
    public async Task SchedulerAcceptsLongWorkflowsWithoutExtendingOtherTasks(string kind, int seconds, bool accepted)
    {
        await using var f = await Fixture.CreateAsync();
        var task = new ScheduledTask { CandidateProfileId = f.Profile.Id, Kind = kind, Name = "Bounded task", TimeoutSeconds = 300 };
        f.Db.Add(task); await f.Db.SaveChangesAsync();
        task.TimeoutSeconds = seconds;
        var response = await f.Client.PutAsJsonAsync($"/api/tasks/{task.Id}", task);
        Assert.Equal(accepted ? HttpStatusCode.OK : HttpStatusCode.BadRequest, response.StatusCode);
        f.Db.ChangeTracker.Clear();
        var stored = await f.Db.Tasks.SingleAsync();
        Assert.Equal(accepted ? seconds : 300, stored.TimeoutSeconds);
        Assert.False(stored.Enabled);
        Assert.False(stored.RunRequested);
    }

    [Fact]
    public async Task PlatformCanBeEditedWithoutBreakingBindingsAndRejectsInvalidChanges()
    {
        await using var f = await Fixture.CreateAsync();
        var account = new PlatformAccount { CandidateProfileId = f.Profile.Id, JobPlatformId = f.Platform.Id };
        f.Db.Add(account); await f.Db.SaveChangesAsync();
        var created = f.Platform.CreatedAt;
        var response = await f.Client.PutAsJsonAsync($"/api/platforms/{f.Platform.Id}",
            new { name = "Updated platform", url = "https://example.invalid/search", adapter = "browser", browserMode = "new-headless", searchFilters = "C# jobs", createdAt = DateTime.UtcNow.AddYears(1) });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        var edited = (await response.Content.ReadFromJsonAsync<JobPlatform>())!;
        Assert.Equal(f.Platform.Id, edited.Id);
        Assert.Equal(created, edited.CreatedAt);
        Assert.Equal("C# jobs", edited.SearchFilters);
        Assert.Equal("new-headless", edited.BrowserMode);
        Assert.Equal(HttpStatusCode.BadRequest, (await f.Client.PutAsJsonAsync($"/api/platforms/{f.Platform.Id}",
            new { name = "Bad browser mode", url = "https://example.invalid", adapter = "browser", browserMode = "unsupported" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await f.Client.PutAsJsonAsync($"/api/platforms/{f.Platform.Id}",
            new { name = "Bad URL", url = "javascript:alert(1)", adapter = "browser" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await f.Client.PutAsJsonAsync($"/api/platforms/{f.Platform.Id}",
            new { name = "Bad adapter", url = "https://example.invalid", adapter = "unknown" })).StatusCode);
        f.Db.ChangeTracker.Clear();
        Assert.Equal("Updated platform", (await f.Db.Platforms.SingleAsync()).Name);
        Assert.Equal(f.Platform.Id, (await f.Db.Set<PlatformAccount>().SingleAsync()).JobPlatformId);
        Assert.Equal(f.Platform.Id, (await f.Db.Vacancies.SingleAsync()).JobPlatformId);
    }

    [Fact]
    public async Task EvaluationIsIdempotentPerProfileAndReevaluationIsExplicit()
    {
        await using var f = await Fixture.CreateAsync();
        var path = $"/api/vacancies/{f.Vacancy.Id}/evaluate";
        (await f.Client.PostAsJsonAsync(path, new { profileId = f.Profile.Id })).EnsureSuccessStatusCode();
        var first = await f.Db.Set<VacancyEvaluation>().SingleAsync();
        (await f.Client.PostAsJsonAsync(path, new { profileId = f.Profile.Id })).EnsureSuccessStatusCode();
        Assert.Equal(first.Id, (await f.Db.Set<VacancyEvaluation>().SingleAsync()).Id);
        (await f.Client.PostAsJsonAsync(path, new { profileId = f.Profile.Id, reevaluate = true })).EnsureSuccessStatusCode();
        Assert.Equal(2, await f.Db.Set<VacancyEvaluation>().CountAsync());
        Assert.Equal(first.ResultJson, (await f.Db.Set<VacancyEvaluation>().SingleAsync(x => x.Id == first.Id)).ResultJson);
        var other = new CandidateProfile { UserId = f.Profile.UserId, Name = "Another candidate" };
        f.Db.Add(other); await f.Db.SaveChangesAsync();
        (await f.Client.PostAsJsonAsync(path, new { profileId = other.Id })).EnsureSuccessStatusCode();
        Assert.Equal(1, await f.Db.Set<VacancyEvaluation>().CountAsync(x => x.CandidateProfileId == other.Id));
        Assert.Equal(2, await f.Db.Set<VacancyEvaluation>().CountAsync(x => x.CandidateProfileId == f.Profile.Id));
    }

    [Fact]
    public async Task DeletingVacancyArchivesItAndPreservesEvaluationsApplicationsAndPdf()
    {
        await using var f = await Fixture.CreateAsync();
        (await f.Client.PostAsJsonAsync($"/api/vacancies/{f.Vacancy.Id}/evaluate", new { profileId = f.Profile.Id })).EnsureSuccessStatusCode();
        var application = new JobApplication { CandidateProfileId = f.Profile.Id, VacancyId = f.Vacancy.Id, ResumeVersionId = f.Version.Id, State = ApplicationState.ReadyForReview };
        var generated = new ResumeVersion { ResumeId = f.Version.ResumeId, Version = 2, VacancyId = f.Vacancy.Id,
            SourceResumeVersionId = f.Version.Id, Origin = "AgentGenerated", StorageKey = "generated.pdf" };
        f.Db.AddRange(application, generated); await f.Db.SaveChangesAsync();
        var pdfDirectory = Path.Combine(f.Directory, "resumes"); System.IO.Directory.CreateDirectory(pdfDirectory);
        var pdfPath = Path.Combine(pdfDirectory, generated.StorageKey);
        await File.WriteAllTextAsync(pdfPath, "%PDF-1.4\n% synthetic fixture\n%%EOF");
        (await f.Client.DeleteAsync($"/api/vacancies/{f.Vacancy.Id}")).EnsureSuccessStatusCode();
        (await f.Client.DeleteAsync($"/api/vacancies/{f.Vacancy.Id}")).EnsureSuccessStatusCode();
        Assert.Empty((await f.Client.GetFromJsonAsync<Vacancy[]>("/api/vacancies"))!);
        Assert.True(Assert.Single((await f.Client.GetFromJsonAsync<Vacancy[]>("/api/vacancies?includeArchived=true"))!).IsArchived);
        Assert.DoesNotContain(f.Vacancy.Title, await f.Client.GetStringAsync("/api/export/vacancies.csv"));
        Assert.Equal(HttpStatusCode.Conflict, (await f.Client.PostAsJsonAsync($"/api/vacancies/{f.Vacancy.Id}/evaluate", new { profileId = f.Profile.Id, reevaluate = true })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await f.Client.PostAsJsonAsync("/api/applications", new { candidateProfileId = f.Profile.Id, vacancyId = f.Vacancy.Id, resumeVersionId = f.Version.Id })).StatusCode);
        f.Db.ChangeTracker.Clear();
        Assert.Equal(application.Id, (await f.Db.Applications.SingleAsync()).Id);
        Assert.Single(await f.Db.Set<VacancyEvaluation>().ToListAsync());
        Assert.Equal(2, await f.Db.ResumeVersions.CountAsync());
        Assert.True(File.Exists(pdfPath));
        Assert.Single(await f.Db.Audit.Where(x => x.EntityId == f.Vacancy.Id && x.Action == "Archived").ToListAsync());
        (await f.Client.PostAsJsonAsync($"/api/vacancies/{f.Vacancy.Id}/restore", new { })).EnsureSuccessStatusCode();
        Assert.False(Assert.Single((await f.Client.GetFromJsonAsync<Vacancy[]>("/api/vacancies"))!).IsArchived);
    }

    [Fact]
    public async Task RunningWorkflowProtectsItsPlatformAndVacanciesFromChanges()
    {
        await using var f = await Fixture.CreateAsync();
        f.Db.Add(new WorkflowDefinition { CandidateProfileId = f.Profile.Id, JobPlatformId = f.Platform.Id,
            ResumeVersionId = f.Version.Id, LeaseUntil = DateTime.UtcNow.AddMinutes(10) });
        await f.Db.SaveChangesAsync();
        Assert.Equal(HttpStatusCode.Conflict, (await f.Client.DeleteAsync($"/api/vacancies/{f.Vacancy.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await f.Client.PutAsJsonAsync($"/api/platforms/{f.Platform.Id}",
            new { name = "Changed while running", url = "https://example.invalid", adapter = "browser" })).StatusCode);
        f.Db.ChangeTracker.Clear();
        Assert.False((await f.Db.Vacancies.SingleAsync()).IsArchived);
        Assert.Equal("Synthetic platform", (await f.Db.Platforms.SingleAsync()).Name);
    }

    [Fact]
    public async Task PrepareEndpointQueuesOneVacancyWithoutChangingAutomaticSubmissionSettings()
    {
        await using var f=await Fixture.CreateAsync();
        var provider=new ModelProvider{Enabled=true};var browser=new BrowserProfile();
        var agent=new AgentProfile{CandidateProfileId=f.Profile.Id,ModelProviderId=provider.Id,Enabled=true,TaskType="BrowserAutomation"};
        var evaluator=new AgentProfile{CandidateProfileId=f.Profile.Id,ModelProviderId=provider.Id,Enabled=true,TaskType="Evaluation"};
        var workflow=new WorkflowDefinition{Name="Single vacancy workflow",CandidateProfileId=f.Profile.Id,JobPlatformId=f.Platform.Id,ResumeVersionId=f.Version.Id,Enabled=true,
            ExecutionMode="browser",BrowserProfileId=browser.Id,BrowserAgentId=agent.Id,EvaluationAgentId=evaluator.Id,AutoSubmit=true,CheckMail=false};
        var ordinaryTask=new ScheduledTask{CandidateProfileId=f.Profile.Id,WorkflowDefinitionId=workflow.Id,Kind="Workflow"};
        f.Db.AddRange(provider,browser,agent,evaluator,workflow,ordinaryTask);await f.Db.SaveChangesAsync();
        var response=await f.Client.PostAsJsonAsync($"/api/vacancies/{f.Vacancy.Id}/prepare",new{workflowDefinitionId=workflow.Id});
        Assert.True(response.IsSuccessStatusCode,await response.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.BadRequest,(await f.Client.PostAsJsonAsync($"/api/vacancies/{f.Vacancy.Id}/prepare",new{workflowDefinitionId=workflow.Id})).StatusCode);
        f.Db.ChangeTracker.Clear();var queued=await f.Db.Tasks.SingleAsync(x=>x.Kind=="PrepareApplication");
        Assert.True(queued.RunRequested);Assert.False(queued.Enabled);Assert.Equal(f.Vacancy.Id,queued.VacancyId);Assert.Equal(0,queued.MaxRetries);
        Assert.Equal(2,await f.Db.Tasks.CountAsync());Assert.True((await f.Db.Set<WorkflowDefinition>().SingleAsync()).AutoSubmit);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public string Directory { get; } = Path.Combine(Path.GetTempPath(), "jobflow-management-" + Guid.NewGuid().ToString("N"));
        private readonly string? previous = Environment.GetEnvironmentVariable("JOBFLOW_DATA_DIR");
        private WebApplicationFactory<Program> factory = null!;
        private IServiceScope scope = null!;
        public HttpClient Client { get; private set; } = null!;
        public JobFlowDb Db { get; private set; } = null!;
        public CandidateProfile Profile { get; private set; } = null!;
        public JobPlatform Platform { get; } = new() { Name = "Synthetic platform", Url = "https://example.invalid", Adapter = "browser" };
        public Vacancy Vacancy { get; private set; } = null!;
        public ResumeVersion Version { get; private set; } = null!;

        public static async Task<Fixture> CreateAsync()
        {
            var f = new Fixture();
            Environment.SetEnvironmentVariable("JOBFLOW_DATA_DIR", f.Directory);
            f.factory = new WebApplicationFactory<Program>();
            f.Client = f.factory.CreateClient();
            f.Client.DefaultRequestHeaders.Add("X-JobFlow-Client", "local-ui");
            f.scope = f.factory.Services.CreateScope(); f.Db = f.scope.ServiceProvider.GetRequiredService<JobFlowDb>();
            var user = new User(); f.Profile = new CandidateProfile { UserId = user.Id, Name = "Synthetic candidate", Skills = "C#" };
            var resume = new Resume { CandidateProfileId = f.Profile.Id };
            f.Version = new ResumeVersion { ResumeId = resume.Id, Version = 1 };
            f.Vacancy = new Vacancy { JobPlatformId = f.Platform.Id, ExternalId = "one", Title = "Synthetic C# developer", Description = "C#" };
            f.Db.AddRange(user, f.Profile, resume, f.Version, f.Platform, f.Vacancy); await f.Db.SaveChangesAsync();
            return f;
        }

        public async ValueTask DisposeAsync()
        {
            scope.Dispose(); Client.Dispose(); await factory.DisposeAsync();
            Environment.SetEnvironmentVariable("JOBFLOW_DATA_DIR", previous);
            SqliteConnection.ClearAllPools();
            if (System.IO.Directory.Exists(Directory)) System.IO.Directory.Delete(Directory, true);
        }
    }
}
