using System.Text.Json;
using JobFlow.Application;
using JobFlow.Domain;
using JobFlow.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace JobFlow.IntegrationTests;

public class WorkflowControlTests
{
    [Fact]
    public async Task ApprovedOnlyWorkflowSendsReadyDraftFromAnotherWorkflowWithoutDiscoveryOrPreparation()
    {
        await using var f = await Fixture.CreateAsync();
        var oldRoot = Environment.GetEnvironmentVariable("JOBFLOW_DATA_DIR");
        var root = Path.Combine(Path.GetTempPath(), "jobflow-approved-" + Guid.NewGuid());
        Directory.CreateDirectory(Path.Combine(root, "resumes"));
        try
        {
            Environment.SetEnvironmentVariable("JOBFLOW_DATA_DIR", root);
            var source = new WorkflowDefinition
            {
                CandidateProfileId = f.Workflow.CandidateProfileId, JobPlatformId = f.Platforms[0].Id,
                ResumeVersionId = f.Workflow.ResumeVersionId
            };
            var version = await f.Db.ResumeVersions.SingleAsync();
            version.StorageKey = "approved.pdf";
            await File.WriteAllTextAsync(Path.Combine(root, "resumes", version.StorageKey), "%PDF-synthetic");
            var ready = SavedVacancy(f.Platforms[0], "approved");
            var unapproved = SavedVacancy(f.Platforms[0], "not-approved");
            var untouched = SavedVacancy(f.Platforms[0], "unprocessed");
            var app = new JobApplication
            {
                CandidateProfileId = source.CandidateProfileId, WorkflowDefinitionId = source.Id,
                VacancyId = ready.Id, ResumeVersionId = version.Id, CoverLetter = "Approved text", State = ApplicationState.ReadyToSubmit
            };
            var review = new JobApplication
            {
                CandidateProfileId = source.CandidateProfileId, WorkflowDefinitionId = source.Id,
                VacancyId = unapproved.Id, ResumeVersionId = version.Id, State = ApplicationState.ReadyForReview
            };
            var journal = new Integration { CandidateProfileId = source.CandidateProfileId, Kind = "google-sheets", JobPlatformId = f.Platforms[0].Id };
            f.Db.AddRange(source, ready, unapproved, untouched, app, review, journal,
                new AuditEvent { EntityId = journal.Id, Action = JournalApproval.NoPriorJournal });
            f.Workflow.VacancySource = "saved";
            f.Workflow.ApplicationStatusFilter = WorkflowDefinition.ApprovedStatusFilter;
            f.Workflow.AutoSubmit = true;
            f.Browser.Submit = async (application, beforeSubmit) =>
            {
                Assert.Equal(app.Id, application.Id);
                Assert.Equal("Approved text", application.CoverLetter);
                await beforeSubmit();
                return new BrowserResult(true, "Synthetic receipt");
            };
            await f.Db.SaveChangesAsync();

            var result = await f.RunAsync();
            Assert.Equal("Succeeded", result.Status);
            Assert.Equal(1, result.Checked);
            Assert.Equal(1, result.Submitted);
            Assert.Equal(0, result.Prepared);
            Assert.Empty(f.Browser.Discovered);
            Assert.Empty(f.Browser.Read);
            Assert.Empty(await f.Db.Set<VacancyEvaluation>().ToListAsync());
            Assert.Equal(2, await f.Db.Applications.CountAsync());
            Assert.Equal(ApplicationState.ReadyForReview, review.State);
            Assert.Equal(source.Id, app.WorkflowDefinitionId);
            Assert.Equal(ApplicationState.Submitted, app.State);
            Assert.Equal(app.Id, (await f.Db.Set<ApplicationAttempt>().SingleAsync()).JobApplicationId);
            Assert.Equal(0, (await f.RunAsync()).Submitted);
        }
        finally
        {
            Environment.SetEnvironmentVariable("JOBFLOW_DATA_DIR", oldRoot);
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task ApprovedQueueChecksAggregatorAliasBeforeAnyPreparationOrSubmission()
    {
        await using var f = await Fixture.CreateAsync();
        f.Workflow.VacancySource = "saved";
        f.Workflow.ApplicationStatusFilter = WorkflowDefinition.ApprovedStatusFilter;
        f.Workflow.AutoSubmit = true;
        var aggregator = SavedVacancy(f.Platforms[0], "aggregator");
        var destination = SavedVacancy(f.Platforms[1], "direct");
        destination.Url = "https://hh.ru/vacancy/123";
        JobApplication Ready(Vacancy v) => new()
        {
            CandidateProfileId = f.Workflow.CandidateProfileId, VacancyId = v.Id, WorkflowDefinitionId = f.Workflow.Id,
            ResumeVersionId = f.Workflow.ResumeVersionId, State = ApplicationState.ReadyToSubmit, CoverLetter = "Approved letter"
        };
        var first = Ready(aggregator);
        var second = Ready(destination);
        f.Db.AddRange(aggregator, destination, first, second, new AuditEvent
        {
            EntityId = first.Id, Action = ExternalApplicationDraft.PlatformReferralAction,
            Details = VacancyIdentity.CanonicalUrl("https://spb.hh.ru/vacancy/123?from=aggregator")
        });
        await f.Db.SaveChangesAsync();

        var run = await f.RunAsync();
        Assert.Equal("NeedsAttention", run.Status);
        Assert.Equal(2, run.NeedsAttention);
        Assert.Equal(0, f.Browser.ExternalPreparations);
        Assert.Empty(await f.Db.Set<ApplicationAttempt>().ToListAsync());
        Assert.All(await f.Db.Applications.ToListAsync(), app => Assert.Equal(ApplicationState.Blocked, app.State));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExternalFormsRequireHumanReviewRegardlessOfAutomaticSubmission(bool autoSubmit)
    {
        await using var f = await Fixture.CreateAsync();
        f.Workflow.AutoSubmit = autoSubmit;
        f.Workflow.VacancySource = "saved";
        f.Browser.ExpectedDiscoveries = 0;
        f.Browser.ExternalReview = true;
        f.Db.Add(SavedVacancy(f.Platforms[0], "external"));
        await f.Db.SaveChangesAsync();
        var run = await new JobSearchWorkflow(f.Db, new Evaluator("Proceed"), f.Browser, monitorWithinSearch: false).RunAsync(f.Workflow.Id, default);
        Assert.Equal(1, f.Browser.ExternalPreparations);
        Assert.Equal("NeedsAttention", run.Status);
        Assert.Equal(1, run.Prepared);
        Assert.Equal(1, run.NeedsAttention);
        Assert.Equal(0, run.Errors);
        Assert.Equal(0, run.Submitted);
        Assert.Equal(ApplicationState.ReadyForReview, (await f.Db.Applications.SingleAsync()).State);
        Assert.Empty(await f.Db.Set<ApplicationAttempt>().ToListAsync());
    }
    [Fact]
    public async Task SavedQueueSkipsSearchAndContinuesWithNextVacancyOnLaterRun()
    {
        await using var f = await Fixture.CreateAsync();
        f.Workflow.VacancySource = "saved";
        f.Workflow.MaxVacancyChecksPerRun = 1;
        f.Browser.ExpectedDiscoveries = 0;
        var first = SavedVacancy(f.Platforms[0], "first");
        var second = SavedVacancy(f.Platforms[0], "second");
        first.CreatedAt = DateTime.UtcNow.AddDays(-2);
        second.CreatedAt = DateTime.UtcNow.AddDays(-1);
        f.Db.AddRange(first, second);
        await f.Db.SaveChangesAsync();

        var run1 = await f.RunAsync();
        var run2 = await f.RunAsync();

        Assert.Empty(f.Browser.Discovered);
        Assert.Equal(new[] { first.Id, second.Id }, f.Browser.Read.Select(x => x.Id));
        Assert.All(new[] { run1, run2 }, run => { Assert.Equal(0, run.Discovered); Assert.Equal(1, run.Checked); Assert.Equal(1, run.Prepared); });
        Assert.Equal(2, await f.Db.Set<VacancyEvaluation>().CountAsync());
        Assert.All(await f.Db.Applications.ToListAsync(), x => Assert.Equal(ApplicationState.ReadyForReview, x.State));
        Assert.Contains(await f.Db.Audit.ToListAsync(), x => x.Action == "WorkflowSavedQueueLoaded");
        Assert.True(f.Browser.Closed);
        Assert.Equal("saved", (await f.Db.Set<WorkflowDefinition>().AsNoTracking().SingleAsync()).VacancySource);
    }

    [Fact]
    public async Task SavedQueueExcludesProcessedHistoryArchivedAndUnselectedPlatforms()
    {
        await using var f = await Fixture.CreateAsync();
        f.Workflow.VacancySource = "saved";
        f.Workflow.EvaluationResultFilter = "Unevaluated";
        f.Workflow.AutoSubmit = true;
        f.Browser.ExpectedDiscoveries = 0;
        var pending = SavedVacancy(f.Platforms[0], "pending");
        var archived = SavedVacancy(f.Platforms[0], "archived"); archived.IsArchived = true;
        var assessed = SavedVacancy(f.Platforms[0], "assessed");
        var historical = SavedVacancy(f.Platforms[0], "historical");
        var other = new JobPlatform { Name = "Unselected", Url = "https://other.example/" };
        var outside = SavedVacancy(other, "outside");
        f.Db.AddRange(pending, archived, assessed, historical, other, outside);
        f.Db.Add(new VacancyEvaluation { CandidateProfileId = f.Workflow.CandidateProfileId, VacancyId = assessed.Id });
        var integration = new Integration { CandidateProfileId = f.Workflow.CandidateProfileId };
        f.Db.Add(integration);
        f.Db.Add(new HistoricalApplication { IntegrationId = integration.Id, CandidateProfileId = f.Workflow.CandidateProfileId, VacancyId = historical.Id });
        foreach (var state in new[] { ApplicationState.Rejected, ApplicationState.ReadyForReview, ApplicationState.ReadyToSubmit,
                     ApplicationState.Submitted, ApplicationState.SubmissionUnknown, ApplicationState.EmployerResponded, ApplicationState.Closed })
        {
            var vacancy = SavedVacancy(f.Platforms[0], state.ToString());
            f.Db.Add(vacancy);
            f.Db.Add(new JobApplication { CandidateProfileId = f.Workflow.CandidateProfileId, VacancyId = vacancy.Id,
                ResumeVersionId = f.Workflow.ResumeVersionId, WorkflowDefinitionId = f.Workflow.Id, State = state });
        }
        await f.Db.SaveChangesAsync();

        var run = await f.RunAsync();

        Assert.Empty(f.Browser.Discovered);
        Assert.Equal(pending.Id, Assert.Single(f.Browser.Read).Id);
        Assert.Equal(1, run.Checked);
        Assert.Equal(1, run.Prepared);
        Assert.Equal(0, run.Submitted);
    }

    [Fact]
    public async Task SavedQueuePrioritizesNewVacanciesAndRechecksFailedPages()
    {
        await using var f = await Fixture.CreateAsync();
        f.Workflow.VacancySource = "saved";
        f.Workflow.MaxVacancyChecksPerRun = 3;
        f.Browser.ExpectedDiscoveries = 0;
        f.Browser.FailFirstRead = true;
        var blocked = SavedVacancy(f.Platforms[0], "retry"); blocked.CreatedAt = DateTime.UtcNow.AddDays(-4);
        var first = SavedVacancy(f.Platforms[0], "unreadable"); first.CreatedAt = DateTime.UtcNow.AddDays(-2);
        var next = SavedVacancy(f.Platforms[0], "next"); next.CreatedAt = DateTime.UtcNow.AddDays(-1);
        f.Db.AddRange(blocked, first, next);
        f.Db.Add(new JobApplication { CandidateProfileId = f.Workflow.CandidateProfileId, VacancyId = blocked.Id,
            ResumeVersionId = f.Workflow.ResumeVersionId, WorkflowDefinitionId = f.Workflow.Id, State = ApplicationState.Blocked });
        await f.Db.SaveChangesAsync();

        var run = await f.RunAsync();

        Assert.Equal(new[] { first.Id, next.Id, blocked.Id }, f.Browser.Read.Select(x => x.Id));
        Assert.Equal(3, run.Checked);
        Assert.Equal(2, run.Prepared);
        Assert.Equal(1, run.NeedsAttention);
        Assert.Equal(ApplicationState.Blocked, (await f.Db.Applications.SingleAsync(x => x.VacancyId == first.Id)).State);
        Assert.Equal(ApplicationState.ReadyForReview, (await f.Db.Applications.SingleAsync(x => x.VacancyId == blocked.Id)).State);
        Assert.True(f.Browser.Closed);
    }

    [Fact]
    public async Task SavedQueueKeepsFreshnessLimitAndSkipsKnownOldVacancy()
    {
        await using var f = await Fixture.CreateAsync();
        f.Workflow.VacancySource = "saved";
        f.Workflow.FreshnessDays = 3;
        f.Browser.ExpectedDiscoveries = 0;
        var old = SavedVacancy(f.Platforms[0], "old"); old.PublishedAt = DateTime.UtcNow.AddDays(-5);
        f.Db.Add(old); await f.Db.SaveChangesAsync();
        var run = await f.RunAsync();
        Assert.Empty(f.Browser.Read);
        Assert.Equal(0, run.Checked);
        Assert.Empty(await f.Db.Applications.ToListAsync());
        Assert.Contains(await f.Db.Audit.ToListAsync(), x => x.Action == "WorkflowVacancySkipped" && x.Details.Contains("publication date"));
    }

    [Theory]
    [InlineData("unknown", "browser")]
    [InlineData("saved", "prepare")]
    public async Task InvalidVacancySourceIsRejected(string source, string mode)
    {
        await using var f = await Fixture.CreateAsync();
        f.Workflow.VacancySource = source;
        f.Workflow.ExecutionMode = mode;
        await Assert.ThrowsAsync<ArgumentException>(() => new JobSearchWorkflow(f.Db, new Evaluator(), f.Browser).ValidateAsync(f.Workflow, default));
    }

    private static Vacancy SavedVacancy(JobPlatform platform, string key) => new()
    {
        JobPlatformId = platform.Id, ExternalId = key, Title = key, Url = platform.Url + "jobs/" + key
    };

    [Fact]
    public async Task CombinedQueueIncludesSavedAndNewLinksOnceAndSkipsFinishedApplicationsOnNextRun()
    {
        await using var f = await Fixture.CreateAsync();
        f.Workflow.VacancySource = "search-and-saved";
        f.Workflow.MaxVacancyChecksPerRun = 20;
        f.Workflow.MaxApplicationsPerRun = 10;
        var cached = SavedVacancy(f.Platforms[0], "cached");
        var overlap = SavedVacancy(f.Platforms[0], "0");
        f.Db.AddRange(cached, overlap); await f.Db.SaveChangesAsync();

        var first = await f.RunAsync();

        Assert.Equal(6, first.Discovered);
        Assert.Equal(7, first.Checked);
        Assert.Equal(7, first.Prepared);
        Assert.Equal(7, f.Browser.Read.Select(x => x.Id).Distinct().Count());
        Assert.Contains(f.Browser.Read, x => x.Id == cached.Id);
        Assert.Single(f.Browser.Read, x => x.Id == overlap.Id);
        var second = await f.RunAsync();
        Assert.Equal(6, second.Discovered);
        Assert.Equal(0, second.Checked);
        Assert.Equal(0, second.Prepared);
    }

    [Theory]
    [InlineData("no-application", "Unevaluated", "fresh")]
    [InlineData("Blocked", "Proceed", "blocked")]
    [InlineData("no-application", "NeedsReview", "latest")]
    [InlineData("no-application", "Reject", "rejected-evaluation")]
    public async Task QueueFiltersUseCurrentCandidateAndLatestAssessment(string status, string result, string expected)
    {
        await using var f = await Fixture.CreateAsync();
        f.Workflow.VacancySource = "saved";
        f.Workflow.ApplicationStatusFilter = status;
        f.Workflow.EvaluationResultFilter = result;
        f.Browser.ExpectedDiscoveries = 0;
        var fresh = SavedVacancy(f.Platforms[0], "fresh");
        var blocked = SavedVacancy(f.Platforms[0], "blocked");
        var latest = SavedVacancy(f.Platforms[0], "latest");
        var rejected = SavedVacancy(f.Platforms[0], "rejected-evaluation");
        f.Db.AddRange(fresh, blocked, latest, rejected);
        f.Db.Add(new JobApplication { CandidateProfileId = f.Workflow.CandidateProfileId, VacancyId = blocked.Id,
            ResumeVersionId = f.Workflow.ResumeVersionId, WorkflowDefinitionId = f.Workflow.Id, State = ApplicationState.Blocked });
        VacancyEvaluation Assessment(Vacancy vacancy, string decision, DateTime? created = null) => new()
        {
            CandidateProfileId = f.Workflow.CandidateProfileId, VacancyId = vacancy.Id,
            ResultJson = JsonSerializer.Serialize(new { decision }), CreatedAt = created ?? DateTime.UtcNow
        };
        f.Db.AddRange(Assessment(blocked, "Proceed"), Assessment(latest, "Proceed", DateTime.UtcNow.AddDays(-2)),
            Assessment(latest, "NeedsReview", DateTime.UtcNow.AddDays(-1)), Assessment(rejected, "Reject"));
        var other = new CandidateProfile { UserId = (await f.Db.Profiles.SingleAsync()).UserId };
        f.Db.Add(other);
        f.Db.Add(new VacancyEvaluation { CandidateProfileId = other.Id, VacancyId = fresh.Id, ResultJson = "{\"decision\":\"Proceed\"}" });
        await f.Db.SaveChangesAsync();

        var run = await f.RunAsync();

        Assert.Equal(expected, Assert.Single(f.Browser.Read).ExternalId);
        Assert.Equal(1, run.Checked);
        Assert.Equal(1, run.Prepared);
        Assert.Empty(f.Browser.Discovered);
    }

    [Theory]
    [InlineData("Submitted", "all")]
    [InlineData("pending", "invalid")]
    public async Task InvalidQueueFiltersAreRejected(string status, string result)
    {
        await using var f = await Fixture.CreateAsync();
        f.Workflow.ApplicationStatusFilter = status;
        f.Workflow.EvaluationResultFilter = result;
        await Assert.ThrowsAsync<ArgumentException>(() => new JobSearchWorkflow(f.Db, new Evaluator(), f.Browser).ValidateAsync(f.Workflow, default));
    }

    [Fact]
    public async Task SavedDraftRechecksItsOriginalPageBeforeResumingPreparation()
    {
        await using var f = await Fixture.CreateAsync();
        f.Workflow.VacancySource = "saved";
        f.Browser.ExpectedDiscoveries = 0;
        f.Browser.FailFirstRead = true;
        var vacancy = SavedVacancy(f.Platforms[0], "saved-draft");
        var application = new JobApplication { CandidateProfileId = f.Workflow.CandidateProfileId, VacancyId = vacancy.Id,
            ResumeVersionId = f.Workflow.ResumeVersionId, WorkflowDefinitionId = f.Workflow.Id, State = ApplicationState.Drafting };
        f.Db.AddRange(vacancy, application, new VacancyEvaluation { CandidateProfileId = f.Workflow.CandidateProfileId,
            VacancyId = vacancy.Id, ResultJson = "{\"decision\":\"NeedsReview\"}" });
        await f.Db.SaveChangesAsync();

        var run = await f.RunAsync();

        Assert.Equal(vacancy.Id, Assert.Single(f.Browser.Read).Id);
        Assert.Equal(0, run.Prepared);
        Assert.Equal(1, run.NeedsAttention);
        Assert.Equal(ApplicationState.Blocked, application.State);
        Assert.True(f.Browser.Closed);
    }

    [Theory]
    [InlineData(7200)]
    [InlineData(21600)]
    public async Task LongRunKeepsTaskAndWorkflowOwnershipThroughTimeout(int seconds)
    {
        await using var f = await Fixture.CreateAsync();
        var task = new ScheduledTask { CandidateProfileId = f.Workflow.CandidateProfileId, WorkflowDefinitionId = f.Workflow.Id,
            Kind = "Workflow", RunRequested = true, TimeoutSeconds = seconds };
        f.Db.Add(task); await f.Db.SaveChangesAsync();
        f.Browser.OnBegin = async () =>
        {
            var minimum = DateTime.UtcNow.AddSeconds(seconds + 270);
            Assert.True((await f.Db.Tasks.AsNoTracking().SingleAsync()).LeaseUntil > minimum);
            Assert.True((await f.Db.Set<WorkflowDefinition>().AsNoTracking().SingleAsync()).LeaseUntil > minimum);
        };
        Assert.True(await new TaskProcessor(f.Db, new Evaluator(), "search", f.Browser).RunOneAsync(default));
        Assert.True(f.Browser.Closed);
        Assert.Null((await f.Db.Tasks.AsNoTracking().SingleAsync()).LeaseUntil);
        Assert.Null((await f.Db.Set<WorkflowDefinition>().AsNoTracking().SingleAsync()).LeaseUntil);
    }

    [Fact]
    public async Task FailedChecksDoNotConsumePreparationBudgetAndEverySourceIsSearched()
    {
        await using var f = await Fixture.CreateAsync();
        f.Workflow.MaxApplicationsPerRun = 1;
        f.Workflow.MaxVacancyChecksPerRun = 3;
        f.Browser.FailFirstRead = true;
        await f.Db.SaveChangesAsync();
        var run = await f.RunAsync();
        Assert.Equal(2, f.Browser.Discovered.Count);
        Assert.Equal(f.Platforms.Select(x => x.Id), f.Browser.Discovered);
        Assert.Equal(6, run.Discovered);
        Assert.Equal(2, run.Checked);
        Assert.Equal(1, run.Prepared);
        Assert.Equal(1, run.NeedsAttention);
        Assert.Equal("NeedsAttention", run.Status);
        Assert.True(f.Browser.Closed);
        Assert.Equal(2, f.Browser.Read.Count);
        Assert.NotEqual(f.Browser.Read[0].JobPlatformId, f.Browser.Read[1].JobPlatformId);
        Assert.Equal("Job 0", f.Browser.Read[0].Title); // First observed result, not last inserted row.
    }

    [Fact]
    public async Task ConfiguredCheckBudgetStopsProcessingWithoutSkippingSourceDiscovery()
    {
        await using var f = await Fixture.CreateAsync();
        f.Workflow.MaxApplicationsPerRun = 5;
        f.Workflow.MaxVacancyChecksPerRun = 1;
        await f.Db.SaveChangesAsync();
        var run = await f.RunAsync();
        Assert.Equal(2, f.Browser.Discovered.Count);
        Assert.Equal(1, run.Checked);
        Assert.Equal(1, run.Prepared);
        Assert.True(f.Browser.Closed);
    }

    [Theory]
    [InlineData(0, 10)]
    [InlineData(201, 10)]
    [InlineData(50, 0)]
    [InlineData(50, 501)]
    public async Task InvalidLimitsAreRejected(int discovery, int checks)
    {
        await using var f = await Fixture.CreateAsync();
        f.Workflow.MaxVacanciesPerPlatform = discovery;
        f.Workflow.MaxVacancyChecksPerRun = checks;
        await Assert.ThrowsAsync<ArgumentException>(() => new JobSearchWorkflow(f.Db, new Evaluator(), f.Browser).ValidateAsync(f.Workflow, default));
    }

    [Fact]
    public async Task AttentionResultHasMatchingSchedulerStatusAndAttentionNotificationWithoutRetry()
    {
        await using var f = await Fixture.CreateAsync();
        f.Workflow.MaxVacancyChecksPerRun = 1;
        f.Browser.FailFirstRead = true;
        var task = new ScheduledTask { CandidateProfileId = f.Workflow.CandidateProfileId, WorkflowDefinitionId = f.Workflow.Id,
            Kind = "Workflow", RunRequested = true, NextRunAt = DateTime.UtcNow.AddMinutes(-1) };
        f.Db.Add(task); await f.Db.SaveChangesAsync();
        Assert.True(await new TaskProcessor(f.Db, new Evaluator(), "search", f.Browser).RunOneAsync(default));
        Assert.Equal("NeedsAttention", (await f.Db.Runs.SingleAsync()).Status);
        Assert.Contains(await f.Db.Set<Notification>().ToListAsync(), x => x.RelatedId == f.Db.Runs.Local.Single().Id && x.Kind == "Error");
        f.Db.ChangeTracker.Clear();
        var stored = await f.Db.Tasks.SingleAsync();
        Assert.False(stored.RunRequested);
        Assert.Equal(0, stored.FailureCount);
        Assert.True(f.Browser.Closed);
    }

    [Fact]
    public async Task TargetedPreparationIgnoresDiscoveryAndNeverSubmitsEvenWhenAutomaticSubmissionIsEnabled()
    {
        await using var f = await Fixture.CreateAsync();
        f.Workflow.AutoSubmit = true; f.Workflow.VacancySource = "search-and-saved";
        f.Browser.ExpectedDiscoveries = 0;
        var first = SavedVacancy(f.Platforms[0], "not-selected");
        var target = SavedVacancy(f.Platforms[1], "selected");
        f.Db.AddRange(first, target); await f.Db.SaveChangesAsync();
        var run = await new JobSearchWorkflow(f.Db, new Evaluator("Proceed"), f.Browser).RunAsync(f.Workflow.Id, default, target.Id, true);
        Assert.Equal(1, run.Prepared); Assert.Equal(0, run.Submitted); Assert.Equal(0, run.NeedsAttention);
        Assert.Equal(target.Id, Assert.Single(f.Browser.Read).Id); Assert.Empty(f.Browser.Discovered);
        Assert.Equal(ApplicationState.ReadyForReview, (await f.Db.Applications.SingleAsync()).State);
        Assert.Equal(target.Id, run.TargetVacancyId); Assert.True(run.PreparationOnly); Assert.True(f.Browser.Closed);
        Assert.True((await f.Db.Set<WorkflowDefinition>().AsNoTracking().SingleAsync()).AutoSubmit);
        Assert.Equal("search-and-saved", (await f.Db.Set<WorkflowDefinition>().AsNoTracking().SingleAsync()).VacancySource);
    }

    [Theory]
    [InlineData("closed")]
    [InlineData("stale")]
    public async Task UnavailableVacancyIsSkippedWithoutErrorOrReviewNotification(string code)
    {
        await using var f = await Fixture.CreateAsync();
        f.Workflow.VacancySource="saved"; f.Browser.ExpectedDiscoveries=0;
        f.Browser.ReadException=new VacancyUnavailableException(code,"Вакансия недоступна");
        f.Db.Add(SavedVacancy(f.Platforms[0], "unavailable")); await f.Db.SaveChangesAsync();
        var run=await f.RunAsync();
        Assert.Equal("Succeeded",run.Status);Assert.Equal(1,run.Skipped);Assert.Equal(0,run.Errors);Assert.Equal(0,run.NeedsAttention);Assert.Empty(run.Error);
        Assert.Equal(code=="closed"?ApplicationState.Closed:ApplicationState.Skipped,(await f.Db.Applications.SingleAsync()).State);
        Assert.Equal("Information",(await f.Db.Set<Notification>().SingleAsync()).Kind);Assert.True(f.Browser.Closed);
    }

    [Fact]
    public async Task RequestForHumanClarificationDoesNotBecomeTechnicalError()
    {
        await using var f = await Fixture.CreateAsync();
        f.Workflow.VacancySource="saved";f.Browser.ExpectedDiscoveries=0;
        f.Browser.ReadException=new HumanReviewRequiredException("Нужно подтвердить дату");
        f.Db.Add(SavedVacancy(f.Platforms[0], "unknown"));await f.Db.SaveChangesAsync();
        var run=await f.RunAsync();
        Assert.Equal("NeedsAttention",run.Status);Assert.Equal(1,run.NeedsAttention);Assert.Equal(0,run.Errors);Assert.Empty(run.Error);Assert.NotEmpty(run.AttentionDetails);
        Assert.Equal("Review",(await f.Db.Set<Notification>().SingleAsync()).Kind);
    }

    [Fact]
    public async Task PreparationQueueRejectsDuplicateAndSearchWorkerProcessesOnlyThatVacancy()
    {
        await using var f=await Fixture.CreateAsync();f.Browser.ExpectedDiscoveries=0;f.Workflow.AutoSubmit=true;
        var vacancy=SavedVacancy(f.Platforms[0],"queued");f.Db.Add(vacancy);await f.Db.SaveChangesAsync();
        var queue=new PreparationQueue(f.Db);var task=await queue.EnqueueAsync(vacancy.Id,f.Workflow.Id,default);
        Assert.Equal("PrepareApplication",task.Kind);Assert.False(task.Enabled);Assert.Equal(0,task.MaxRetries);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>queue.EnqueueAsync(vacancy.Id,f.Workflow.Id,default));
        Assert.True(await new TaskProcessor(f.Db,new Evaluator("Proceed"),"search",f.Browser).RunOneAsync(default));
        Assert.Equal("Succeeded",(await f.Db.Runs.SingleAsync()).Status);
        Assert.Equal(ApplicationState.ReadyForReview,(await f.Db.Applications.SingleAsync()).State);
        Assert.Empty(f.Browser.Discovered);Assert.Single(f.Browser.Read);
        f.Db.ChangeTracker.Clear();Assert.False((await f.Db.Tasks.SingleAsync()).RunRequested);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>new PreparationQueue(f.Db).EnqueueAsync(vacancy.Id,f.Workflow.Id,default));
    }

    [Fact]
    public async Task DifferentVacancyCanQueueBehindActivePreparationWithItsOwnAgeOverride()
    {
        await using var f = await Fixture.CreateAsync();
        f.Browser.ExpectedDiscoveries = 0;
        f.Workflow.FreshnessDays = 3;
        var first = SavedVacancy(f.Platforms[0], "first");
        var second = SavedVacancy(f.Platforms[0], "second");
        second.PublishedAt = DateTime.UtcNow.AddDays(-7);
        f.Db.AddRange(first, second);
        await f.Db.SaveChangesAsync();
        var queue = new PreparationQueue(f.Db);
        var current = await queue.EnqueueAsync(first.Id, f.Workflow.Id, default);
        current.RunRequested = false;
        current.LeaseUntil = DateTime.UtcNow.AddMinutes(2);
        await f.Db.SaveChangesAsync();

        var next = await queue.EnqueueAsync(second.Id, f.Workflow.Id, default, outsideAgeLimit: true);
        Assert.True(next.RunRequested);
        Assert.True(next.PrepareOutsideAgeLimit);
        var worker = new TaskProcessor(f.Db, new Evaluator("Proceed"), "search", f.Browser);
        Assert.False(await worker.RunOneAsync(default));

        current.LeaseUntil = null;
        await f.Db.SaveChangesAsync();
        Assert.True(await worker.RunOneAsync(default));
        Assert.Equal(second.Id, Assert.Single(f.Browser.Read).Id);
        Assert.True(f.Browser.IgnoredPublicationAgeOnRead);
        Assert.Equal(ApplicationState.ReadyForReview, (await f.Db.Applications.SingleAsync()).State);
        Assert.False(await worker.RunOneAsync(default));
    }

    [Fact]
    public async Task SavedAssessmentSeparatesCandidateQuestionsFromReviewNotes()
    {
        await using var f = await Fixture.CreateAsync();
        f.Workflow.VacancySource = "saved";
        f.Browser.ExpectedDiscoveries = 0;
        var vacancy = SavedVacancy(f.Platforms[0], "candidate-question");
        var profile = await f.Db.Profiles.SingleAsync();
        profile.Rules = "Already supplied candidate preferences";
        f.Db.Add(vacancy);
        await f.Db.SaveChangesAsync();
        const string question = "Can you work until 20:00 CET?";
        var evaluator = new Evaluator(clarifications: [question, "Employer salary is not published.", question]);

        var run = await new JobSearchWorkflow(f.Db, evaluator, f.Browser, monitorWithinSearch: false).RunAsync(f.Workflow.Id, default);

        Assert.Equal(1, run.Prepared);
        var evaluation = await f.Db.Set<VacancyEvaluation>().SingleAsync();
        using var result = JsonDocument.Parse(evaluation.ResultJson);
        Assert.Equal(question, Assert.Single(result.RootElement.GetProperty("candidateQuestions").EnumerateArray()).GetString());
        Assert.DoesNotContain("Already supplied candidate preferences", evaluation.ResultJson);
        Assert.DoesNotContain("Verify experience, languages", evaluation.ResultJson);
    }

    [Fact]
    public async Task LegacyApplicationNotificationLinksToExactVacancyAssessmentAndApplication()
    {
        await using var f=await Fixture.CreateAsync();var vacancy=SavedVacancy(f.Platforms[0],"linked");
        var application=new JobApplication{CandidateProfileId=f.Workflow.CandidateProfileId,WorkflowDefinitionId=f.Workflow.Id,VacancyId=vacancy.Id,ResumeVersionId=f.Workflow.ResumeVersionId,State=ApplicationState.Blocked};
        var evaluation=new VacancyEvaluation{CandidateProfileId=f.Workflow.CandidateProfileId,VacancyId=vacancy.Id};
        var notification=new Notification{CandidateProfileId=f.Workflow.CandidateProfileId,Kind="Attention",RelatedId=application.Id,Url=vacancy.Url,Message="Resume verification requires attention"};
        f.Db.AddRange(vacancy,application,evaluation,notification);await f.Db.SaveChangesAsync();
        var result=JsonSerializer.SerializeToElement(await NotificationQueries.ReadAsync(f.Db,default),new JsonSerializerOptions(JsonSerializerDefaults.Web))[0];
        Assert.Equal("Review",result.GetProperty("kind").GetString());Assert.Equal(vacancy.Id,result.GetProperty("vacancyId").GetGuid());
        Assert.Equal("Документы не прошли проверку",result.GetProperty("title").GetString());
        Assert.Equal(evaluation.Id,result.GetProperty("evaluationId").GetGuid());Assert.Equal(application.Id,result.GetProperty("applicationId").GetGuid());
        Assert.DoesNotContain("Resume verification",result.GetProperty("message").GetString());Assert.Contains("Resume verification",result.GetProperty("rawMessage").GetString());
    }

    [Fact]
    public async Task StaleVacancyCanBePreparedAfterExplicitlyIncreasingFreshnessLimit()
    {
        await using var f=await Fixture.CreateAsync();f.Workflow.VacancySource="saved";f.Workflow.FreshnessDays=3;f.Browser.ExpectedDiscoveries=0;
        var vacancy=SavedVacancy(f.Platforms[0],"age-limit");f.Db.Add(vacancy);await f.Db.SaveChangesAsync();
        f.Browser.ReadException=new VacancyUnavailableException("stale","Старше трёх дней");
        var first=await f.RunAsync();Assert.Equal(1,first.Skipped);Assert.Equal(ApplicationState.Skipped,(await f.Db.Applications.SingleAsync()).State);
        vacancy.PublishedAt=DateTime.UtcNow.AddDays(-4);f.Workflow.FreshnessDays=7;f.Browser.ReadException=null;await f.Db.SaveChangesAsync();
        var second=await f.RunAsync();Assert.Equal(1,second.Prepared);Assert.Equal(0,second.Errors);Assert.Equal(ApplicationState.ReadyForReview,(await f.Db.Applications.SingleAsync()).State);
        Assert.Single(await f.Db.Applications.ToListAsync());Assert.Empty(f.Browser.Discovered);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProviderOutageStopsQueueWithoutBlockingVacanciesAndRetriesOnlyTransientFailures(bool retryable)
    {
        await using var f=await Fixture.CreateAsync();f.Workflow.VacancySource="saved";f.Browser.ExpectedDiscoveries=0;
        f.Browser.ReadException=new ModelProviderException("synthetic","Provider unavailable",retryable);
        f.Db.AddRange(SavedVacancy(f.Platforms[0],"first"),SavedVacancy(f.Platforms[0],"next"));
        f.Db.Add(new ScheduledTask{CandidateProfileId=f.Workflow.CandidateProfileId,WorkflowDefinitionId=f.Workflow.Id,Kind="Workflow",RunRequested=true,Enabled=false,MaxRetries=2,NextRunAt=DateTime.UtcNow});
        await f.Db.SaveChangesAsync();
        Assert.True(await new TaskProcessor(f.Db,new Evaluator(),"search",f.Browser).RunOneAsync(default));
        f.Db.ChangeTracker.Clear();
        var run=await f.Db.Set<WorkflowRun>().SingleAsync();Assert.Equal("Failed",run.Status);Assert.Equal(1,run.Errors);Assert.Equal(0,run.NeedsAttention);
        Assert.Single(f.Browser.Read);Assert.True(f.Browser.Closed);
        Assert.Equal(ApplicationState.Discovered,(await f.Db.Applications.SingleAsync()).State);
        Assert.Equal(retryable,(await f.Db.Tasks.SingleAsync()).RunRequested);
        Assert.Single(await f.Db.Set<Notification>().ToListAsync());
        Assert.Contains(await f.Db.Audit.ToListAsync(),x=>x.Action=="WorkflowProviderStopped");
    }

    [Fact]
    public async Task DiscoveryProviderFailureStopsBeforeOtherPlatformsAndSavedQueue()
    {
        await using var f=await Fixture.CreateAsync();f.Workflow.VacancySource="search-and-saved";
        f.Browser.DiscoveryException=new ModelProviderException("authentication","Sign in required");
        f.Db.Add(SavedVacancy(f.Platforms[0],"saved"));await f.Db.SaveChangesAsync();
        await Assert.ThrowsAsync<ModelProviderException>(()=>f.RunAsync());
        Assert.Single(f.Browser.Discovered);Assert.Empty(f.Browser.Read);Assert.True(f.Browser.Closed);
        Assert.Empty(await f.Db.Applications.ToListAsync());Assert.Empty(await f.Db.Set<Notification>().ToListAsync());
        Assert.Equal(1,(await f.Db.Set<WorkflowRun>().SingleAsync()).Errors);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProviderFailureKeepsReadyDraftOrProtectsUnconfirmedSubmission(bool afterClick)
    {
        await using var f=await Fixture.CreateAsync();
        var previousRoot=Environment.GetEnvironmentVariable("JOBFLOW_DATA_DIR");
        var root=Path.Combine(Path.GetTempPath(),"jobflow-provider-submit-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root,"resumes"));
        try
        {
            Environment.SetEnvironmentVariable("JOBFLOW_DATA_DIR",root);
            var version=await f.Db.ResumeVersions.SingleAsync();version.StorageKey="synthetic.pdf";
            await File.WriteAllTextAsync(Path.Combine(root,"resumes",version.StorageKey),"%PDF-synthetic");
            var vacancy=SavedVacancy(f.Platforms[0],"provider-submit");
            var application=new JobApplication{CandidateProfileId=f.Workflow.CandidateProfileId,WorkflowDefinitionId=f.Workflow.Id,VacancyId=vacancy.Id,ResumeVersionId=version.Id,State=ApplicationState.ReadyToSubmit};
            f.Workflow.AutoSubmit=true;f.Db.AddRange(vacancy,application);await f.Db.SaveChangesAsync();
            var candidate=await f.Db.Profiles.SingleAsync();
            await Assert.ThrowsAsync<ModelProviderException>(()=>new BrowserSubmissionService(f.Db,new FailingSubmitBrowser(afterClick)).SubmitAsync(f.Workflow,application,f.Platforms[0],candidate,vacancy,default));
            Assert.Equal(afterClick?ApplicationState.SubmissionUnknown:ApplicationState.ReadyToSubmit,application.State);
            Assert.Equal(afterClick?1:0,await f.Db.Set<ApplicationAttempt>().CountAsync());
            if(afterClick)Assert.Equal("SubmissionUnknown",(await f.Db.Set<ApplicationAttempt>().SingleAsync()).Outcome);
        }
        finally{Environment.SetEnvironmentVariable("JOBFLOW_DATA_DIR",previousRoot);Directory.Delete(root,true);}
    }

    private sealed class FailingSubmitBrowser(bool afterClick):IBrowserWorkflowDriver
    {
        public Task<IAsyncDisposable> BeginRunAsync(WorkflowDefinition workflow,CancellationToken ct)=>throw new NotSupportedException();
        public Task<IReadOnlyList<Vacancy>> DiscoverAsync(WorkflowDefinition workflow,JobPlatform platform,CandidateProfile candidate,CancellationToken ct)=>throw new NotSupportedException();
        public Task ReadVacancyAsync(WorkflowDefinition workflow,JobPlatform platform,Vacancy vacancy,CancellationToken ct)=>throw new NotSupportedException();
        public async Task<BrowserResult> ApplyAsync(WorkflowDefinition workflow,JobPlatform platform,CandidateProfile candidate,Vacancy vacancy,JobApplication application,string resumePath,Func<Task> beforeSubmit,CancellationToken ct)
        {if(afterClick)await beforeSubmit();throw new ModelProviderException("authentication","Login expired");}
    }

    [Theory]
    [InlineData(ApplicationState.ReadyForReview)]
    [InlineData(ApplicationState.ReadyToSubmit)]
    [InlineData(ApplicationState.Rejected)]
    public async Task ClarificationQueuesOneUnsentApplicationAndInvalidatesApproval(ApplicationState state)
    {
        await using var f = await Fixture.CreateAsync();
        var vacancy = SavedVacancy(f.Platforms[0], "clarification");
        var application = new JobApplication { CandidateProfileId = f.Workflow.CandidateProfileId, WorkflowDefinitionId = f.Workflow.Id,
            VacancyId = vacancy.Id, ResumeVersionId = f.Workflow.ResumeVersionId, State = state, CoverLetter = "Previous letter", Revision = 5 };
        var other = SavedVacancy(f.Platforms[0], "already-running");
        f.Workflow.LeaseUntil = DateTime.UtcNow.AddMinutes(5);
        var running = new WorkflowRun
        {
            WorkflowDefinitionId = f.Workflow.Id, CandidateProfileId = f.Workflow.CandidateProfileId,
            TargetVacancyId = other.Id, PreparationOnly = true, Status = "Running"
        };
        f.Db.AddRange(vacancy, application, other, running); await f.Db.SaveChangesAsync();
        var queue = new PreparationQueue(f.Db);
        var task = await queue.EnqueueClarificationAsync(application.Id, "  Confirmed working hours  ", 5, default);
        Assert.True(task.RunRequested); Assert.Equal("PrepareApplication", task.Kind); Assert.Equal(vacancy.Id, task.VacancyId);
        Assert.Equal(ApplicationState.Evaluating, application.State); Assert.Equal(6, application.Revision);
        Assert.Equal("Previous letter", application.CoverLetter);
        Assert.Equal("Confirmed working hours", Assert.Single(await ApplicationClarifications.ReadAsync(f.Db, application.Id, default)).Text);
        Assert.False(await new TaskProcessor(f.Db, new Evaluator(), "search", f.Browser).RunOneAsync(default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => queue.EnqueueClarificationAsync(application.Id, "Duplicate", 6, default));
        Assert.Single(await f.Db.Tasks.ToListAsync()); Assert.Single(await ApplicationClarifications.ReadAsync(f.Db, application.Id, default));
    }

    [Fact]
    public async Task ClarificationRejectsStaleRevisionAndAnyExistingSubmissionAttempt()
    {
        await using var f = await Fixture.CreateAsync(); var vacancy = SavedVacancy(f.Platforms[0], "protected");
        var application = new JobApplication { CandidateProfileId = f.Workflow.CandidateProfileId, WorkflowDefinitionId = f.Workflow.Id,
            VacancyId = vacancy.Id, ResumeVersionId = f.Workflow.ResumeVersionId, State = ApplicationState.Blocked, Revision = 2 };
        f.Db.AddRange(vacancy, application); await f.Db.SaveChangesAsync(); var queue = new PreparationQueue(f.Db);
        await Assert.ThrowsAsync<InvalidOperationException>(() => queue.EnqueueClarificationAsync(application.Id, "New facts", 1, default));
        f.Db.Add(new ApplicationAttempt { JobApplicationId = application.Id, Outcome = "SubmissionUnknown" }); await f.Db.SaveChangesAsync();
        await Assert.ThrowsAsync<ArgumentException>(() => queue.EnqueueClarificationAsync(application.Id, "New facts", 2, default));
        Assert.Empty(await f.Db.Tasks.ToListAsync()); Assert.Empty(await ApplicationClarifications.ReadAsync(f.Db, application.Id, default));
        Assert.Equal(ApplicationState.Blocked, application.State);
    }

    [Fact]
    public async Task ClarificationRechecksOnlyItsVacancyAndRevisesAndVerifiesBothDocumentsWithoutSubmitting()
    {
        var previousRoot = Environment.GetEnvironmentVariable("JOBFLOW_DATA_DIR");
        var root = Path.Combine(Path.GetTempPath(), "jobflow-clarification-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "resumes")); Environment.SetEnvironmentVariable("JOBFLOW_DATA_DIR", root);
        try
        {
            await using var f = await Fixture.CreateAsync(); f.Browser.ExpectedDiscoveries = 0; f.Workflow.AutoSubmit = true;
            var provider = await f.Db.Providers.SingleAsync();
            var resumeAgent = new AgentProfile { CandidateProfileId = f.Workflow.CandidateProfileId, ModelProviderId = provider.Id, TaskType = "Resume", Enabled = true };
            var writer = new AgentProfile { CandidateProfileId = f.Workflow.CandidateProfileId, ModelProviderId = provider.Id, TaskType = "CoverLetter", Enabled = true };
            f.Workflow.ResumeMode = "tailor"; f.Workflow.ResumeAgentId = resumeAgent.Id; f.Workflow.LetterAgentId = writer.Id;
            var source = await f.Db.ResumeVersions.SingleAsync(); source.StorageKey = "source.pdf"; source.OriginalName = "source.pdf";
            await File.WriteAllBytesAsync(ResumeAgentService.PathFor(source), await ResumeAgentService.RenderAsync("Synthetic Candidate", [("Skills", "C# development")], default));
            var vacancy = SavedVacancy(f.Platforms[0], "selected"); var other = SavedVacancy(f.Platforms[1], "other");
            vacancy.PublishedAt = DateTime.UtcNow.AddDays(-20);
            f.Workflow.FreshnessDays = 3;
            var application = new JobApplication { CandidateProfileId = f.Workflow.CandidateProfileId, WorkflowDefinitionId = f.Workflow.Id,
                VacancyId = vacancy.Id, ResumeVersionId = source.Id, State = ApplicationState.ReadyToSubmit, CoverLetter = "Previous letter", Revision = 3 };
            f.Db.AddRange(resumeAgent, writer, vacancy, other, application); await f.Db.SaveChangesAsync();
            const string clarification = "Подтверждаю: могу работать с 10:00 до 19:00 CET.";
            await new PreparationQueue(f.Db).EnqueueClarificationAsync(application.Id, clarification, 3, default, outsideAgeLimit: true);
            var agent = new ClarificationAgent(clarification, f.Db);
            Assert.True(await new TaskProcessor(f.Db, agent, "search", f.Browser).RunOneAsync(default));
            Assert.Equal(ApplicationState.ReadyForReview, application.State);
            Assert.Equal(clarification, application.CoverLetter);
            Assert.NotEqual(source.Id, application.ResumeVersionId);
            Assert.Single(f.Browser.Read); Assert.Equal(vacancy.Id, f.Browser.Read[0].Id); Assert.Empty(f.Browser.Discovered);
            Assert.True(f.Browser.IgnoredPublicationAgeOnRead);
            Assert.False((await f.Db.Tasks.AsNoTracking().SingleAsync()).PrepareOutsideAgeLimit);
            Assert.Equal(0, f.Browser.ExternalPreparations); Assert.Empty(await f.Db.Set<ApplicationAttempt>().ToListAsync());
            Assert.DoesNotContain(clarification, (await f.Db.Profiles.AsNoTracking().SingleAsync()).Dossier);
            Assert.Empty(await ApplicationClarifications.ReadAsync(f.Db, other.Id, default));
            Assert.Contains(agent.Requests, x => x.OutputSchema == AgentTemplates.EvaluationSchema && x.Input.Contains("Vacancy-specific"));
            Assert.Contains(agent.Requests, x => x.OutputSchema == ResumeAgentService.VerifySchema && x.Input.Contains("Vacancy-specific"));
            Assert.Contains(agent.Requests, x => x.OutputSchema == AgentTemplates.LetterVerificationSchema && x.Input.Contains("Vacancy-specific"));
            Assert.Equal("Succeeded", (await f.Db.Runs.SingleAsync()).Status);
            var savedWorkflow = await f.Db.Set<WorkflowDefinition>().AsNoTracking().SingleAsync();
            Assert.True(savedWorkflow.AutoSubmit);
            Assert.Equal(3, savedWorkflow.FreshnessDays);
        }
        finally { Environment.SetEnvironmentVariable("JOBFLOW_DATA_DIR", previousRoot); Directory.Delete(root, true); }
    }

    private sealed class ClarificationAgent(string text, JobFlowDb db) : IAgentExecutor
    {
        public List<AgentExecutionRequest> Requests { get; } = [];
        public Task<AgentExecutionResult> ExecuteAsync(AgentExecutionRequest request, CancellationToken ct)
        {
            Requests.Add(request); using var input = JsonDocument.Parse(request.Input); var root = input.RootElement;
            object output;
            if (request.OutputSchema == ResumeAgentService.SelectionSchema)
                output = new { resumeVersionId = root.GetProperty("resumes")[0].GetProperty("resumeVersionId").GetString(), reason = "Use source" };
            else if (request.OutputSchema == ResumeAgentService.AdaptSchema)
                output = new { title = "Synthetic Candidate", sections = new[] { new { heading = "Availability", claims = new[] { new { text, source = "dossier", quote = text } } } } };
            else if (request.OutputSchema == ResumeAgentService.VerifySchema) output = new { supported = true, issues = Array.Empty<string>() };
            else if (request.OutputSchema == AgentTemplates.LetterVerificationSchema) output = new { supported = true, issues = Array.Empty<string>(), unsupportedClaims = Array.Empty<string>() };
            else if (request.OutputSchema == AgentTemplates.LetterSchema)
            {
                var fact = root.GetProperty("factCatalog").EnumerateArray().Single(x => x.GetProperty("text").GetString() == text);
                output = new { letter = text, citations = new[] { new { factId = fact.GetProperty("id").GetString(), quote = text } } };
            }
            else output = new { decision = "Proceed", rationale = "Candidate confirmed availability", matches = new[] { "C#" }, missingSkills = Array.Empty<string>(), unverifiedRequirements = Array.Empty<string>(), clarifications = Array.Empty<string>(), confidence = "High" };
            var execution = new AgentExecution { AgentProfileId = request.AgentProfileId, Status = "Succeeded" }; db.Add(execution); db.SaveChanges();
            return Task.FromResult(new AgentExecutionResult(execution.Id, "Succeeded", JsonSerializer.Serialize(output, new JsonSerializerOptions(JsonSerializerDefaults.Web))));
        }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string file = Path.Combine(Path.GetTempPath(), "jobflow-controls-" + Guid.NewGuid().ToString("N") + ".db");
        public JobFlowDb Db { get; private set; } = null!;
        public WorkflowDefinition Workflow { get; private set; } = null!;
        public JobPlatform[] Platforms { get; private set; } = [];
        public FakeBrowser Browser { get; } = new();
        public static async Task<Fixture> CreateAsync()
        {
            var f = new Fixture();
            f.Db = new(new DbContextOptionsBuilder<JobFlowDb>().UseSqlite($"Data Source={f.file};Pooling=False").Options);
            await f.Db.Database.MigrateAsync();
            var user = new User(); var profile = new CandidateProfile { UserId = user.Id, Skills = "C#", Name = "Synthetic Candidate" };
            var resume = new Resume { CandidateProfileId = profile.Id }; var version = new ResumeVersion { ResumeId = resume.Id };
            var provider = new ModelProvider { Enabled = true };
            var agent = new AgentProfile { CandidateProfileId = profile.Id, ModelProviderId = provider.Id, TaskType = "BrowserAutomation", Enabled = true };
            var evaluator = new AgentProfile { CandidateProfileId = profile.Id, ModelProviderId = provider.Id, TaskType = "Evaluation", Enabled = true };
            var browser = new BrowserProfile();
            f.Platforms = [new() { Name = "First", Url = "https://first.example/" }, new() { Name = "Second", Url = "https://second.example/" }];
            f.Workflow = new() { Name = "Controlled workflow", CandidateProfileId = profile.Id, JobPlatformIds = f.Platforms.Select(x => x.Id).ToArray(),
                JobPlatformId = f.Platforms[0].Id, ResumeVersionId = version.Id, ExecutionMode = "browser", Enabled = true,
                BrowserProfileId = browser.Id, BrowserAgentId = agent.Id, EvaluationAgentId = evaluator.Id, CheckMail = false };
            f.Db.AddRange(user, profile, resume, version, provider, agent, evaluator, browser, f.Platforms[0], f.Platforms[1], f.Workflow);
            await f.Db.SaveChangesAsync(); return f;
        }
        public Task<WorkflowRun> RunAsync() => new JobSearchWorkflow(Db, new Evaluator(), Browser, monitorWithinSearch: false).RunAsync(Workflow.Id, default);
        public async ValueTask DisposeAsync() { await Db.DisposeAsync(); File.Delete(file); }
    }
    private sealed class Evaluator(string decision="NeedsReview", string[]? clarifications = null) : IAgentExecutor
    {
        public Task<AgentExecutionResult> ExecuteAsync(AgentExecutionRequest request, CancellationToken ct) =>
            Task.FromResult(new AgentExecutionResult(Guid.NewGuid(), "Succeeded", JsonSerializer.Serialize(new {
                decision, rationale = "Potential fit", matches = new[] { "C#" }, missingSkills = Array.Empty<string>(),
                unverifiedRequirements = decision=="Proceed" ? Array.Empty<string>() : new[] { "Confirm working hours" }, clarifications = clarifications ?? [], confidence = "Medium" })));
    }
    private sealed class FakeBrowser : IBrowserWorkflowDriver
    {
        public Func<JobApplication, Func<Task>, Task<BrowserResult>>? Submit { get; set; }
        public bool ExternalReview { get; set; }
        public int ExternalPreparations { get; private set; }
        public Task<bool> PrepareExternalDraftAsync(WorkflowDefinition workflow, JobPlatform platform, CandidateProfile candidate,
            Vacancy vacancy, JobApplication application, CancellationToken ct)
        { ExternalPreparations++; return Task.FromResult(ExternalReview); }
        public int ExpectedDiscoveries { get; set; } = 2;
        public List<Guid> Discovered { get; } = [];
        public List<Vacancy> Read { get; } = [];
        public bool IgnoredPublicationAgeOnRead { get; private set; }
        public bool FailFirstRead { get; set; }
        public Exception? ReadException { get; set; }
        public Exception? DiscoveryException { get; set; }
        public bool Closed { get; private set; }
        public Func<Task>? OnBegin { get; set; }
        public async Task<IAsyncDisposable> BeginRunAsync(WorkflowDefinition workflow, CancellationToken ct)
        {
            if (OnBegin != null) await OnBegin();
            return new CloseScope(() => Closed = true);
        }
        public Task<IReadOnlyList<Vacancy>> DiscoverAsync(WorkflowDefinition workflow, JobPlatform platform, CandidateProfile candidate, CancellationToken ct)
        {
            Discovered.Add(platform.Id);
            if(DiscoveryException!=null)throw DiscoveryException;
            return Task.FromResult<IReadOnlyList<Vacancy>>(Enumerable.Range(0, 3).Select(i => new Vacancy {
                JobPlatformId = platform.Id, ExternalId = i.ToString(), Title = "Job " + i, Url = platform.Url + "jobs/" + i }).ToArray());
        }
        public Task ReadVacancyAsync(WorkflowDefinition workflow, JobPlatform platform, Vacancy vacancy, CancellationToken ct)
        {
            Assert.Equal(ExpectedDiscoveries, Discovered.Count);
            IgnoredPublicationAgeOnRead = workflow.IgnorePublicationAge;
            Read.Add(vacancy);
            if(ReadException!=null)throw ReadException;
            if (FailFirstRead && Read.Count == 1) throw new InvalidOperationException("Synthetic first-card failure");
            return Task.CompletedTask;
        }
        public Task<BrowserResult> ApplyAsync(WorkflowDefinition workflow, JobPlatform platform, CandidateProfile candidate, Vacancy vacancy,
            JobApplication application, string resumePath, Func<Task> beforeSubmit, CancellationToken ct) =>
            Submit?.Invoke(application,beforeSubmit) ?? throw new InvalidOperationException("Unexpected submission");
        private sealed class CloseScope(Action close) : IAsyncDisposable { public ValueTask DisposeAsync() { close(); return ValueTask.CompletedTask; } }
    }
}
