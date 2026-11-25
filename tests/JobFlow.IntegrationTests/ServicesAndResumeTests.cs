using System.Text.Json;
using JobFlow.Application;
using JobFlow.Domain;
using JobFlow.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace JobFlow.IntegrationTests;
public class ServicesAndResumeTests
{
    private const string SmokeTestQuote = "Implemented separate EF Core migration steps and smoke tests.";

    [Fact]
    public async Task AgentSelectsSourceCreatesSeparateReadablePdfAndPersistsProvenance()
    {
        await using var f = await ResumeFixture.CreateAsync();
        await new ResumeAgentService(f.Db, f.Agent).PrepareAsync(f.Workflow, f.Profile, f.Vacancy, f.Application, default);
        var result = await f.Db.ResumeVersions.SingleAsync(x => x.Id == f.Application.ResumeVersionId);
        Assert.Equal("AgentGenerated", result.Origin);
        Assert.Equal(f.Source.Id, result.SourceResumeVersionId);
        Assert.Equal(f.Vacancy.Id, result.VacancyId);
        Assert.NotNull(result.AgentExecutionId);
        Assert.StartsWith("generated/", result.StorageKey);
        var pdfText = ResumeAgentService.Extract(ResumeAgentService.PathFor(result));
        Assert.Contains("C# engineer", pdfText);
        Assert.Contains("Разработка", pdfText);
        Assert.Contains("smoke-тесты", pdfText);
        Assert.DoesNotContain(SmokeTestQuote, pdfText);
        Assert.Equal("Uploaded", f.Source.Origin);
        Assert.Equal(3, f.Agent.Calls.Count);
        Assert.All(f.Agent.Calls, call => Assert.False(string.IsNullOrWhiteSpace(call.Instructions)));
        Assert.True(File.Exists(ResumeAgentService.PathFor(f.Source)));
        Assert.NotEmpty(result.DossierHash);
    }
    [Fact]
    public async Task SelectionCanChooseUploadedPdfWithoutGeneratingOne()
    {
        await using var f = await ResumeFixture.CreateAsync(); f.Workflow.ResumeMode = "select";
        await new ResumeAgentService(f.Db, f.Agent).PrepareAsync(f.Workflow, f.Profile, f.Vacancy, f.Application, default);
        Assert.Equal(f.Source.Id, f.Application.ResumeVersionId);
        Assert.Single(f.Agent.Calls);
        Assert.Equal(2, await f.Db.ResumeVersions.CountAsync());
    }
    [Theory]
    [InlineData("foreign")]
    [InlineData("evidence")]
    [InlineData("wrong-source")]
    [InlineData("verification")]
    public async Task UnsupportedResumeOutputCannotProduceAnApplicationPdf(string fault)
    {
        await using var f = await ResumeFixture.CreateAsync(); f.Agent.Fault = fault;
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => new ResumeAgentService(f.Db, f.Agent).PrepareAsync(f.Workflow, f.Profile, f.Vacancy, f.Application, default));
        Assert.False(await f.Db.ResumeVersions.AnyAsync(x => x.Origin == "AgentGenerated"));
        if (fault == "wrong-source")
            Assert.DoesNotContain(f.Agent.Calls, call => call.OutputSchema == ResumeAgentService.VerifySchema);
    }
    [Fact]
    public async Task DossierProposalDoesNotChangeProfileUntilConfirmedAndCannotBeAppliedTwice()
    {
        await using var f = await ResumeFixture.CreateAsync();
        var service = new ResumeAgentService(f.Db, f.Agent);
        var before = f.Profile.Experience;
        var proposal = await service.ProposeAsync(f.Profile.Id, f.Source.Id, f.Agent.Id, default);
        Assert.Equal(before, f.Profile.Experience);
        await service.ConfirmAsync(proposal.Id, default);
        Assert.Equal("C# engineer", f.Profile.Experience);
        Assert.NotNull(proposal.ConfirmedAt);
        f.Profile.Experience = "Manually corrected"; await f.Db.SaveChangesAsync();
        await service.ConfirmAsync(proposal.Id, default);
        Assert.Equal("Manually corrected", f.Profile.Experience);
    }
    [Fact]
    public async Task SearchAndMonitoringProcessorsTakeOnlyTheirOwnTasksAndNotify()
    {
        await using var f = await ResumeFixture.CreateAsync();
        var search = new ScheduledTask { CandidateProfileId = f.Profile.Id, Kind = "HealthCheck", RunRequested = true, NextRunAt = DateTime.UtcNow.AddMinutes(-1) };
        var integration = new Integration { CandidateProfileId = f.Profile.Id, Kind = "feedback-json", Enabled = true, Endpoint = "http://127.0.0.1:1/feedback" };
        var monitor = new ScheduledTask { CandidateProfileId = f.Profile.Id, Kind = "Integration", IntegrationId = integration.Id, RunRequested = true };
        f.Db.AddRange(search, integration, monitor); await f.Db.SaveChangesAsync();
        Assert.True(await new TaskProcessor(f.Db, role: "monitor").RunOneAsync(default));
        Assert.Equal(monitor.Id, (await f.Db.Runs.SingleAsync()).ScheduledTaskId);
        Assert.True((await f.Db.Tasks.AsNoTracking().SingleAsync(x => x.Id == search.Id)).RunRequested);
        Assert.True(await new TaskProcessor(f.Db, role: "search").RunOneAsync(default));
        Assert.Equal(2, await f.Db.Runs.CountAsync());
        Assert.Equal(2, await f.Db.Set<Notification>().CountAsync());
    }
    [Theory]
    [InlineData("daily", "2026-11-17T16:00:00Z", "09:00", "2026-11-17T17:00:00Z")]
    [InlineData("daily", "2026-11-17T18:00:00Z", "09:00", "2026-11-18T17:00:00Z")]
    [InlineData("weekly", "2026-11-21T18:00:00Z", "09:00", "2026-11-23T17:00:00Z")]
    [InlineData("daily", "2026-03-08T09:00:00Z", "02:30", "2026-03-08T10:00:00Z")]
    [InlineData("daily", "2026-11-01T07:00:00Z", "01:30", "2026-11-01T08:30:00Z")]
    public void CalendarScheduleUsesTimeZoneWeekdaysAndDaylightTransitions(string kind, string now, string time, string expected)
    {
        var task = new ScheduledTask { ScheduleKind = kind, TimeZone = "America/Los_Angeles", LocalTime = time };
        Assert.Equal(DateTime.Parse(expected).ToUniversalTime(), TaskSchedule.Next(task, DateTime.Parse(now).ToUniversalTime()));
    }
    [Fact]
    public void IntervalSkipsMissedSlotsAndKeepsCadence()
    {
        var start = new DateTime(2026, 11, 17, 9, 0, 0, DateTimeKind.Utc);
        var task = new ScheduledTask { NextRunAt = start, IntervalMinutes = 30 };
        Assert.Equal(start.AddMinutes(90), TaskSchedule.Next(task, start.AddMinutes(76)));
    }
    [Theory]
    [InlineData("Published today", 0)]
    [InlineData("Опубликована вчера", -1)]
    [InlineData("Posted 3 days ago", -3)]
    public void RelativePublicationDateRequiresResolvableEvidence(string evidence, int days)
    {
        var now = DateTime.UtcNow;
        Assert.Equal(now.Date.AddDays(days), BrowserWorkflowDriver.ResolvePublicationDate(evidence, now));
        Assert.Null(BrowserWorkflowDriver.ResolvePublicationDate("Full requirement C#", now));
        Assert.Null(BrowserWorkflowDriver.ResolvePublicationDate("Updated today", now));
    }
    [Theory]
    [InlineData("evidence-once",4)]
    [InlineData("verification-once",5)]
    public async Task ResumeCorrectionReceivesPreciseIssuesAndRetainsBothDrafts(string fault,int calls)
    {
        await using var f=await ResumeFixture.CreateAsync();f.Agent.Fault=fault;
        var old = await f.Db.ResumeVersions.SingleAsync(x => x.Id == f.Application.ResumeVersionId);
        old.Origin = "AgentGenerated";
        old.AdaptationJson = """{"title":"Legacy draft","sections":[{"heading":"Experience","text":"Old wording","evidence":["C# engineer"]}]}""";
        await f.Db.SaveChangesAsync();

        await new ResumeAgentService(f.Db,f.Agent).PrepareAsync(f.Workflow,f.Profile,f.Vacancy,f.Application,default);
        Assert.Equal(calls,f.Agent.Calls.Count);
        var corrections=f.Agent.Calls.Where(x=>x.OutputSchema==ResumeAgentService.AdaptSchema).ToArray();Assert.Equal(2,corrections.Length);
        using var firstInput = JsonDocument.Parse(corrections[0].Input);
        Assert.Equal("Legacy draft", firstInput.RootElement.GetProperty("previousDraft").GetProperty("title").GetString());
        using var input=JsonDocument.Parse(corrections[1].Input);Assert.NotEmpty(input.RootElement.GetProperty("correctionIssues").EnumerateArray());
        Assert.NotEqual(JsonValueKind.Null,input.RootElement.GetProperty("previousDraft").ValueKind);
        var adaptationExecutions=await f.Db.Set<AgentExecution>().Where(x=>x.Output.Contains("sections")).ToListAsync();Assert.Equal(2,adaptationExecutions.Count);
        var generated = await f.Db.ResumeVersions.SingleAsync(x => x.Origin == "AgentGenerated" && x.Id != old.Id);
        Assert.Contains("smoke-тесты", generated.ExtractedText);
        Assert.DoesNotContain("проверки после развёртывания", generated.ExtractedText);
        Assert.Equal("Uploaded",f.Source.Origin);
    }

    private sealed class ResumeFixture : IAsyncDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "jobflow-resume-" + Guid.NewGuid().ToString("N"));
        private readonly string? previous = Environment.GetEnvironmentVariable("JOBFLOW_DATA_DIR");
        public JobFlowDb Db { get; private set; } = null!;
        public CandidateProfile Profile { get; private set; } = null!;
        public ResumeVersion Source { get; private set; } = null!;
        public Vacancy Vacancy { get; private set; } = null!;
        public JobApplication Application { get; private set; } = null!;
        public WorkflowDefinition Workflow { get; private set; } = null!;
        public ResumeAgent Agent { get; private set; } = null!;
        public static async Task<ResumeFixture> CreateAsync()
        {
            var f = new ResumeFixture(); Directory.CreateDirectory(f.root); Environment.SetEnvironmentVariable("JOBFLOW_DATA_DIR", f.root);
            f.Db = new(new DbContextOptionsBuilder<JobFlowDb>().UseSqlite(Storage.ConnectionString).Options); await f.Db.Database.MigrateAsync();
            var user = new User(); f.Profile = new() { UserId = user.Id, Name = "Synthetic Candidate", Skills = "C#, SQL", Dossier = "C# engineer. SQL development. Разработка приложений." };
            var resume = new Resume { CandidateProfileId = f.Profile.Id, Name = "C# resume" };
            f.Source = new() { ResumeId = resume.Id, Version = 1, StorageKey = "source.pdf", OriginalName = "source.pdf" };
            var other = new ResumeVersion { ResumeId = resume.Id, Version = 2, StorageKey = "other.pdf", OriginalName = "other.pdf" };
            var platform = new JobPlatform(); f.Vacancy = new() { JobPlatformId = platform.Id, ExternalId = "job-1", Title = "C# Engineer", Url = "https://example.invalid/jobs/1" };
            var provider = new ModelProvider { Enabled = true }; var agent = new AgentProfile { CandidateProfileId = f.Profile.Id, ModelProviderId = provider.Id, Enabled = true, TaskType = "Resume" };
            f.Workflow = new() { CandidateProfileId = f.Profile.Id, JobPlatformId = platform.Id, ResumeVersionId = f.Source.Id, ResumeMode = "tailor", ResumeAgentId = agent.Id };
            f.Application = new() { CandidateProfileId = f.Profile.Id, VacancyId = f.Vacancy.Id, ResumeVersionId = other.Id, State = ApplicationState.Drafting };
            f.Db.AddRange(user, f.Profile, resume, f.Source, other, platform, f.Vacancy, provider, agent, f.Workflow, f.Application); await f.Db.SaveChangesAsync();
            var bytes = await ResumeAgentService.RenderAsync("Synthetic resume", [("Experience", f.Profile.Dossier)], default);
            f.Profile.Dossier += "\n" + SmokeTestQuote;
            await f.Db.SaveChangesAsync();
            Directory.CreateDirectory(Path.Combine(f.root, "resumes")); await File.WriteAllBytesAsync(ResumeAgentService.PathFor(f.Source), bytes);
            await File.WriteAllBytesAsync(ResumeAgentService.PathFor(other), bytes);
            f.Agent = new(f.Db, agent.Id, f.Source.Id); return f;
        }
        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync(); Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Environment.SetEnvironmentVariable("JOBFLOW_DATA_DIR", previous); Directory.Delete(root, true);
        }
    }
    private sealed class ResumeAgent(JobFlowDb db, Guid id, Guid source) : IAgentExecutor
    {
        public Guid Id => id;
        public string Fault { get; set; } = "";
        public List<AgentExecutionRequest> Calls { get; } = [];
        public async Task<AgentExecutionResult> ExecuteAsync(AgentExecutionRequest request, CancellationToken ct)
        {
            Calls.Add(request); var execution = new AgentExecution { AgentProfileId = id, Status = "Succeeded", TaskInstructions = request.Instructions };
            db.Add(execution); await db.SaveChangesAsync(ct);
            var firstDraft = Calls.Count(x => x.OutputSchema == ResumeAgentService.AdaptSchema) == 1;
            var invalidQuote = Fault == "evidence" || Fault == "evidence-once" && firstDraft;
            var unsupportedMeaning = Fault == "verification" || Fault == "verification-once" && firstDraft;
            var output = request.OutputSchema switch
            {
                ResumeAgentService.SelectionSchema => JsonSerializer.Serialize(new { resumeVersionId = Fault == "foreign" ? Guid.NewGuid() : source, reason = "C# evidence" }),
                ResumeAgentService.AdaptSchema => JsonSerializer.Serialize(new { title = "Synthetic Candidate", sections = new[] { new
                    {
                        heading = "Experience",
                        claims = new[]
                        {
                            new { text = "C# engineer.", source = "sourceResume", quote = "C# engineer." },
                            new { text = "Разработка приложений.", source = "dossier", quote = "Разработка приложений." },
                            new
                            {
                                text = unsupportedMeaning ? "Внедрил проверки после развёртывания." : "Внедрил smoke-тесты.",
                                source = Fault == "wrong-source" ? "sourceResume" : "dossier",
                                quote = invalidQuote ? "Invented employer" : SmokeTestQuote
                            }
                        }
                    } } }),
                ResumeAgentService.VerifySchema => CheckClaims(request, unsupportedMeaning),
                _ => JsonSerializer.Serialize(new { experience = "C# engineer", skills = "C#, SQL", education = "", languages = "", facts = new[] { "C# engineer" } })
            };
            return new(execution.Id, "Succeeded", output);
        }

        private static string CheckClaims(AgentExecutionRequest request, bool unsupportedMeaning)
        {
            using var input = JsonDocument.Parse(request.Input);
            var claim = input.RootElement.GetProperty("adapted").GetProperty("sections")[0].GetProperty("claims")[2];
            Assert.Equal("dossier", claim.GetProperty("source").GetString());
            Assert.Equal(SmokeTestQuote, claim.GetProperty("quote").GetString());
            Assert.Equal(unsupportedMeaning, claim.GetProperty("text").GetString()!.Contains("после развёртывания"));
            return JsonSerializer.Serialize(new
            {
                supported = !unsupportedMeaning,
                issues = unsupportedMeaning ? new[] { "Исходник называет smoke tests, но не указывает момент запуска." } : Array.Empty<string>()
            });
        }
    }
}
