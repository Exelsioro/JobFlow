using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using JobFlow.Api;
using JobFlow.Domain;
using JobFlow.Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace JobFlow.IntegrationTests;

public class ApplicationDetailsTests
{
    [Fact]
    public async Task ClosureRepairOnlyReopensStatusForAnUnverifiedClosure()
    {
        await using var f = await Fixture.CreateAsync();
        await f.Db.GetService<IMigrator>().MigrateAsync("20261123010000_PreparationAgeException");
        var unverified = await f.Db.Vacancies.SingleAsync();
        unverified.Availability = "Closed";
        unverified.VerifiedAt = DateTime.UtcNow.AddMinutes(-1);
        var confirmed = new Vacancy
        {
            JobPlatformId = unverified.JobPlatformId, ExternalId = "confirmed-closure",
            Availability = "Closed", VerifiedAt = unverified.VerifiedAt
        };
        f.Db.AddRange(confirmed,
            new AuditEvent { EntityId = unverified.Id, Action = "VacancyFreshnessChecked",
                Details = """{"availability":"Closed","validationFailures":["Open-status evidence is not an exact nonempty source quote."]}""" },
            new AuditEvent { EntityId = confirmed.Id, Action = "VacancyFreshnessChecked",
                Details = """{"availability":"Closed","statusEvidence":"No longer accepting applications"}""" });
        await f.Db.SaveChangesAsync();
        await f.Db.Database.MigrateAsync();
        Assert.Equal("Unknown", (await f.Db.Vacancies.AsNoTracking().SingleAsync(x => x.Id == unverified.Id)).Availability);
        Assert.Equal("Closed", (await f.Db.Vacancies.AsNoTracking().SingleAsync(x => x.Id == confirmed.Id)).Availability);
    }

    [Fact]
    public async Task WorkflowRunRequestWaitsBehindSinglePreparationAndDoesNotDuplicate()
    {
        await using var f = await Fixture.CreateAsync();
        var vacancy = await f.Db.Vacancies.SingleAsync();
        var workflow = new WorkflowDefinition
        {
            Name = "Queued workflow", CheckMail = false,
            CandidateProfileId = f.Application.CandidateProfileId, JobPlatformId = vacancy.JobPlatformId,
            ResumeVersionId = f.Source.Id, Enabled = true, LeaseUntil = DateTime.UtcNow.AddMinutes(5)
        };
        var preparation = new ScheduledTask
        {
            Kind = "PrepareApplication", WorkflowDefinitionId = workflow.Id,
            CandidateProfileId = workflow.CandidateProfileId, VacancyId = vacancy.Id, LeaseUntil = workflow.LeaseUntil
        };
        var batch = new ScheduledTask
        {
            Kind = "Workflow", WorkflowDefinitionId = workflow.Id, CandidateProfileId = workflow.CandidateProfileId
        };
        f.Db.AddRange(workflow, preparation, batch);
        await f.Db.SaveChangesAsync();

        var url = $"/api/workflows/{workflow.Id}/run";
        var queued = await (await f.Client.PostAsync(url, null)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(queued.GetProperty("queued").GetBoolean());
        Assert.False(queued.GetProperty("alreadyQueued").GetBoolean());
        var repeated = await (await f.Client.PostAsync(url, null)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(repeated.GetProperty("alreadyQueued").GetBoolean());
        Assert.False(await new TaskProcessor(f.Db).RunOneAsync(default));
        Assert.True((await f.Db.Tasks.AsNoTracking().SingleAsync(x => x.Id == batch.Id)).RunRequested);

        workflow.LeaseUntil = null;
        await f.Db.SaveChangesAsync();
        Assert.False(await new TaskProcessor(f.Db).RunOneAsync(default));

        preparation.LeaseUntil = null;
        await f.Db.SaveChangesAsync();
        Assert.True(await new TaskProcessor(f.Db).RunOneAsync(default));
        var run = await f.Db.Runs.SingleAsync();
        Assert.Equal(batch.Id, run.ScheduledTaskId);
        Assert.Equal("Succeeded", run.Status);
        Assert.False(await new TaskProcessor(f.Db).RunOneAsync(default));
    }

    [Fact]
    public async Task ClarificationEndpointQueuesReworkAndShowsPersistedTextAndQueueStatus()
    {
        await using var f = await Fixture.CreateAsync();
        var vacancy = await f.Db.Vacancies.SingleAsync(); vacancy.Url = "https://example.invalid/job";
        vacancy.PublishedAt = DateTime.UtcNow.AddDays(-20);
        var platform = await f.Db.Platforms.SingleAsync(); platform.Url = "https://example.invalid/";
        var provider = new ModelProvider { Enabled = true }; var browser = new BrowserProfile();
        var agent = new AgentProfile { CandidateProfileId = f.Application.CandidateProfileId, ModelProviderId = provider.Id, TaskType = "BrowserAutomation", Enabled = true };
        var evaluator = new AgentProfile { CandidateProfileId = f.Application.CandidateProfileId, ModelProviderId = provider.Id, TaskType = "Evaluation", Enabled = true };
        var workflow = new WorkflowDefinition { Name = "Clarification workflow", CandidateProfileId = f.Application.CandidateProfileId, JobPlatformId = platform.Id,
            ResumeVersionId = f.Source.Id, BrowserProfileId = browser.Id, BrowserAgentId = agent.Id, EvaluationAgentId = evaluator.Id, ExecutionMode = "browser", Enabled = true, CheckMail = false, FreshnessDays = 3 };
        f.Application.WorkflowDefinitionId = workflow.Id; f.Db.AddRange(provider, browser, agent, evaluator, workflow);
        f.Db.Add(new AuditEvent { EntityId = f.Application.Id, Action = ExternalApplicationDraft.AuditAction,
            Details = JsonSerializer.Serialize(new ExternalApplicationDraft(f.Application.Id, browser.Id, vacancy.Url, "https://company.example/form", true, "Previous draft", [], "fingerprint", 0, "hash", DateTime.UtcNow)) });
        f.Db.Add(new AuditEvent
        {
            EntityId = f.Application.Id, Action = "BrowserApplicationBlocked",
            Details = "Browser read: Vacancy freshness or open status is unverified, closed, or outside the configured publication age. Publication date exceeds the configured age limit. Diagnostics: /api/browser-diagnostics/test"
        });
        await f.Db.SaveChangesAsync();
        var before = await f.Client.GetFromJsonAsync<JsonElement>("/api/applications/" + f.Application.Id);
        Assert.Equal(3, before.GetProperty("preparationAgeLimit").GetInt32());
        Assert.True(before.GetProperty("ageOnlyAttention").GetBoolean());
        Assert.Equal("При прошлой проверке вакансия была старше лимита давности.", before.GetProperty("attention").GetString());
        var url = "/api/applications/" + f.Application.Id + "/clarifications";
        const string clarification = "Подтверждаю рабочие часы CET.";
        Assert.Equal(HttpStatusCode.BadRequest, (await f.Client.PostAsJsonAsync(url, new { text = clarification, revision = 0 })).StatusCode);
        Assert.Empty(await f.Db.Tasks.ToListAsync());
        Assert.Empty(await ApplicationClarifications.ReadAsync(f.Db, f.Application.Id, default));
        Assert.Equal(HttpStatusCode.OK, (await f.Client.PostAsJsonAsync(url, new { text = clarification, revision = 0, outsideAgeLimit = true })).StatusCode);
        var detail = await f.Client.GetFromJsonAsync<JsonElement>("/api/applications/" + f.Application.Id);
        Assert.Equal("Evaluating", detail.GetProperty("application").GetProperty("state").GetString());
        Assert.Equal("Queued", detail.GetProperty("clarification").GetProperty("status").GetString());
        Assert.Equal(clarification, detail.GetProperty("clarification").GetProperty("text").GetString());
        Assert.False(detail.GetProperty("externalDraft").GetBoolean()); Assert.Null(await ExternalApplicationDraft.ReadAsync(f.Db, f.Application.Id));
        Assert.NotEqual(HttpStatusCode.OK, (await f.Client.PostAsJsonAsync(url, new { text = "Повтор", revision = 0 })).StatusCode);
        Assert.True((await f.Db.Tasks.SingleAsync()).PrepareOutsideAgeLimit);
        Assert.Single(await ApplicationClarifications.ReadAsync(f.Db, f.Application.Id, default));
    }

    [Fact]
    public async Task HistoricalSuccessfulTaskWithSkippedVacancyShowsReasonInsteadOfCompletedRework()
    {
        await using var f = await Fixture.CreateAsync();
        var vacancy = await f.Db.Vacancies.SingleAsync();
        var workflow = new WorkflowDefinition
        {
            CandidateProfileId = f.Application.CandidateProfileId, JobPlatformId = vacancy.JobPlatformId,
            ResumeVersionId = f.Source.Id
        };
        var task = new ScheduledTask
        {
            Kind = "PrepareApplication", CandidateProfileId = f.Application.CandidateProfileId,
            WorkflowDefinitionId = workflow.Id, VacancyId = vacancy.Id
        };
        var note = ApplicationClarifications.Create(f.Application.Id, "Check this vacancy again", task.Id);
        var finishedAt = note.CreatedAt.AddMinutes(1);
        var run = new WorkflowRun
        {
            WorkflowDefinitionId = workflow.Id, CandidateProfileId = f.Application.CandidateProfileId,
            TargetVacancyId = vacancy.Id, PreparationOnly = true, Status = "Succeeded", Skipped = 1,
            CreatedAt = note.CreatedAt.AddSeconds(2), FinishedAt = finishedAt.AddSeconds(-1)
        };
        const string reason = "Publication date exceeds the workflow age limit.";
        f.Application.WorkflowDefinitionId = workflow.Id;
        f.Application.State = ApplicationState.Skipped;
        f.Db.AddRange(workflow, task, note, run,
            new TaskRun { ScheduledTaskId = task.Id, Status = "Succeeded", CreatedAt = note.CreatedAt.AddSeconds(1), FinishedAt = finishedAt },
            new VacancyEvaluation
            {
                VacancyId = vacancy.Id, CandidateProfileId = f.Application.CandidateProfileId,
                CreatedAt = note.CreatedAt.AddDays(-1), ResultJson = "{}"
            },
            new AuditEvent
            {
                EntityId = run.Id, Action = "WorkflowVacancySkipped", CreatedAt = finishedAt.AddSeconds(-2),
                Details = JsonSerializer.Serialize(new { details = new { applicationId = f.Application.Id, reason } })
            });
        await f.Db.SaveChangesAsync();

        var detail = await f.Client.GetFromJsonAsync<JsonElement>("/api/applications/" + f.Application.Id);
        Assert.Equal("Skipped", detail.GetProperty("clarification").GetProperty("status").GetString());
        Assert.Equal(reason, detail.GetProperty("attention").GetString());
        Assert.False(detail.GetProperty("evaluationIsCurrent").GetBoolean());
    }

    [Fact]
    public async Task NoPriorJournalDeclarationAllowsApprovalWithoutMarkingAnImport()
    {
        await using var f = await Fixture.CreateAsync();
        f.Application.State = ApplicationState.ReadyForReview;
        f.Application.CoverLetter = "Reviewed synthetic letter";
        var vacancy = await f.Db.Vacancies.SingleAsync();
        var source = new Integration
        {
            CandidateProfileId = f.Application.CandidateProfileId, JobPlatformId = vacancy.JobPlatformId, Kind = "google-sheets"
        };
        f.Db.Add(source);
        await f.Db.SaveChangesAsync();
        var detailUrl = "/api/applications/" + f.Application.Id;
        var before = await f.Client.GetFromJsonAsync<JsonElement>(detailUrl);
        Assert.True(before.GetProperty("journalApprovalPending").GetBoolean());

        var declaration = await f.Client.PostAsJsonAsync("/api/integrations/" + source.Id + "/no-prior-journal", new { });
        Assert.Equal(HttpStatusCode.OK, declaration.StatusCode);
        Assert.False((await f.Client.GetFromJsonAsync<JsonElement>(detailUrl)).GetProperty("journalApprovalPending").GetBoolean());
        Assert.Equal(HttpStatusCode.OK, (await f.Client.PostAsJsonAsync(detailUrl + "/transition", new { state = "ReadyToSubmit", revision = 0 })).StatusCode);
        Assert.False((await f.Db.Set<Integration>().AsNoTracking().SingleAsync()).ImportConfirmed);
        Assert.Empty(await f.Db.Set<ApplicationAttempt>().ToListAsync());

        f.Db.Add(new JournalImport { IntegrationId = source.Id, Status = "Preview" });
        await f.Db.SaveChangesAsync();
        Assert.True((await f.Client.GetFromJsonAsync<JsonElement>(detailUrl)).GetProperty("journalApprovalPending").GetBoolean());
    }

    [Fact]
    public async Task DetailReadsCurrentStateAndCurrentResumeAndClearsHistoricalBlockReason()
    {
        await using var f = await Fixture.CreateAsync();
        var response = await f.Client.GetAsync("/api/applications/" + f.Application.Id);
        Assert.Equal("no-store", response.Headers.CacheControl!.ToString());
        var before = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Blocked", before.GetProperty("application").GetProperty("state").GetString());
        Assert.Equal("Review exact source quote", before.GetProperty("attention").GetString());
        Assert.Equal(f.Source.Id, before.GetProperty("resume").GetProperty("id").GetGuid());
        Assert.DoesNotContain("Submitted", before.GetProperty("allowedTransitions").EnumerateArray().Select(x => x.GetString()));
        var adapted = new ResumeVersion { ResumeId = f.Source.ResumeId, SourceResumeVersionId = f.Source.Id, Origin = "AgentGenerated", Version = 2,
            OriginalName = "current-adapted.pdf", StorageKey = "current.pdf" };
        f.Db.Add(adapted); f.Application.State = ApplicationState.ReadyForReview; f.Application.Revision++;
        f.Application.ResumeVersionId = adapted.Id; f.Application.CoverLetter = "Latest letter"; await f.Db.SaveChangesAsync();
        var after = await f.Client.GetFromJsonAsync<JsonElement>("/api/applications/" + f.Application.Id);
        Assert.Equal("ReadyForReview", after.GetProperty("application").GetProperty("state").GetString());
        Assert.Equal("Latest letter", after.GetProperty("application").GetProperty("coverLetter").GetString());
        Assert.Equal(JsonValueKind.Null, after.GetProperty("attention").ValueKind);
        Assert.Equal(adapted.Id, after.GetProperty("resume").GetProperty("id").GetGuid());
        Assert.Equal("AgentGenerated", after.GetProperty("resume").GetProperty("origin").GetString());
        Assert.Equal(HttpStatusCode.Conflict, (await f.Client.PostAsJsonAsync("/api/applications/" + f.Application.Id + "/transition", new { state = "Rejected", revision = 0 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await f.Client.PostAsJsonAsync("/api/applications/" + f.Application.Id + "/transition", new { state = "Submitted", revision = f.Application.Revision })).StatusCode);
    }

    [Fact]
    public async Task PdfPreviewIsInlineAndApprovalRequirementsAreVisible()
    {
        await using var f = await Fixture.CreateAsync();
        var pdf = await f.Client.GetAsync("/api/resumes/versions/" + f.Source.Id + "/preview");
        Assert.Equal(HttpStatusCode.OK, pdf.StatusCode);
        Assert.Equal("application/pdf", pdf.Content.Headers.ContentType!.MediaType);
        Assert.Null(pdf.Content.Headers.ContentDisposition);
        Assert.Equal("%PDF-1.4\nSynthetic source", await pdf.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.NotFound, (await f.Client.GetAsync("/api/applications/" + Guid.NewGuid())).StatusCode);
        f.Application.State = ApplicationState.ReadyForReview; f.Application.CoverLetter = ""; await f.Db.SaveChangesAsync();
        var view = await f.Client.GetFromJsonAsync<JsonElement>("/api/applications/" + f.Application.Id);
        Assert.Contains("ReadyToSubmit", view.GetProperty("allowedTransitions").EnumerateArray().Select(x => x.GetString()));
        Assert.NotEmpty(view.GetProperty("approvalBlockers").EnumerateArray());
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "jobflow-application-details-" + Guid.NewGuid().ToString("N"));
        private readonly string? previous = Environment.GetEnvironmentVariable("JOBFLOW_DATA_DIR");
        public JobFlowDb Db = null!;
        public JobApplication Application = null!;
        public ResumeVersion Source = null!;
        public WebApplicationFactory<Program> Factory = null!;
        public HttpClient Client = null!;
        public static async Task<Fixture> CreateAsync()
        {
            var f = new Fixture(); Directory.CreateDirectory(f.root); Environment.SetEnvironmentVariable("JOBFLOW_DATA_DIR", f.root);
            f.Db = new(new DbContextOptionsBuilder<JobFlowDb>().UseSqlite(Storage.ConnectionString).Options); await f.Db.Database.MigrateAsync();
            var user = new User(); var profile = new CandidateProfile { UserId = user.Id, Name = "Synthetic Candidate" };
            var platform = new JobPlatform(); var vacancy = new Vacancy { JobPlatformId = platform.Id, Title = "Synthetic C# Developer" };
            var resume = new Resume { CandidateProfileId = profile.Id };
            f.Source = new() { ResumeId = resume.Id, StorageKey = "source.pdf", OriginalName = "source.pdf", Version = 1 };
            f.Application = new() { CandidateProfileId = profile.Id, VacancyId = vacancy.Id, ResumeVersionId = f.Source.Id, State = ApplicationState.Blocked };
            f.Db.AddRange(user, profile, platform, vacancy, resume, f.Source, f.Application,
                new AuditEvent { EntityId = f.Application.Id, EntityType = nameof(JobApplication), Action = "BrowserApplicationBlocked", Details = "Review exact source quote" });
            await f.Db.SaveChangesAsync(); Directory.CreateDirectory(Path.Combine(f.root, "resumes"));
            await File.WriteAllTextAsync(ResumeAgentService.PathFor(f.Source), "%PDF-1.4\nSynthetic source", new UTF8Encoding(false));
            f.Factory = new WebApplicationFactory<Program>(); f.Client = f.Factory.CreateClient(); f.Client.DefaultRequestHeaders.Add("X-JobFlow-Client", "local-ui");
            return f;
        }
        public async ValueTask DisposeAsync()
        {
            Client.Dispose(); await Factory.DisposeAsync(); await Db.DisposeAsync(); Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Environment.SetEnvironmentVariable("JOBFLOW_DATA_DIR", previous); Directory.Delete(root, true);
        }
    }
}
