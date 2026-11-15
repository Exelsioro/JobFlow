using System.Text.Json;
using JobFlow.Application;
using JobFlow.Domain;
using JobFlow.Infrastructure;
using Microsoft.EntityFrameworkCore;
namespace JobFlow.IntegrationTests;
public class WorkflowTests
{
    [Fact] public async Task DiscoveryEvaluationDraftReviewIsDurableAndIdempotent()
    {
        await using var fixture = await Setup.Create();
        var db = fixture.Db;
        var workflow = new JobSearchWorkflow(db, new FakeAgent());
        var first = await workflow.RunAsync(fixture.Definition.Id, default);
        Assert.Equal("Succeeded", first.Status); Assert.Equal(1, first.Prepared);
        var application = await db.Applications.SingleAsync();
        Assert.Equal(ApplicationState.ReadyForReview, application.State);
        Assert.Contains("C#", application.CoverLetter);
        Assert.Equal(fixture.Definition.ResumeVersionId, application.ResumeVersionId);
        Assert.Single(await db.Set<VacancyEvaluation>().ToListAsync());
        var second = await workflow.RunAsync(fixture.Definition.Id, default);
        Assert.Equal(0, second.Prepared); Assert.Equal(1, await db.Applications.CountAsync());
        await workflow.SaveDraftAsync(application.Id, "Reviewed letter", application.Revision, default);
        Assert.Equal("Reviewed letter", application.CoverLetter);
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => workflow.SaveDraftAsync(application.Id, "Stale", 0, default));
        db.ChangeTracker.Clear();
        Assert.Equal(2, await db.Set<WorkflowRun>().CountAsync());
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OneRunProcessesOnlySelectedPlatforms(bool failedSource)
    {
        await using var fixture = await Setup.Create();
        var db = fixture.Db;
        var selected = new JobPlatform { Name = "Second", Adapter = "search-only" };
        var excluded = new JobPlatform { Name = "Outside selection" };
        var unavailable = new JobPlatform { Name = "Unavailable", Adapter = "headhunter", SearchFilters = "" };
        db.AddRange(selected, excluded, unavailable,
            new Vacancy { JobPlatformId = selected.Id, ExternalId = "one", Title = "Second vacancy" },
            new Vacancy { JobPlatformId = excluded.Id, ExternalId = "one", Title = "Excluded vacancy" });
        fixture.Definition.JobPlatformIds = failedSource
            ? [unavailable.Id, fixture.Definition.JobPlatformId, selected.Id]
            : [fixture.Definition.JobPlatformId, selected.Id];
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var workflow = new JobSearchWorkflow(db, new FakeAgent());
        if (failedSource)
            await Assert.ThrowsAsync<InvalidOperationException>(() => workflow.RunAsync(fixture.Definition.Id, default));
        else
            await workflow.RunAsync(fixture.Definition.Id, default);

        var run = await db.Set<WorkflowRun>().SingleAsync();
        Assert.Equal(2, run.Prepared);
        Assert.Equal(failedSource ? "Failed" : "Succeeded", run.Status);
        Assert.Equal(failedSource ? "Discovery failed: Unavailable" : "", run.Error);
        Assert.Equal(2, await db.Applications.CountAsync());
        Assert.False(await db.Applications.AnyAsync(app => db.Vacancies.Any(v => v.Id == app.VacancyId && v.JobPlatformId == excluded.Id)));
    }

    [Fact] public async Task AIStagesUseConfiguredAgentsAndFailureCanResume()
    {
        await using var fixture = await Setup.Create();
        var db = fixture.Db;
        var provider = new ModelProvider { Enabled = true };
        var evaluator = new AgentProfile { CandidateProfileId = fixture.Definition.CandidateProfileId, ModelProviderId = provider.Id, TaskType = "Evaluation", Enabled = true };
        var writer = new AgentProfile { CandidateProfileId = fixture.Definition.CandidateProfileId, ModelProviderId = provider.Id, TaskType = "CoverLetter", Enabled = true };
        db.AddRange(provider, evaluator, writer);
        fixture.Definition.EvaluationAgentId = evaluator.Id; fixture.Definition.LetterAgentId = writer.Id; await db.SaveChangesAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => new JobSearchWorkflow(db, new FakeAgent(true)).RunAsync(fixture.Definition.Id, default));
        Assert.Equal(ApplicationState.Blocked, (await db.Applications.SingleAsync()).State);
        var agent = new FakeAgent(false, evaluator.Id);
        var run = await new JobSearchWorkflow(db, agent).RunAsync(fixture.Definition.Id, default);
        Assert.Equal("Succeeded", run.Status); Assert.Equal(2, agent.Calls);
        Assert.Equal(ApplicationState.ReadyForReview, (await db.Applications.SingleAsync()).State);
    }
    [Fact] public void RulesDoNotInventMissingEvidenceOrCompareDifferentCurrencies()
    {
        var profile = new CandidateProfile { Skills = "C#" };
        var vacancy = new Vacancy { RequiredSkills = "C#, SQL", SalaryMax = 5000, Currency = "EUR" };
        var result = RuleEvaluator.Evaluate(profile, vacancy, new(MinimumSalary: 7000, Currency: "USD"), DateTime.UtcNow);
        Assert.Equal("NeedsReview", result.Decision); Assert.Contains("SQL", result.MissingSkills);
        Assert.Contains(result.Clarifications, x => x.Contains("currency"));
        vacancy.Location = "Excluded";
        Assert.Equal("Reject", RuleEvaluator.Evaluate(profile, vacancy, new(Locations: ["Allowed"]), DateTime.UtcNow).Decision);
    }
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task WorkerStopsActiveWorkflowAndRecordsTimeoutOrCancellation(bool stop)
    {
        await using var fixture=await Setup.Create();
        var db=fixture.Db;var provider=new ModelProvider{Enabled=true};
        var evaluator=new AgentProfile{CandidateProfileId=fixture.Definition.CandidateProfileId,ModelProviderId=provider.Id,TaskType="Evaluation",Enabled=true};
        fixture.Definition.EvaluationAgentId=evaluator.Id;
        var task=new ScheduledTask{CandidateProfileId=fixture.Definition.CandidateProfileId,Kind="Workflow",WorkflowDefinitionId=fixture.Definition.Id,RunRequested=true,TimeoutSeconds=5,MaxRetries=0};
        db.AddRange(provider,evaluator,task);await db.SaveChangesAsync();db.ChangeTracker.Clear();
        var agent=new WaitingAgent();var running=new TaskProcessor(db,agent).RunOneAsync(default);
        await agent.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        if(stop){
            await using var control=new JobFlowDb(new DbContextOptionsBuilder<JobFlowDb>().UseSqlite(db.Database.GetConnectionString()).Options);
            await control.Tasks.Where(x=>x.Id==task.Id).ExecuteUpdateAsync(s=>s.SetProperty(x=>x.CancelRequested,true).SetProperty(x=>x.Enabled,false));
        }
        Assert.True(await running.WaitAsync(TimeSpan.FromSeconds(10)));
        db.ChangeTracker.Clear();
        Assert.Equal(stop?"Cancelled":"Failed",(await db.Runs.SingleAsync()).Status);
        Assert.False((await db.Tasks.SingleAsync()).RunRequested);
        Assert.Equal(ApplicationState.Blocked,(await db.Applications.SingleAsync()).State);
    }
    private class WaitingAgent:IAgentExecutor
    {
        public TaskCompletionSource Started=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<AgentExecutionResult> ExecuteAsync(AgentExecutionRequest request,CancellationToken ct){
            Started.SetResult();await Task.Delay(Timeout.Infinite,ct);throw new InvalidOperationException();
        }
    }
    private class FakeAgent(bool fail = false, Guid? evaluation = null) : IAgentExecutor
    {
        public int Calls;
        public Task<AgentExecutionResult> ExecuteAsync(AgentExecutionRequest request, CancellationToken ct)
        {
            Calls++;
            if (fail) throw new InvalidOperationException("Synthetic failure.");
            var output = request.AgentProfileId == evaluation ?
                """{"decision":"NeedsReview","rationale":"Review conditions","matches":["C#"],"missingSkills":[],"unverifiedRequirements":[],"clarifications":[],"confidence":"Medium"}""" :
                """{"letter":"I have C# skills.","usedFacts":["C#"]}""";
            return Task.FromResult(new AgentExecutionResult(Guid.NewGuid(), "Succeeded", output));
        }
    }
    private sealed class Setup : IAsyncDisposable
    {
        private readonly string file = Path.Combine(Path.GetTempPath(), "jobflow-workflow-" + Guid.NewGuid() + ".db");
        public JobFlowDb Db { get; private set; } = null!;
        public WorkflowDefinition Definition { get; private set; } = null!;
        public static async Task<Setup> Create()
        {
            var f = new Setup(); f.Db = new(new DbContextOptionsBuilder<JobFlowDb>().UseSqlite($"Data Source={f.file};Pooling=False").Options);
            await f.Db.Database.MigrateAsync();
            var user = new User(); var profile = new CandidateProfile { UserId = user.Id, Name = "Synthetic", Skills = "C#" };
            var resume = new Resume { CandidateProfileId = profile.Id }; var version = new ResumeVersion { ResumeId = resume.Id, Version = 1 };
            var platform = new JobPlatform { Adapter = "search-only" };
            var vacancy = new Vacancy { JobPlatformId = platform.Id, ExternalId = "one", Title = "Engineer", RequiredSkills = "C#" };
            f.Definition = new WorkflowDefinition { Name = "Prepare", CandidateProfileId = profile.Id, JobPlatformId = platform.Id, ResumeVersionId = version.Id, Enabled = true };
            f.Db.AddRange(user, profile, resume, version, platform, vacancy, f.Definition); await f.Db.SaveChangesAsync(); return f;
        }
        public async ValueTask DisposeAsync() { await Db.DisposeAsync(); File.Delete(file); }
    }
}

