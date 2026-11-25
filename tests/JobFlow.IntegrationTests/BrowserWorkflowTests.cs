using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using JobFlow.Application;
using JobFlow.Domain;
using JobFlow.Infrastructure;
using JobFlow.Api;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Playwright;

namespace JobFlow.IntegrationTests;

public class BrowserWorkflowTests
{
    [Fact]
    public async Task SnapshotRecoversWhenAFrameIsRemovedWhileReadingThePage()
    {
        await using var f = await Fixture.CreateAsync(true, scenario: "frame-detached");
        f.Agent.BrowserOverride = root =>
        {
            Assert.Contains("Frame removed during observation", root.GetProperty("page").GetProperty("text").GetString());
            return FixtureAgent.Action("finish");
        };

        Assert.Empty(await f.Driver.DiscoverAsync(f.Definition, f.Platform, await f.Db.Profiles.SingleAsync(), default));
        Assert.Equal(1, f.SearchPageLoads);
        Assert.Equal(0, f.Submissions);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ScreenshotFallbackRecoversStaleTargetOrAnActionWithoutProgress(bool noProgress)
    {
        await using var f = await Fixture.CreateAsync(true, scenario: "visual", options: new() { VisualRecoveryLimit = 3 });
        var visualCalls = 0;
        f.Agent.BrowserOverride = root =>
        {
            var page = root.GetProperty("page");
            if (page.GetProperty("text").GetString()!.Contains("Visual recovery worked")) return FixtureAgent.Action("finish");
            return FixtureAgent.Action("click", noProgress ? page.GetProperty("controls").EnumerateArray()
                .Single(x => x.GetProperty("label").GetString() == "No effect").GetProperty("id").GetString()! : "missing-target");
        };
        f.Agent.VisualOverride = async request =>
        {
            visualCalls++;
            Assert.NotNull(request.ImagePng);
            Assert.True(request.ImagePng.Length > 100);
            Assert.Equal(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, request.ImagePng[..8]);
            var page = f.Context.Pages.Single(x => x.Url.Contains("/search"));
            var box = (await page.Locator("#visual-action").BoundingBoxAsync())!;
            return Visual("click", (int)(box.X + box.Width / 2), (int)(box.Y + box.Height / 2));
        };
        Assert.Empty(await f.Driver.DiscoverAsync(f.Definition, f.Platform, await f.Db.Profiles.SingleAsync(), default));
        Assert.Equal(1, visualCalls);
        var audit = await f.Db.Audit.SingleAsync(x => x.Action == "Browser/search/VisualRecovery");
        Assert.Contains("\"changed\":true", audit.Details);
        Assert.Equal(0, f.Submissions);
    }

    [Theory]
    [InlineData("submit")]
    [InlineData("outside")]
    [InlineData("stale")]
    public async Task ScreenshotFallbackRejectsSubmissionInvalidCoordinatesAndChangedPages(string fault)
    {
        await using var f = await Fixture.CreateAsync(true, scenario: "visual", options: new() { VisualRecoveryLimit = 1 });
        f.Agent.BrowserOverride = _ => FixtureAgent.Action("blocked", reason: "Cannot locate the control");
        f.Agent.VisualOverride = async _ =>
        {
            var page = f.Context.Pages.Single(x => x.Url.Contains("/search"));
            var box = (await page.Locator(fault == "submit" ? "#visual-submit" : "#visual-action").BoundingBoxAsync())!;
            if (fault == "stale") await page.Locator("h1").EvaluateAsync("el => el.textContent='Page changed during model decision'");
            return Visual("click", fault == "outside" ? -1 : (int)(box.X + box.Width / 2), (int)(box.Y + box.Height / 2));
        };
        var candidate = await f.Db.Profiles.SingleAsync();
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => f.Driver.DiscoverAsync(f.Definition, f.Platform, candidate, default));
        Assert.Equal(0, f.SearchPosts);
        Assert.Equal(0, f.Submissions);
        Assert.Single(f.Agent.Requests, x => x.OutputSchema == BrowserWorkflowDriver.VisualActionSchema);
    }

    private static AgentExecutionResult Visual(string action, int x, int y, string value = "", int deltaY = 0) =>
        new(Guid.NewGuid(), "Succeeded", JsonSerializer.Serialize(new { action, x, y, value, deltaY, reason = "Synthetic visual recovery" }));

    [Fact]
    public async Task ScreenshotScrollCountsAsProgressAndCancellationPreventsVisualClick()
    {
        await using var f = await Fixture.CreateAsync(true, scenario: "visual", options: new() { VisualRecoveryLimit = 3 });
        var visualCalls = 0;
        f.Agent.BrowserOverride = root => root.GetProperty("page").GetProperty("viewport")[3].GetInt32() > 0
            ? FixtureAgent.Action("finish") : FixtureAgent.Action("click", "missing-target");
        f.Agent.VisualOverride = _ => { visualCalls++; return Task.FromResult(Visual("scroll", 0, 0, deltaY: 500)); };
        Assert.Empty(await f.Driver.DiscoverAsync(f.Definition, f.Platform, await f.Db.Profiles.SingleAsync(), default));
        Assert.Equal(1, visualCalls);

        using var cancel = new CancellationTokenSource();
        f.Agent.BrowserOverride = _ => FixtureAgent.Action("blocked", reason: "Cannot locate the control");
        f.Agent.VisualOverride = _ => { cancel.Cancel(); return Task.FromResult(Visual("click", 150, 170)); };
        var candidate = await f.Db.Profiles.SingleAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Driver.DiscoverAsync(f.Definition, f.Platform, candidate, cancel.Token));
        Assert.Equal(1, await f.Db.Audit.CountAsync(x => x.Action == "Browser/search/VisualRecovery"));
        Assert.Equal(0, f.Submissions);
    }

    [Fact]
    public async Task PerWorkflowDiscoveryLimitControlsHowManyObservedLinksAreSaved()
    {
        await using var f = await Fixture.CreateAsync(true, scenario: "multiple");
        f.Definition.MaxVacanciesPerPlatform = 1;
        f.Agent.BrowserOverride = root =>
        {
            Assert.Equal(1, root.GetProperty("task").GetProperty("limit").GetInt32());
            var links = root.GetProperty("page").GetProperty("controls").EnumerateArray()
                .Where(x => x.GetProperty("href").GetString()!.Contains("/jobs/")).Take(2).ToArray();
            return FixtureAgent.Action("finish", vacancies: links.Select((x, i) => (object)new {
                title = "Job " + i, employer = "Synthetic", url = x.GetProperty("href").GetString(), description = "C#" }).ToArray());
        };
        var found = await f.Driver.DiscoverAsync(f.Definition, f.Platform, await f.Db.Profiles.SingleAsync(), default);
        Assert.Single(found);
        Assert.Single(await f.Db.Vacancies.ToListAsync());
    }

    [Fact]
    public async Task CancellationClosesBrowserAndNextWorkflowReopensTheSameAuthenticatedProfile()
    {
        await using var f = await Fixture.CreateAsync(true);
        using var cancel = new CancellationTokenSource();
        f.Agent.BrowserOverride = _ => { cancel.Cancel(); return FixtureAgent.Action("finish"); };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new JobSearchWorkflow(f.Db, f.Agent, f.Driver, monitorWithinSearch: false).RunAsync(f.Definition.Id, cancel.Token));
        Assert.False(f.Sessions.IsBusy(f.Browser.Id));
        Assert.False(f.Sessions.IsAutomating(f.Browser.Id));
        Assert.Equal("Interrupted", (await f.Db.Set<WorkflowRun>().SingleAsync()).Status);
        f.Agent.BrowserOverride = null;
        var next = await new JobSearchWorkflow(f.Db, f.Agent, f.Driver, monitorWithinSearch: false).RunAsync(f.Definition.Id, default);
        Assert.Equal("Succeeded", next.Status);
        Assert.Equal(1, f.Submissions);
        Assert.False(f.Sessions.IsBusy(f.Browser.Id));
    }

    [Fact]
    public async Task MetadataPublicationDateSurvivesAnUnparseableModelDate()
    {
        await using var f = await Fixture.CreateAsync(true, scenario: "metadata-date");
        f.Agent.FreshnessFault = "unknown";
        var next = await new JobSearchWorkflow(f.Db, f.Agent, f.Driver, monitorWithinSearch: false).RunAsync(f.Definition.Id, default);
        Assert.Equal("Succeeded", next.Status);
        Assert.Equal(1, f.Submissions);
        Assert.NotNull((await f.Db.Vacancies.SingleAsync()).PublishedAt);
        Assert.Contains(await f.Db.Audit.Where(x => x.Action == "VacancyFreshnessChecked").ToListAsync(), x => x.Details.Contains("JobPosting.datePosted"));
    }

    [Fact]
    public async Task PlatformBrowserModeIsUsedForTheWholeWorkflow()
    {
        await using var f = await Fixture.CreateAsync(true);
        f.Platform.BrowserMode = "new-headless";
        await f.Db.SaveChangesAsync();
        var run = await new JobSearchWorkflow(f.Db, f.Agent, f.Driver, f.Mail).RunAsync(f.Definition.Id, default);
        Assert.Equal("Succeeded", run.Status);
        Assert.Equal(1, f.Submissions);
        Assert.Null(f.Sessions.GetMode(f.Browser.Id));
        Assert.False(f.Sessions.IsBusy(f.Browser.Id));
        var actions = await f.Db.Audit.Where(x => x.Action.StartsWith("Browser/")).ToListAsync();
        Assert.NotEmpty(actions);
        Assert.All(actions, action =>
        {
            using var document = JsonDocument.Parse(action.Details);
            Assert.Equal("new-headless", document.RootElement.GetProperty("browserMode").GetString());
        });
    }

    [Fact]
    public async Task RediscoveredArchivedVacancyKeepsHistoryAndIsNotEvaluatedOrSubmitted()
    {
        await using var f = await Fixture.CreateAsync(true);
        var vacancy = new Vacancy { JobPlatformId = f.Platform.Id, ExternalId = "1", Title = "Archived job", Url = f.BaseUrl + "/jobs/1", IsArchived = true };
        f.Db.Add(vacancy);
        await f.Db.SaveChangesAsync();
        var run = await new JobSearchWorkflow(f.Db, f.Agent, f.Driver, monitorWithinSearch: false).RunAsync(f.Definition.Id, default);
        Assert.Equal("Succeeded", run.Status);
        Assert.Equal(0, run.Prepared);
        Assert.Empty(await f.Db.Applications.ToListAsync());
        Assert.Empty(await f.Db.Set<VacancyEvaluation>().ToListAsync());
        Assert.True((await f.Db.Vacancies.SingleAsync()).IsArchived);
        Assert.Equal(0, f.Submissions);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EmptyDiscoveryDoesNotResurrectCachedVacancyAndExplainsWhy(bool previousBlockedApplication)
    {
        await using var f = await Fixture.CreateAsync(true);
        var cached = new Vacancy { JobPlatformId = f.Platform.Id, ExternalId = "cached", Title = "Old cached job", Url = f.BaseUrl + "/jobs/1" };
        f.Db.Add(cached);
        if (previousBlockedApplication)
            f.Db.Add(new JobApplication { CandidateProfileId = f.Definition.CandidateProfileId, VacancyId = cached.Id,
                ResumeVersionId = f.Definition.ResumeVersionId, WorkflowDefinitionId = f.Definition.Id, State = ApplicationState.Blocked });
        await f.Db.SaveChangesAsync();
        f.Agent.BrowserOverride = _ => FixtureAgent.Action("finish", reason: "No suitable current vacancies");
        var run = await new JobSearchWorkflow(f.Db, f.Agent, f.Driver, monitorWithinSearch: false).RunAsync(f.Definition.Id, default);
        Assert.Equal("Succeeded", run.Status);
        Assert.Equal(0, run.Prepared);
        if (previousBlockedApplication) Assert.Equal(ApplicationState.Blocked, (await f.Db.Applications.SingleAsync()).State);
        else Assert.Empty(await f.Db.Applications.ToListAsync());
        Assert.Empty(await f.Db.Set<VacancyEvaluation>().ToListAsync());
        Assert.Single(await f.Db.Vacancies.ToListAsync());
        Assert.Equal(0, f.Submissions);
        var skipped = await f.Db.Audit.SingleAsync(x => x.Action == "WorkflowVacancySkipped");
        Assert.Contains(cached.Id.ToString(), skipped.Details);
        Assert.Contains("cached vacancy is not eligible", skipped.Details);
    }

    [Fact]
    public async Task ConflictingVisibleAndStructuredDatesBlockBeforeEvaluationOrSubmission()
    {
        await using var f = await Fixture.CreateAsync(true, scenario: "conflicting-dates");
        var run = await new JobSearchWorkflow(f.Db, f.Agent, f.Driver, monitorWithinSearch: false).RunAsync(f.Definition.Id, default);
        Assert.Equal("NeedsAttention", run.Status);
        var vacancy = await f.Db.Vacancies.SingleAsync();
        Assert.Null(vacancy.PublishedAt);
        Assert.Equal("Unknown", vacancy.Availability);
        Assert.Contains("JobPosting.datePosted", vacancy.PublicationEvidence);
        Assert.Contains("Visible publication label", vacancy.PublicationEvidence);
        Assert.Empty(await f.Db.Set<VacancyEvaluation>().ToListAsync());
        Assert.Empty(await f.Db.Set<ApplicationAttempt>().ToListAsync());
        Assert.Equal(ApplicationState.Blocked, (await f.Db.Applications.SingleAsync()).State);
        Assert.Equal(0, f.Submissions);
        Assert.Single(await f.Db.Audit.Where(x => x.Action == "PublicationDateConflict").ToListAsync());
        var notification = await f.Db.Set<Notification>().SingleAsync(x => x.Kind == "Attention");
        Assert.Contains("Conflicting publication dates", notification.Message);
        // A later failure must replace the obsolete cause in the same unread notification.
        f.Agent.FreshnessFault = "unknown";
        notification.Message = "Obsolete failure";
        await f.Db.SaveChangesAsync();
        await new JobSearchWorkflow(f.Db, f.Agent, f.Driver, monitorWithinSearch: false).RunAsync(f.Definition.Id, default);
        Assert.Single(await f.Db.Set<Notification>().Where(x => x.Kind == "Attention").ToListAsync());
        Assert.Contains("Conflicting publication dates", notification.Message);
    }

    [Fact]
    public async Task SearchReturnsToVisitedResultsAndCollectsTwoDifferentVacancies()
    {
        await using var f = await Fixture.CreateAsync(true, scenario: "multiple");
        var step = 0;
        f.Agent.BrowserOverride = root =>
        {
            var page = root.GetProperty("page");
            var url = page.GetProperty("url").GetString()!;
            var links = page.GetProperty("controls").EnumerateArray().Select(x => x.GetProperty("href").GetString()!).ToArray();
            return ++step switch
            {
                1 => FixtureAgent.Action("navigate", url: links.Single(x => x.EndsWith("/jobs/1"))),
                2 => FixtureAgent.Action("collect", vacancies: [new { title = "First job", employer = "Synthetic", url, description = "C#" }]),
                3 => FixtureAgent.Action("navigate", url: page.GetProperty("visitedPages").EnumerateArray().Single(x => x.GetProperty("url").GetString()!.EndsWith("/search")).GetProperty("url").GetString()!),
                4 => FixtureAgent.Action("navigate", url: links.Single(x => x.EndsWith("/jobs/2"))),
                _ => FixtureAgent.Action("finish", vacancies: [new { title = "Second job", employer = "Synthetic", url, description = "C#" }])
            };
        };
        var found = await f.Driver.DiscoverAsync(f.Definition, f.Platform, await f.Db.Profiles.SingleAsync(), default);
        Assert.Equal(2, found.Count);
        Assert.Equal(2, await f.Db.Vacancies.CountAsync());
        Assert.Equal(0, f.Submissions);
        var completed = await f.Db.Audit.Where(x => x.Action == "Browser/search/ActionCompleted").ToListAsync();
        Assert.Contains(completed, row => { using var d = JsonDocument.Parse(row.Details); return d.RootElement.GetProperty("requestedUrl").GetString() == f.BaseUrl + "/search" && d.RootElement.GetProperty("actualUrl").GetString() == f.BaseUrl + "/search"; });
    }

    [Fact]
    public async Task CollectedVacancySurvivesFailureWithoutApplyingAndRetryDoesNotDuplicateIt()
    {
        await using var f = await Fixture.CreateAsync(true);
        var step = 0;
        f.Agent.BrowserOverride = root =>
        {
            var url = root.GetProperty("page").GetProperty("controls").EnumerateArray().Single(x => x.GetProperty("href").GetString()!.Contains("/jobs/1")).GetProperty("href").GetString()!;
            return ++step == 1 ? FixtureAgent.Action("collect", vacancies: [new { title = "Retained job", employer = "Synthetic", url, description = "C#" }]) : FixtureAgent.Action("blocked", reason: "Synthetic navigation failure after collection");
        };
        await Assert.ThrowsAsync<InvalidOperationException>(() => new JobSearchWorkflow(f.Db, f.Agent, f.Driver, monitorWithinSearch: false).RunAsync(f.Definition.Id, default));
        var vacancy = await f.Db.Vacancies.SingleAsync();
        var id = vacancy.Id;
        Assert.Empty(await f.Db.Applications.ToListAsync());
        Assert.Equal(0, f.Submissions);
        f.Agent.BrowserOverride = _ => FixtureAgent.Action("finish", vacancies: [new { title = "Retained job", employer = "Synthetic", url = f.BaseUrl + "/jobs/1", description = "C#" }]);
        await f.Driver.DiscoverAsync(f.Definition, f.Platform, await f.Db.Profiles.SingleAsync(), default);
        Assert.Equal(id, (await f.Db.Vacancies.SingleAsync()).Id);
    }

    [Theory]
    [InlineData("https://outside.invalid/search", "outside the selected platform")]
    [InlineData("/never-observed", "neither an observed link")]
    public async Task NavigationRejectionRecordsRequestedAndActualUrls(string target, string reason)
    {
        await using var f = await Fixture.CreateAsync(true, options: new() { ActionRecoveryLimit = 0 });
        if (target.StartsWith('/')) target = f.BaseUrl + target;
        f.Agent.BrowserOverride = _ => FixtureAgent.Action("navigate", url: target);
        var profile = await f.Db.Profiles.SingleAsync();
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => f.Driver.DiscoverAsync(f.Definition, f.Platform, profile, default));
        Assert.Contains(reason, failure.Message);
        var trace = await f.Db.Audit.SingleAsync(x => x.Action == "Browser/search/navigate");
        using var parsed = JsonDocument.Parse(trace.Details);
        Assert.Equal(target, parsed.RootElement.GetProperty("requestedUrl").GetString());
        Assert.Equal(f.BaseUrl + "/search", parsed.RootElement.GetProperty("url").GetString());
        Assert.Equal(0, f.Submissions);
    }

    [Fact]
    public async Task CustomPeriodAndSortControlsWaitForUpdatedResults()
    {
        await using var f = await Fixture.CreateAsync(true, scenario: "custom-settings");
        var step = 0;
        f.Agent.BrowserOverride = root =>
        {
            var page = root.GetProperty("page");
            var controls = page.GetProperty("controls").EnumerateArray().ToArray();
            string Target(string label)
            {
                var matches = controls.Where(x => x.GetProperty("label").GetString() == label).ToArray();
                Assert.True(matches.Length == 1, $"Step {step}: expected {label}; controls: {string.Join(" | ", controls.Select(x => x.GetProperty("label").GetString()))}");
                return matches[0].GetProperty("id").GetString()!;
            }
            switch (++step)
            {
                case 1: Assert.Contains(controls, x => x.GetProperty("role").GetString() == "combobox"); return FixtureAgent.Action("click", Target("За всё время"));
                case 2: return FixtureAgent.Action("click", Target("За три дня"));
                case 3: Assert.Contains("period=3", page.GetProperty("url").GetString()); return FixtureAgent.Action("click", Target("По соответствию"));
                case 4: return FixtureAgent.Action("click", Target("По дате публикации"));
                default:
                    Assert.Contains("order=newest", page.GetProperty("url").GetString());
                    Assert.Equal("", page.GetProperty("activeDialog").GetString());
                    return FixtureAgent.Action("finish");
            }
        };
        await f.Driver.DiscoverAsync(f.Definition, f.Platform, await f.Db.Profiles.SingleAsync(), default);
        Assert.Equal(2, f.FilterReads);
        Assert.Equal(5, step);
    }

    [Fact]
    public async Task SearchReturnHistoryDoesNotOfferPostOnlyResults()
    {
        await using var f = await Fixture.CreateAsync(true, true, scenario: "post-only");
        var step = 0;
        f.Agent.BrowserOverride = root =>
        {
            var page = root.GetProperty("page");
            var controls = page.GetProperty("controls").EnumerateArray().ToArray();
            string Target(string label) => controls.Single(x => x.GetProperty("label").GetString() == label).GetProperty("id").GetString()!;
            switch (++step)
            {
                case 1: return FixtureAgent.Action("fill", Target("Search query"), "C# Engineer");
                case 2: return FixtureAgent.Action("click", Target("Search"));
                case 3: Assert.EndsWith("/post-result", page.GetProperty("url").GetString()); return FixtureAgent.Action("navigate", url: f.BaseUrl + "/jobs/1");
                case 4:
                    Assert.DoesNotContain(page.GetProperty("visitedPages").EnumerateArray(), x => x.GetProperty("url").GetString()!.EndsWith("/post-result"));
                    return FixtureAgent.Action("navigate", url: f.BaseUrl + "/post-result");
                default:
                    Assert.Contains("neither an observed link", page.GetProperty("lastActionError").GetString());
                    return FixtureAgent.Action("finish");
            }
        };
        await f.Driver.DiscoverAsync(f.Definition, f.Platform, await f.Db.Profiles.SingleAsync(), default);
        Assert.Equal(1, f.SearchPosts);
        Assert.Equal(0, f.Submissions);
    }

    [Fact]
    public async Task RerenderedUniqueTargetIsResolvedAndNextCallReceivesProgressMemory()
    {
        await using var f = await Fixture.CreateAsync(true);
        var calls = 0;
        f.Agent.BrowserOverride = async root =>
        {
            var memory = root.GetProperty("memory");
            if (++calls == 1)
            {
                Assert.Empty(memory.GetProperty("recentActions").EnumerateArray());
                var target = root.GetProperty("page").GetProperty("controls").EnumerateArray().Single(c => c.GetProperty("href").GetString()!.Contains("/jobs/1"));
                await f.Context.Pages.Last().EvaluateAsync("""
                    () => {const old=document.querySelector('a[href="/jobs/1"]');const next=old.cloneNode(true);next.removeAttribute('data-jobflow-agent-id');old.replaceWith(next);}
                    """);
                return await FixtureAgent.Action("click", target.GetProperty("id").GetString()!, reason: "Open observed vacancy");
            }
            Assert.Contains("/jobs/1", root.GetProperty("page").GetProperty("url").GetString());
            var previous = Assert.Single(memory.GetProperty("recentActions").EnumerateArray());
            Assert.Equal("Executed", previous.GetProperty("outcome").GetString());
            Assert.Equal("Open observed vacancy", previous.GetProperty("reason").GetString());
            Assert.True(previous.GetProperty("pageChanged").GetBoolean());
            Assert.False(previous.TryGetProperty("target", out _));
            return await FixtureAgent.Action("finish");
        };
        await f.Driver.DiscoverAsync(f.Definition, f.Platform, await f.Db.Profiles.SingleAsync(), default);
        Assert.Equal(2, calls);
        Assert.False(await f.Db.Audit.AnyAsync(x => x.Action == "Browser/search/ActionFailed"));
    }

    [Fact]
    public async Task AmbiguousReplacementIsNotClickedAndFailureSurvivesSuccessfulWait()
    {
        await using var f = await Fixture.CreateAsync(true);
        var calls = 0;
        f.Agent.BrowserOverride = async root =>
        {
            var page = root.GetProperty("page");
            if (++calls == 1)
            {
                var target = page.GetProperty("controls").EnumerateArray().Single(c => c.GetProperty("href").GetString()!.Contains("/jobs/1"));
                await f.Context.Pages.Last().EvaluateAsync("""
                    () => {const old=document.querySelector('a[href="/jobs/1"]');const next=old.cloneNode(true);next.removeAttribute('data-jobflow-agent-id');old.replaceWith(next,next.cloneNode(true));}
                    """);
                return await FixtureAgent.Action("click", target.GetProperty("id").GetString()!);
            }
            Assert.EndsWith("/search", page.GetProperty("url").GetString());
            if (calls == 2)
            {
                Assert.Contains("ambiguous", page.GetProperty("lastActionError").GetString());
                return await FixtureAgent.Action("wait");
            }
            Assert.Equal("", page.GetProperty("lastActionError").GetString());
            var history = root.GetProperty("memory").GetProperty("recentActions").EnumerateArray().ToArray();
            Assert.Equal(2, history.Length);
            Assert.Equal("Failed", history[0].GetProperty("outcome").GetString());
            Assert.Contains("ambiguous", history[0].GetProperty("error").GetString());
            Assert.Equal("wait", history[1].GetProperty("action").GetString());
            Assert.False(history[1].GetProperty("pageChanged").GetBoolean());
            return await FixtureAgent.Action("finish");
        };
        await f.Driver.DiscoverAsync(f.Definition, f.Platform, await f.Db.Profiles.SingleAsync(), default);
        Assert.Equal(3, calls);
        Assert.Equal(0, f.Submissions);
    }

    [Fact]
    public async Task UnchangedPageLoopIsWarnedThenStoppedWithDiagnostics()
    {
        await using var f = await Fixture.CreateAsync(true);
        f.Definition.MaxBrowserSteps = 40;
        var calls = 0; var warned = false;
        f.Agent.BrowserOverride = root =>
        {
            calls++;
            if (root.GetProperty("memory").GetProperty("sameStateVisits").GetInt32() >= 3)
                warned |= root.GetProperty("memory").GetProperty("warning").GetString()!.Contains("repeatedly");
            return FixtureAgent.Action("wait");
        };
        var profile = await f.Db.Profiles.SingleAsync();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => f.Driver.DiscoverAsync(f.Definition, f.Platform, profile, default));
        Assert.Contains("repeated page states without progress", error.Message);
        Assert.Contains("/api/browser-diagnostics/", error.Message);
        Assert.Equal(7, calls); Assert.True(warned); Assert.Equal(0, f.Submissions);
    }

    [Fact]
    public async Task ModalControlsArePrioritizedOverHundredsOfBackgroundLinks()
    {
        await using var f = await Fixture.CreateAsync(true, scenario: "modal");
        var sawDialog = false;
        f.Agent.BrowserOverride = root =>
        {
            var page = root.GetProperty("page");
            var controls = page.GetProperty("controls").EnumerateArray().ToArray();
            if (page.GetProperty("activeDialog").GetString()!.Length > 0)
            {
                sawDialog = true;
                Assert.DoesNotContain(controls, c => c.GetProperty("href").GetString()!.Contains("/background"));
                Assert.DoesNotContain(controls, c => c.GetProperty("isSubmit").GetBoolean());
                return FixtureAgent.Action("click", controls.Single(c => c.GetProperty("label").GetString() == "Close dialog").GetProperty("id").GetString()!);
            }
            Assert.True(sawDialog);
            Assert.Contains(controls, c => c.GetProperty("href").GetString()!.Contains("/jobs/1"));
            return FixtureAgent.Action("finish");
        };
        await f.Driver.DiscoverAsync(f.Definition, f.Platform, await f.Db.Profiles.SingleAsync(), default);
        Assert.True(sawDialog);
        Assert.Equal(0, f.Submissions);
    }

    [Fact]
    public async Task ObstructedClickReturnsFreshSnapshotAndErrorToAgent()
    {
        await using var f = await Fixture.CreateAsync(true, options: new() { ActionTimeoutSeconds = 1 });
        var injected = false; var recovered = false;
        f.Agent.BrowserOverride = async root =>
        {
            var snapshot = root.GetProperty("page");
            var controls = snapshot.GetProperty("controls").EnumerateArray().ToArray();
            if (!injected)
            {
                injected = true;
                var target = controls.Single(c => c.GetProperty("href").GetString()!.Contains("/jobs/1")).GetProperty("id").GetString()!;
                await f.Context.Pages.Last().EvaluateAsync("""
                    () => {const overlay=document.createElement('div');overlay.setAttribute('role','dialog');overlay.setAttribute('aria-modal','true');
                    overlay.style='position:fixed;inset:0;z-index:999;background:white';overlay.innerHTML='<p>New dialog</p><button onclick="this.parentElement.remove()">Close dialog</button>';document.body.append(overlay);}
                    """);
                return await FixtureAgent.Action("click", target);
            }
            if (snapshot.GetProperty("activeDialog").GetString()!.Length > 0)
            {
                Assert.Contains("failed", snapshot.GetProperty("lastActionError").GetString());
                recovered = true;
                return await FixtureAgent.Action("click", controls.Single().GetProperty("id").GetString()!);
            }
            Assert.Equal("", snapshot.GetProperty("lastActionError").GetString());
            return await FixtureAgent.Action("finish");
        };
        await f.Driver.DiscoverAsync(f.Definition, f.Platform, await f.Db.Profiles.SingleAsync(), default);
        Assert.True(recovered);
        Assert.Single(await f.Db.Audit.Where(x => x.Action == "Browser/search/ActionFailed").ToListAsync());
    }

    [Fact]
    public async Task ExhaustedRecoverySavesScreenshotAndSpecificWorkflowError()
    {
        await using var f = await Fixture.CreateAsync(true, options: new() { ActionRecoveryLimit = 1 });
        f.Agent.BrowserOverride = _ => FixtureAgent.Action("click", "stale-target");
        await Assert.ThrowsAsync<InvalidOperationException>(() => new JobSearchWorkflow(f.Db, f.Agent, f.Driver, monitorWithinSearch: false).RunAsync(f.Definition.Id, default));
        var run = await f.Db.Set<WorkflowRun>().SingleAsync();
        Assert.Equal("Failed", run.Status);
        Assert.Contains("recovery limit (1) exhausted", run.Error);
        Assert.Contains("/api/browser-diagnostics/", run.Error);
        var failureFile = Directory.GetFiles(Path.Combine(Storage.Root, "browser-diagnostics"), "failure.json", SearchOption.AllDirectories).Single();
        using var failure = JsonDocument.Parse(await File.ReadAllTextAsync(failureFile));
        Assert.True(failure.RootElement.GetProperty("screenshot").GetBoolean());
        Assert.True(new FileInfo(Path.Combine(Path.GetDirectoryName(failureFile)!, "screen.jpg")).Length > 0);
        Assert.Contains("stale", failure.RootElement.GetProperty("error").GetString());
        using (var factory = new WebApplicationFactory<Program>())
        using (var client = factory.CreateClient())
        {
            var diagnosticId = failure.RootElement.GetProperty("id").GetGuid();
            var metadata = await client.GetAsync($"/api/browser-diagnostics/{diagnosticId}");
            Assert.Equal(HttpStatusCode.OK, metadata.StatusCode);
            using var payload = JsonDocument.Parse(await metadata.Content.ReadAsStringAsync());
            Assert.Equal(diagnosticId, payload.RootElement.GetProperty("id").GetGuid());
            var screen = await client.GetAsync($"/api/browser-diagnostics/{diagnosticId}/screen");
            Assert.Equal("image/jpeg", screen.Content.Headers.ContentType?.MediaType);
            Assert.True((await screen.Content.ReadAsByteArrayAsync()).Length > 0);
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/browser-diagnostics/{Guid.NewGuid()}")).StatusCode);
        }
        Assert.False(f.Sessions.IsAutomating(f.Browser.Id));
        Assert.Equal(0, f.Submissions);
        Assert.Equal(2, await f.Db.Audit.CountAsync(x => x.Action == "Browser/search/ActionFailed"));
    }

    [Fact]
    public async Task SlowInitialNavigationIsRetriedOnce()
    {
        await using var f = await Fixture.CreateAsync(true, scenario: "slow-first", options: new() { NavigationTimeoutSeconds = 2 });
        var found = await f.Driver.DiscoverAsync(f.Definition, f.Platform, await f.Db.Profiles.SingleAsync(), default);
        Assert.Single(found);
        Assert.Equal(2, f.SearchPageLoads);
    }

    [Fact]
    public async Task NavigationRetryLimitPreservesTimeoutReason()
    {
        await using var f = await Fixture.CreateAsync(true, scenario: "slow-always", options: new() { NavigationTimeoutSeconds = 2 });
        var candidate = await f.Db.Profiles.SingleAsync();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => f.Driver.DiscoverAsync(f.Definition, f.Platform, candidate, default));
        Assert.Contains("Timeout 2000ms", error.Message);
        Assert.Contains("Diagnostics:", error.Message);
        Assert.Equal(2, f.SearchPageLoads);
    }

    [Fact]
    public async Task CancellationStopsNavigationWithoutRetryAndReleasesBrowser()
    {
        await using var f = await Fixture.CreateAsync(true, scenario: "slow-always");
        var candidate = await f.Db.Profiles.SingleAsync();
        using var stop = new CancellationTokenSource();
        f.SearchRequested = stop.Cancel;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Driver.DiscoverAsync(f.Definition, f.Platform, candidate, stop.Token));
        Assert.Equal(1, f.SearchPageLoads);
        Assert.False(f.Sessions.IsAutomating(f.Browser.Id));
    }

    [Fact]
    public async Task ApprovedOneClickApplicationUsesTheRecordedSubmitAction()
    {
        await using var f = await Fixture.CreateAsync(true, scenario: "one-click");
        f.Definition.VacancySource = "saved";
        f.Definition.ApplicationStatusFilter = WorkflowDefinition.ApprovedStatusFilter;
        f.Definition.CheckMail = false;
        var vacancy = new Vacancy { JobPlatformId = f.Platform.Id, ExternalId = "1", Title = "C# Engineer", Url = f.BaseUrl + "/jobs/1" };
        f.Db.AddRange(vacancy, new JobApplication
        {
            VacancyId = vacancy.Id, CandidateProfileId = f.Definition.CandidateProfileId,
            ResumeVersionId = f.Definition.ResumeVersionId, State = ApplicationState.ReadyToSubmit
        });
        await f.Db.SaveChangesAsync();
        f.Agent.BrowserOverride = input =>
        {
            var task = input.GetProperty("task");
            if (!task.TryGetProperty("submissionAuthorized", out var authorized)) return FixtureAgent.Action("finish");
            Assert.True(authorized.GetBoolean());
            Assert.Contains("never \"click\"", task.GetProperty("task").GetString());
            var button = input.GetProperty("page").GetProperty("controls").EnumerateArray()
                .Single(x => x.GetProperty("label").GetString() == "Candidatar-me");
            Assert.False(button.GetProperty("isSubmit").GetBoolean());
            Assert.True(button.GetProperty("maySubmit").GetBoolean());
            return FixtureAgent.Action("submit", button.GetProperty("id").GetString()!);
        };

        var result = await new JobSearchWorkflow(f.Db, f.Agent, f.Driver, monitorWithinSearch: false).RunAsync(f.Definition.Id, default);
        Assert.Equal(1, result.Submitted);
        Assert.Equal(1, f.Submissions);
        Assert.True(f.PendingWasDurable);
        Assert.Equal("Confirmed", (await f.Db.Set<ApplicationAttempt>().SingleAsync()).Outcome);
    }

    [Fact]
    public async Task SubmissionTimeoutDoesNotCauseRecoveryOrDuplicateSubmission()
    {
        await using var f = await Fixture.CreateAsync(true, scenario: "slow-submit");
        f.Agent.BeforeSubmit = () => f.Context.Pages.Last().SetDefaultTimeout(1000);
        f.Definition.CheckMail = false;
        var workflow = new JobSearchWorkflow(f.Db, f.Agent, f.Driver, monitorWithinSearch: false);
        var result = await workflow.RunAsync(f.Definition.Id, default);
        Assert.Equal(1, f.Submissions);
        Assert.Equal(1, result.NeedsAttention);
        Assert.Equal(ApplicationState.SubmissionUnknown, (await f.Db.Applications.SingleAsync()).State);
        Assert.Contains("/api/browser-diagnostics/", (await f.Db.Set<ApplicationAttempt>().SingleAsync()).Evidence);
        Assert.Empty(await f.Db.Audit.Where(x => x.Action == "Browser/apply/ActionFailed").ToListAsync());
        await workflow.RunAsync(f.Definition.Id, default);
        Assert.Equal(1, f.Submissions);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AuthenticatedBrowserSearchUploadSubmitReceiptMailAndRestartAreConnected(bool receipt)
    {
        await using var fixture = await Fixture.CreateAsync(receipt, options: new() { VisualRecoveryLimit = 3 });
        var workflow = new JobSearchWorkflow(fixture.Db, fixture.Agent, fixture.Driver, fixture.Mail);
        var result = await workflow.RunAsync(fixture.Definition.Id, default);
        Assert.True(fixture.Submissions == 1, result.Error + " | " + result.AttentionDetails);
        Assert.True(fixture.PendingWasDurable);
        Assert.True(fixture.ResumeWasUploaded);
        Assert.Contains("C#", fixture.SentLetter);
        Assert.Equal(receipt ? 1 : 0, result.Submitted);
        Assert.Equal(receipt ? "Succeeded" : "NeedsAttention", result.Status);
        var application = await fixture.Db.Applications.SingleAsync();
        Assert.Equal(receipt ? ApplicationState.EmployerResponded : ApplicationState.SubmissionUnknown, application.State);
        Assert.Equal(receipt ? "Invitation" : "", application.ResponseStatus);
        var attempt = await fixture.Db.Set<ApplicationAttempt>().SingleAsync();
        Assert.Equal(receipt ? "Confirmed" : "SubmissionUnknown", attempt.Outcome);
        if (receipt)
        {
            Assert.Single(await fixture.Db.Set<EmployerInteraction>().ToListAsync());
            Assert.Contains("Reference", attempt.Evidence);
        }
        await workflow.RunAsync(fixture.Definition.Id, default);
        Assert.Equal(1, fixture.Submissions);
        Assert.Single(await fixture.Db.Set<ApplicationAttempt>().ToListAsync());
        Assert.Single(await fixture.Db.Applications.ToListAsync());
        Assert.True(fixture.Agent.SawFullDescription);
        Assert.Equal(fixture.Definition.FreshnessDays, fixture.Agent.EvaluationAgeLimit);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AmbiguousEvaluationIsNotAutomaticallySubmitted(bool contradictoryProceed)
    {
        await using var fixture = await Fixture.CreateAsync(true);
        fixture.Agent.Decision = contradictoryProceed ? "Proceed" : "NeedsReview";
        if (contradictoryProceed) fixture.Agent.UnverifiedRequirements = ["Management experience is unknown"];
        await new JobSearchWorkflow(fixture.Db, fixture.Agent, fixture.Driver, fixture.Mail).RunAsync(fixture.Definition.Id, default);
        Assert.Equal(0, fixture.Submissions);
        Assert.Equal(ApplicationState.ReadyForReview, (await fixture.Db.Applications.SingleAsync()).State);
        Assert.Empty(await fixture.Db.Set<ApplicationAttempt>().ToListAsync());
    }

    [Fact]
    public async Task PlausibleSeniorVacancyWithUnverifiedRequirementsIsSavedForReview()
    {
        await using var f = await Fixture.CreateAsync(true);
        var profile = await f.Db.Profiles.SingleAsync();
        profile.Languages = "English B1-B2";
        profile.Dossier = "Personal project using AI tools; duration and US timezone availability are not specified.";
        await f.Db.SaveChangesAsync();
        f.Agent.Decision = "NeedsReview";
        f.Agent.UnverifiedRequirements = ["Confirm six months of parallel AI-agent work", "Confirm English B2", "Confirm US timezone overlap"];
        f.Agent.BrowserOverride = root =>
        {
            Assert.Equal("search", root.GetProperty("phase").GetString());
            var link = root.GetProperty("page").GetProperty("controls").EnumerateArray()
                .Single(x => x.GetProperty("href").GetString()!.Contains("/jobs/1")).GetProperty("href").GetString()!;
            return FixtureAgent.Action("finish", vacancies: [new { title = "Senior Agentic Engineer", employer = "Synthetic", url = link, description = "Potential C# fit; English and experience duration need confirmation." }]);
        };

        var run = await new JobSearchWorkflow(f.Db, f.Agent, f.Driver, monitorWithinSearch: false).RunAsync(f.Definition.Id, default);

        Assert.Equal("Succeeded", run.Status);
        Assert.Equal(1, run.Prepared);
        Assert.Equal(0, run.Rejected);
        Assert.Equal(0, f.Submissions);
        Assert.True(f.Agent.SawFullDescription);
        Assert.Equal("Senior Agentic Engineer", (await f.Db.Vacancies.SingleAsync()).Title);
        Assert.Equal(ApplicationState.ReadyForReview, (await f.Db.Applications.SingleAsync()).State);
        var evaluation = JsonSerializer.Deserialize<EvaluationResult>((await f.Db.Set<VacancyEvaluation>().SingleAsync()).ResultJson, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal("NeedsReview", evaluation.Decision);
        Assert.Equal(f.Agent.UnverifiedRequirements, evaluation.UnverifiedRequirements);
        Assert.Equal("C#", Assert.Single(evaluation.Matches));
        Assert.Single(await f.Db.Set<Notification>().Where(x => x.Kind == "Approval").ToListAsync());
        Assert.Empty(await f.Db.Set<ApplicationAttempt>().ToListAsync());
        Assert.Equal("English B1-B2", profile.Languages);
        Assert.Contains(f.Agent.Requests, x => x.Instructions.Contains(VacancyAssessmentPolicy.Search));
        Assert.Contains(f.Agent.Requests, x => x.Instructions == VacancyAssessmentPolicy.Evaluation);
    }

    [Fact]
    public async Task UnsupportedEvaluationIsSavedAndBlocksAutoSubmissionBeforePreparation()
    {
        await using var fixture = await Fixture.CreateAsync(true);
        fixture.Agent.EvaluationMatches = ["CSharp", "Kubernetes"];
        var run = await new JobSearchWorkflow(fixture.Db, fixture.Agent, fixture.Driver, monitorWithinSearch: false).RunAsync(fixture.Definition.Id, default);
        Assert.Equal("NeedsAttention", run.Status);
        Assert.Equal(1, run.NeedsAttention);
        Assert.Equal(0, run.Prepared);
        Assert.Contains("Kubernetes", run.Error);
        Assert.Equal(0, fixture.Submissions);
        Assert.Equal(ApplicationState.Blocked, (await fixture.Db.Applications.SingleAsync()).State);
        Assert.Empty(await fixture.Db.Set<ApplicationAttempt>().ToListAsync());
        var row = await fixture.Db.Set<VacancyEvaluation>().SingleAsync();
        using var json = JsonDocument.Parse(row.ResultJson);
        Assert.Equal("NeedsReview", json.RootElement.GetProperty("decision").GetString());
        Assert.Equal("Proceed", json.RootElement.GetProperty("validation").GetProperty("agentDecision").GetString());
        Assert.False(json.RootElement.GetProperty("validation").GetProperty("isValid").GetBoolean());
        Assert.Contains("Kubernetes", json.RootElement.GetProperty("rationale").GetString());
    }

    [Fact]
    public async Task BrowserCanSearchUsingAPostFormBeforeApplying()
    {
        await using var fixture = await Fixture.CreateAsync(true, true);
        await new JobSearchWorkflow(fixture.Db, fixture.Agent, fixture.Driver, fixture.Mail).RunAsync(fixture.Definition.Id, default);
        Assert.Equal(1, fixture.SearchPosts);
        Assert.Equal(1, fixture.Submissions);
        Assert.True(fixture.PendingWasDurable);
    }

    [Fact]
    public async Task DuplicatePlatformEntriesCannotSendTheSameVacancyTwice()
    {
        await using var fixture = await Fixture.CreateAsync(true);
        fixture.Definition.CheckMail = false;
        var duplicate = new JobPlatform { Name = "Same board, another query", Adapter = "browser", Url = fixture.Platform.Url };
        fixture.Definition.JobPlatformIds = [fixture.Platform.Id, duplicate.Id];
        fixture.Db.Add(duplicate); await fixture.Db.SaveChangesAsync();
        var result = await new JobSearchWorkflow(fixture.Db, fixture.Agent, fixture.Driver).RunAsync(fixture.Definition.Id, default);
        Assert.Equal(1, fixture.Submissions);
        Assert.Equal(1, result.Submitted);
        Assert.Equal(1, result.NeedsAttention);
        Assert.Single(await fixture.Db.Set<ApplicationAttempt>().ToListAsync());
    }

    [Fact]
    public async Task LoginLossStopsBeforeCreatingAnAttempt()
    {
        await using var fixture = await Fixture.CreateAsync(true);
        await using (var lease = await fixture.Sessions.AcquireAsync(fixture.Browser.Id, default))
            await lease.Page.Context.ClearCookiesAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => new JobSearchWorkflow(fixture.Db, fixture.Agent, fixture.Driver, fixture.Mail).RunAsync(fixture.Definition.Id, default));
        Assert.Equal(0, fixture.Submissions);
        Assert.Empty(await fixture.Db.Set<ApplicationAttempt>().ToListAsync());
    }

    [Fact]
    public async Task InterruptedPendingSubmissionIsReconciledWithoutAnotherClick()
    {
        await using var fixture = await Fixture.CreateAsync(true);
        fixture.Definition.CheckMail = false;
        var vacancy = new Vacancy { JobPlatformId = fixture.Platform.Id, ExternalId = "1", Url = fixture.BaseUrl + "/jobs/1", Title = "Engineer" };
        var application = new JobApplication { CandidateProfileId = fixture.Definition.CandidateProfileId,
            WorkflowDefinitionId = fixture.Definition.Id, VacancyId = vacancy.Id, ResumeVersionId = fixture.Definition.ResumeVersionId,
            State = ApplicationState.Submitting };
        fixture.Db.AddRange(vacancy, application, new ApplicationAttempt { JobApplicationId = application.Id,
            IdempotencyKey = VacancyIdentity.SubmissionKey(application.CandidateProfileId, vacancy.Url), Outcome = "Pending" });
        await fixture.Db.SaveChangesAsync();
        await new JobSearchWorkflow(fixture.Db, fixture.Agent, fixture.Driver).RunAsync(fixture.Definition.Id, default);
        Assert.Equal(0, fixture.Submissions);
        Assert.Equal(ApplicationState.SubmissionUnknown, application.State);
        Assert.Equal("SubmissionUnknown", (await fixture.Db.Set<ApplicationAttempt>().SingleAsync()).Outcome);
    }

    [Fact]
    public async Task BrowserProfileCannotBeControlledOrClosedByTwoRunsAtOnce()
    {
        await using var fixture = await Fixture.CreateAsync(true);
        await using var lease = await fixture.Sessions.AcquireAsync(fixture.Browser.Id, default);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Sessions.AcquireAsync(fixture.Browser.Id, default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Sessions.CloseAsync(fixture.Browser.Id));
    }

    [Theory]
    [InlineData("old")]
    [InlineData("unknown")]
    [InlineData("closed")]
    [InlineData("closed-with-open-quote")]
    [InlineData("closed-without-quote")]
    [InlineData("fabricated")]
    [InlineData("invalid-id")]
    public async Task UnverifiedOrClosedVacancyCannotBeSubmitted(string fault)
    {
        await using var fixture = await Fixture.CreateAsync(true, scenario: fault == "closed" ? "closed" : "");
        fixture.Agent.FreshnessFault = fault;
        var result = await new JobSearchWorkflow(fixture.Db, fixture.Agent, fixture.Driver, fixture.Mail).RunAsync(fixture.Definition.Id, default);
        Assert.Equal(0, fixture.Submissions);
        // A confirmed closed vacancy is skipped; uncertain evidence requires review.
        Assert.Equal(fault == "closed" ? 0 : 1, result.NeedsAttention);
        if (fault == "closed") Assert.Equal(1, result.Skipped);
        if (fault is "closed-with-open-quote" or "closed-without-quote")
            Assert.Equal("Unknown", (await fixture.Db.Vacancies.SingleAsync()).Availability);
        Assert.Empty(await fixture.Db.Set<ApplicationAttempt>().ToListAsync());
    }

    [Fact]
    public async Task SearchRoleDoesNotReadMailEvenWhenLegacyCheckMailIsSet()
    {
        await using var fixture = await Fixture.CreateAsync(true);
        var result = await new JobSearchWorkflow(fixture.Db, fixture.Agent, fixture.Driver, fixture.Mail, monitorWithinSearch: false).RunAsync(fixture.Definition.Id, default);
        Assert.Equal(1, result.Submitted);
        Assert.Equal(ApplicationState.Submitted, (await fixture.Db.Applications.SingleAsync()).State);
        Assert.Empty(await fixture.Db.Set<EmployerInteraction>().ToListAsync());
    }

    [Fact]
    public async Task AdaptedPdfIsTheFileActuallyUploadedByTheBrowser()
    {
        await using var fixture = await Fixture.CreateAsync(true);
        fixture.Definition.ResumeMode = "tailor";
        fixture.Definition.ResumeAgentId = fixture.Definition.BrowserAgentId;
        var source = await fixture.Db.ResumeVersions.SingleAsync();
        var bytes = await ResumeAgentService.RenderAsync("Synthetic Candidate", [("Experience", "C# SQL development")], default);
        await File.WriteAllBytesAsync(ResumeAgentService.PathFor(source), bytes);
        await fixture.Db.SaveChangesAsync();
        var run = await new JobSearchWorkflow(fixture.Db, fixture.Agent, fixture.Driver, fixture.Mail).RunAsync(fixture.Definition.Id, default);
        Assert.True(run.Submitted == 1, run.Error);
        var application = await fixture.Db.Applications.SingleAsync();
        var generated = await fixture.Db.ResumeVersions.SingleAsync(x => x.Id == application.ResumeVersionId);
        Assert.Equal("AgentGenerated", generated.Origin);
        Assert.Equal(generated.Sha256, fixture.UploadedSha256);
        Assert.NotEqual(source.Id, generated.Id);
        Assert.Equal(1, fixture.Submissions);
    }

    [Fact]
    public async Task WorkerUsesTheApiBrowserHostAndRecordsTheWholeRun()
    {
        await using var fixture = await Fixture.CreateAsync(true);
        var previous = Environment.GetEnvironmentVariable("JOBFLOW_API_URL");
        Environment.SetEnvironmentVariable("JOBFLOW_API_URL", fixture.BaseUrl);
        try
        {
            fixture.Db.Add(new ScheduledTask { CandidateProfileId = fixture.Definition.CandidateProfileId,
                Kind = "Workflow", WorkflowDefinitionId = fixture.Definition.Id, RunRequested = true, TimeoutSeconds = 120 });
            await fixture.Db.SaveChangesAsync();
            Assert.True(await new TaskProcessor(fixture.Db).RunOneAsync(default));
            Assert.Equal("Succeeded", (await fixture.Db.Runs.SingleAsync()).Status);
            Assert.Equal(1, fixture.ApiRuns);
            Assert.Equal(1, fixture.Submissions);
            Assert.Equal(ApplicationState.EmployerResponded, (await fixture.Db.Applications.SingleAsync()).State);
        }
        finally { Environment.SetEnvironmentVariable("JOBFLOW_API_URL", previous); }
    }

    [Fact]
    public async Task LanBrowserControlsSupportManualLoginAndScreenshots()
    {
        await using var fixture = await Fixture.CreateAsync(true);
        var remoteProfile = new BrowserProfile { Name = "LAN login" };
        fixture.Db.Add(remoteProfile); await fixture.Db.SaveChangesAsync();
        await using var remoteSessions = new BrowserSessions(true);
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder.UseSetting("Services:UseWorkerBrowser", "false").ConfigureServices(services =>
        {
            services.RemoveAll<BrowserSessions>();
            services.AddSingleton(remoteSessions);
        }));
        using var client = factory.CreateClient(new() { BaseAddress = new("http://192.168.100.16:5271") });
        client.DefaultRequestHeaders.Add("X-JobFlow-Client", "local-ui");
        var prefix = "/api/browser-profiles/" + remoteProfile.Id;
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync(prefix + "/open", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync(prefix + "/control", new { action = "navigate", value = fixture.BaseUrl + "/login" })).StatusCode);
        var sessions = factory.Services.GetRequiredService<BrowserSessions>();
        var page = sessions.GetManualPage(remoteProfile.Id);
        var bounds = (await page.GetByLabel("Login password").BoundingBoxAsync())!;
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync(prefix + "/control", new { action = "click", x = bounds.X + 5, y = bounds.Y + 5 })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync(prefix + "/control", new { action = "text", value = "synthetic-password" })).StatusCode);
        Assert.Equal("synthetic-password", await page.GetByLabel("Login password").InputValueAsync());
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync(prefix + "/control", new { action = "key", value = "Enter" })).StatusCode);
        await page.WaitForURLAsync("**/search");
        Assert.Contains("C# Engineer", await page.Locator("body").InnerTextAsync());
        using var screenshot = await client.GetAsync(prefix + "/screen?tab=0");
        Assert.Equal("image/jpeg", screenshot.Content.Headers.ContentType!.MediaType);
        Assert.True((await screenshot.Content.ReadAsByteArrayAsync()).Length > 500);
        var forbidden = await factory.Server.SendAsync(context =>
        {
            context.Connection.RemoteIpAddress = IPAddress.Parse("192.168.100.20");
            context.Request.Method = "POST";
            context.Request.Headers["X-JobFlow-Client"] = "local-ui";
            context.Request.Host = new HostString("192.168.100.16:5271");
            context.Request.Path = "/api/internal/workflows/" + fixture.Definition.Id + "/browser-run";
        });
        Assert.Equal(403, forbidden.Response.StatusCode);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "jobflow-browser-workflow-" + Guid.NewGuid().ToString("N"));
        private readonly string? previous = Environment.GetEnvironmentVariable("JOBFLOW_DATA_DIR");
        private WebApplication server = null!;
        public JobFlowDb Db { get; private set; } = null!;
        public BrowserSessions Sessions { get; } = new(true);
        public BrowserWorkflowDriver Driver { get; private set; } = null!;
        public FixtureAgent Agent { get; private set; } = null!;
        public MailHandler Mail { get; private set; } = null!;
        public WorkflowDefinition Definition { get; private set; } = null!;
        public JobPlatform Platform { get; private set; } = null!;
        public BrowserProfile Browser { get; } = new() { Name = "Authenticated fixture browser" };
        public string BaseUrl => server.Urls.Single();
        public int Submissions { get; private set; }
        public int ApiRuns { get; private set; }
        public int SearchPosts { get; private set; }
        public bool PendingWasDurable { get; private set; }
        public bool ResumeWasUploaded { get; private set; }
        public string UploadedSha256 { get; private set; } = "";
        public string SentLetter { get; private set; } = "";
        public IBrowserContext Context { get; private set; } = null!;
        public int SearchPageLoads { get; private set; }
        public Action? SearchRequested { get; set; }
        public int FilterReads { get; private set; }

        public static async Task<Fixture> CreateAsync(bool receipt, bool postSearch = false, string scenario = "", BrowserWorkflowOptions? options = null)
        {
            var f = new Fixture(); Directory.CreateDirectory(f.root);
            Environment.SetEnvironmentVariable("JOBFLOW_DATA_DIR", f.root);
            var connection = $"Data Source={Path.Combine(f.root, "jobflow.db")};Pooling=False";
            f.Db = new(new DbContextOptionsBuilder<JobFlowDb>().UseSqlite(connection).Options);
            await f.Db.Database.MigrateAsync();
            var builder = WebApplication.CreateBuilder(); builder.WebHost.UseUrls("http://127.0.0.1:0");
            f.server = builder.Build();
            f.server.MapGet("/login", () => Results.Content("<form action='/login' method='post'><label>Login password<input name='password' type='password' required></label><button>Sign in</button></form>", "text/html"));
            f.server.MapPost("/login", (HttpContext context) =>
            {
                context.Response.Cookies.Append("session", "logged-in", new() { HttpOnly = true });
                return Results.Redirect("/search");
            });
            f.server.MapPost("/api/internal/workflows/{id:guid}/browser-run", async (Guid id, HttpContext context) =>
            {
                Assert.Equal("local-ui", context.Request.Headers["X-JobFlow-Client"].ToString());
                f.ApiRuns++;
                await using var hostDb = new JobFlowDb(new DbContextOptionsBuilder<JobFlowDb>().UseSqlite(connection).Options);
                return Results.Ok(await new JobSearchWorkflow(hostDb, f.Agent, new BrowserWorkflowDriver(f.Sessions, f.Agent, hostDb), f.Mail).RunAsync(id, context.RequestAborted));
            });
            f.server.MapGet("/search", async (HttpRequest request) =>
            {
                f.SearchPageLoads++;
                f.SearchRequested?.Invoke();
                if (scenario == "slow-always" || scenario == "slow-first" && f.SearchPageLoads == 1) await Task.Delay(5000);
                var modal = scenario == "modal" ?
                    string.Concat(Enumerable.Range(0, 300).Select(i => $"<a href='/background/{i}'>Background {i}</a>")) +
                    """<div data-qa='modal-overlay' style='position:fixed;inset:0;background:white;z-index:999'><section role='dialog' aria-modal='true'><p>Choose region</p><button onclick="this.closest('[data-qa]').remove();document.querySelectorAll('a').forEach(el=>{if(el.getAttribute('href').startsWith('/background'))el.remove()})">Close dialog</button></section></div>""" : "";
                var settings = scenario == "custom-settings" ? """
                    <meta charset="utf-8">
                    <div id="period" tabindex="0" data-qa="search-period-menu" onclick="document.querySelector('#period-options').hidden=false">За всё время</div>
                    <div id="sort" role="combobox" tabindex="0" aria-haspopup="dialog" onclick="document.querySelector('#sort-options').hidden=false">По соответствию</div>
                    <div id="period-options" role="dialog" hidden><label><input type="radio" style="display:none" name="period" value="3" onchange="filter('period','3')">За три дня</label></div>
                    <div id="sort-options" role="dialog" hidden><div style="cursor:pointer" onclick="filter('order','newest')"><span>По дате публикации</span></div></div>
                    <script>async function filter(key,value){document.body.setAttribute('aria-busy','true');await fetch('/filter?'+key+'='+value);const url=new URL(location.href);url.searchParams.set(key,value);history.replaceState(null,'',url);document.querySelector('#'+(key==='period'?'period':'sort')).textContent=key==='period'?'За три дня':'По дате публикации';document.querySelectorAll('[role=dialog]').forEach(x=>x.hidden=true);document.body.removeAttribute('aria-busy');}</script>
                    """ : "";
                if (scenario == "frame-detached") settings = """
                    <iframe src="/snapshot-frame"></iframe>
                    <script>
                      const setAttribute = Element.prototype.setAttribute;
                      Element.prototype.setAttribute = function(name, value) {
                        if (name === 'data-jobflow-agent-id') {
                          Element.prototype.setAttribute = setAttribute;
                          document.querySelector('iframe').remove();
                          document.body.append('Frame removed during observation');
                        }
                        return setAttribute.call(this, name, value);
                      };
                    </script>
                    """;
                if (scenario == "visual") settings = """
                    <style>body{min-height:2400px}</style><button type='button'>No effect</button>
                    <div id='visual-action' style='position:fixed;left:100px;top:150px;width:220px;height:40px;background:#ccc'
                      onclick="this.textContent='Visual recovery worked'">Open visual control</div>
                    <form action='/search' method='post'><button id='visual-submit' type='submit'>Search</button></form>
                    """;
                return Results.Content(!LoggedIn(request) ?
                "<h1>Sign in required</h1><input type='password'>" : postSearch ?
                "<form action='" + (scenario == "post-only" ? "/post-result" : "/search") + "' method='post'><label>Search query<input name='query'></label><button type='submit'>Search</button></form>" :
                "<h1>Jobs</h1><a href='/jobs/1'>C# Engineer</a>" + (scenario == "multiple" ? "<a href='/jobs/2'>Second C# Engineer</a>" : "") + modal + settings, "text/html");
            });
            f.server.MapGet("/filter", async () => { f.FilterReads++; await Task.Delay(600); return Results.Ok(); });
            f.server.MapGet("/snapshot-frame", () => Results.Content("<p>Child frame</p>", "text/html"));
            f.server.MapGet("/jobs/2", () => Results.Content("<h1>Second C# Engineer</h1><p>Published today. Full requirement: C#.</p>", "text/html"));
            f.server.MapPost(scenario == "post-only" ? "/post-result" : "/search", async (HttpRequest request) =>
            {
                f.SearchPosts++;
                Assert.Contains("C#", (await request.ReadFormAsync())["query"].ToString());
                return Results.Content("<h1>Search results</h1><a href='/jobs/1'>C# Engineer</a>", "text/html");
            }).DisableAntiforgery();
            f.server.MapGet("/jobs/1", (HttpRequest request) => Results.Content(LoggedIn(request) ?
                "<h1>C# Engineer</h1>" + (scenario == "conflicting-dates" ?
                    $"<p>Published {DateTime.UtcNow.AddDays(-2):yyyy-MM-dd}</p><script type='application/ld+json'>{{\"@type\":\"JobPosting\",\"datePosted\":\"{DateTime.UtcNow.AddDays(-1):O}\"}}</script>" :
                    scenario == "metadata-date" ? $"<script type='application/ld+json'>{{\"@type\":\"JobPosting\",\"datePosted\":\"{DateTime.UtcNow.AddDays(-1):yyyy-MM-ddTHH:mm:ss.ffffff}\"}}</script>" : "<p>Published today</p>") +
                    (scenario == "closed" ? "<p>No longer accepting applications</p>" : "") +
                "<p>Full requirement: C# and SQL development. Work on the synthetic team's application.</p>" +
                    (scenario == "one-click"
                        ? "<button type='button' onclick=\"fetch('/submit-one-click',{method:'POST'}).then(r=>r.text()).then(t=>document.body.textContent=t)\">Candidatar-me</button>"
                        : "<a href='/apply/1'>Apply for this role</a>") : "<h1>Sign in required</h1>", "text/html"));
            f.server.MapPost("/submit-one-click", async (HttpRequest request) =>
            {
                Assert.True(LoggedIn(request));
                f.Submissions++;
                await using var auditDb = new JobFlowDb(new DbContextOptionsBuilder<JobFlowDb>().UseSqlite(connection).Options);
                f.PendingWasDurable = await auditDb.Set<ApplicationAttempt>().AnyAsync(x => x.Outcome == "Pending");
                return Results.Text("Application submitted. Reference ONE-CLICK-FIXTURE.");
            });
            f.server.MapGet("/apply/1", () => Results.Content("""
                <h1>Apply to C# Engineer</h1><form action='/submit/1' method='post' enctype='multipart/form-data'>
                <label>Full name<input name='fullName' autocomplete='name' required></label>
                <label>Email<input name='email' type='email' required></label>
                <label>Cover letter<textarea name='letter' required></textarea></label>
                <label>Resume<input name='resume' type='file' accept='.pdf' required></label>
                <label>Application data consent<input name='consent' type='checkbox' required></label>
                <button type='submit'>Send application</button></form>
                """, "text/html"));
            f.server.MapPost("/submit/1", async (HttpRequest request) =>
            {
                Assert.True(LoggedIn(request));
                f.Submissions++;
                await using var auditDb = new JobFlowDb(new DbContextOptionsBuilder<JobFlowDb>().UseSqlite(connection).Options);
                f.PendingWasDurable = await auditDb.Set<ApplicationAttempt>().AnyAsync(x => x.Outcome == "Pending") &&
                    await auditDb.Applications.AnyAsync(x => x.State == ApplicationState.Submitting);
                var form = await request.ReadFormAsync();
                f.ResumeWasUploaded = form.Files.GetFile("resume")?.Length > 5;
                if (form.Files.GetFile("resume") is { } sentPdf) { await using var content = sentPdf.OpenReadStream(); f.UploadedSha256 = Convert.ToHexString(await System.Security.Cryptography.SHA256.HashDataAsync(content)); }
                f.SentLetter = form["letter"].ToString();
                Assert.Equal("Synthetic Candidate", form["fullName"].ToString());
                Assert.Equal("synthetic@example.invalid", form["email"].ToString());
                if (scenario == "slow-submit") await Task.Delay(2200);
                return Results.Content(receipt ? "<h1>Application submitted</h1><p>Reference REAL-HTTP-FIXTURE-1</p>" :
                    "<h1>Request processing</h1><p>No confirmation was returned.</p>", "text/html");
            }).DisableAntiforgery();
            await f.server.StartAsync();
            var user = new User(); var candidate = new CandidateProfile { UserId = user.Id, Name = "Synthetic Candidate", Email = "synthetic@example.invalid", Skills = "C#" };
            var resume = new Resume { CandidateProfileId = candidate.Id };
            var version = new ResumeVersion { ResumeId = resume.Id, Version = 1, StorageKey = "fixture.pdf" };
            Directory.CreateDirectory(Path.Combine(f.root, "resumes"));
            await File.WriteAllTextAsync(Path.Combine(f.root, "resumes", version.StorageKey), "%PDF-1.4\nSynthetic fixture resume\n%%EOF");
            var provider = new ModelProvider { Enabled = true };
            var browserAgent = new AgentProfile { CandidateProfileId = candidate.Id, ModelProviderId = provider.Id, TaskType = "BrowserAutomation", Enabled = true };
            var evaluator = new AgentProfile { CandidateProfileId = candidate.Id, ModelProviderId = provider.Id, TaskType = "Evaluation", Enabled = true };
            f.Platform = new() { Name = "Synthetic Board", Adapter = "browser", Url = f.BaseUrl + "/search", SearchFilters = "C# Engineer" };
            f.Definition = new() { Name = "Browser search and apply", CandidateProfileId = candidate.Id, JobPlatformId = f.Platform.Id,
                ResumeVersionId = version.Id, BrowserProfileId = f.Browser.Id, BrowserAgentId = browserAgent.Id, EvaluationAgentId = evaluator.Id,
                ExecutionMode = "browser", AutoSubmit = true, CheckMail = true, Enabled = true, MaxBrowserSteps = 20 };
            var gmail = new Integration { Name = "Fixture Gmail", CandidateProfileId = candidate.Id, Kind = "gmail", Enabled = true,
                Endpoint = "https://gmail.googleapis.com/gmail/v1/users/me/messages?q=job" };
            f.Db.AddRange(user, candidate, resume, version, provider, browserAgent, evaluator, f.Platform, f.Definition, f.Browser, gmail);
            await f.Db.SaveChangesAsync();
            f.Agent = new(evaluator.Id, f.Db);
            f.Driver = new(f.Sessions, f.Agent, f.Db, options ?? new() { VisualRecoveryLimit = 0 });
            f.Mail = new(f, receipt);
            await using (var lease = await f.Sessions.AcquireAsync(f.Browser.Id, default))
            {
                f.Context = lease.Page.Context;
                await lease.Page.Context.AddCookiesAsync([new() { Name = "session", Value = "logged-in", Url = f.BaseUrl, Expires = DateTimeOffset.UtcNow.AddDays(1).ToUnixTimeSeconds() }]);
            }
            return f;
        }

        private static bool LoggedIn(HttpRequest request) => request.Cookies["session"] == "logged-in";
        public async ValueTask DisposeAsync()
        {
            await Sessions.DisposeAsync();
            await server.StopAsync(); await server.DisposeAsync();
            Mail.Dispose(); await Db.DisposeAsync();
            Environment.SetEnvironmentVariable("JOBFLOW_DATA_DIR", previous);
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            // Chromium can release its Windows debug log shortly after CloseAsync returns.
            for (var attempt = 0; ; attempt++)
            {
                try { Directory.Delete(root, true); break; }
                catch (IOException) when (attempt < 19) { await Task.Delay(250); }
            }
        }
    }

    private sealed class FixtureAgent(Guid evaluator, JobFlowDb db) : IAgentExecutor
    {
        public List<AgentExecutionRequest> Requests { get; } = [];
        public Func<JsonElement, Task<AgentExecutionResult>>? BrowserOverride { get; set; }
        public Func<AgentExecutionRequest, Task<AgentExecutionResult>>? VisualOverride { get; set; }
        public Action? BeforeSubmit { get; set; }
        public string FreshnessFault { get; set; } = "";
        public string Decision { get; set; } = "Proceed";
        public string[] EvaluationMatches { get; set; } = ["C#"];
        public string[] UnverifiedRequirements { get; set; } = [];
        public bool SawFullDescription { get; private set; }
        public int? EvaluationAgeLimit { get; private set; }
        public Task<AgentExecutionResult> ExecuteAsync(AgentExecutionRequest request, CancellationToken ct)
        {
            Requests.Add(request);
            using var input = JsonDocument.Parse(request.Input);
            var root = input.RootElement;
            if (request.OutputSchema == BrowserWorkflowDriver.VisualActionSchema && VisualOverride != null) return VisualOverride(request);
            if (request.OutputSchema == ResumeAgentService.SelectionSchema || request.OutputSchema == ResumeAgentService.AdaptSchema || request.OutputSchema == ResumeAgentService.VerifySchema)
            {
                var execution = new AgentExecution { AgentProfileId = request.AgentProfileId, Status = "Succeeded" }; db.Add(execution); db.SaveChanges();
                var result = request.OutputSchema == ResumeAgentService.SelectionSchema
                    ? JsonSerializer.Serialize(new { resumeVersionId = root.GetProperty("resumes")[0].GetProperty("resumeVersionId").GetString(), reason = "Matching C# facts" })
                    : request.OutputSchema == ResumeAgentService.AdaptSchema
                        ? JsonSerializer.Serialize(new { title = "Synthetic Candidate", sections = new[] { new { heading = "Experience", claims = new[] { new { text = "C# SQL development", source = "sourceResume", quote = "C# SQL development" } } } } })
                        : "{\"supported\":true,\"issues\":[]}";
                return Task.FromResult(new AgentExecutionResult(execution.Id, "Succeeded", result));
            }
            if (request.OutputSchema?.Contains("publicationEvidence") == true)
                return Result(JsonSerializer.Serialize(new { publishedAt = FreshnessFault == "unknown" ? "" : DateTime.UtcNow.AddDays(FreshnessFault == "old" ? -30 : 0).ToString("O"),
                    publicationEvidence = FreshnessFault == "fabricated" ? "A date not present in the page" : "Published today", availability = FreshnessFault.StartsWith("closed", StringComparison.Ordinal) ? "Closed" : "Open",
                    availabilityEvidence = FreshnessFault == "invalid-id" ? "e999" : FreshnessFault == "closed-without-quote" ? "" : root.GetProperty("availabilityEvidenceChoices").EnumerateObject().First(x => x.Value.GetString() is "Apply for this role" or "Candidatar-me").Name }));
            if (request.AgentProfileId == evaluator)
            {
                EvaluationAgeLimit = root.GetProperty("freshnessDays").GetInt32();
                SawFullDescription = root.GetProperty("vacancy").GetProperty("description").GetString()!.Contains("Full requirement");
                return Result(JsonSerializer.Serialize(new { decision = Decision, rationale = "C# is verified", matches = EvaluationMatches,
                    missingSkills = Array.Empty<string>(), unverifiedRequirements = UnverifiedRequirements, clarifications = Array.Empty<string>(), confidence = "High" }));
            }
            var phase = root.GetProperty("phase").GetString();
            if (BrowserOverride != null) return BrowserOverride(root.Clone());
            var page = root.GetProperty("page");
            if (page.GetProperty("text").GetString()!.Contains("Sign in required")) return Action("blocked", reason: "Log in manually to the saved browser profile.");
            var controls = page.GetProperty("controls").EnumerateArray().ToArray();
            if (phase == "search")
            {
                var link = controls.FirstOrDefault(x => x.GetProperty("href").GetString()!.Contains("/jobs/1"));
                if (link.ValueKind != JsonValueKind.Object)
                {
                    var query = controls.First(x => x.GetProperty("label").GetString() == "Search query");
                    if (query.GetProperty("value").GetString() == "") return Action("fill", query.GetProperty("id").GetString()!, "C# Engineer");
                    return Action("click", controls.Single(x => x.GetProperty("label").GetString() == "Search").GetProperty("id").GetString()!);
                }
                return Action("finish", vacancies: [new { title = "C# Engineer", employer = "Synthetic", url = link.GetProperty("href").GetString()!, description = "C# job" }]);
            }
            if (phase == "confirmation") return Action("blocked", reason: "No verifiable receipt.");
            var apply = controls.FirstOrDefault(x => x.GetProperty("href").GetString()!.Contains("/apply/1"));
            if (apply.ValueKind == JsonValueKind.Object) return Action("click", apply.GetProperty("id").GetString()!);
            foreach (var field in controls)
            {
                var type = field.GetProperty("inputType").GetString();
                var target = field.GetProperty("id").GetString()!;
                var label = field.GetProperty("label").GetString();
                if (type == "checkbox" && !field.GetProperty("checked").GetBoolean()) return Action("check", target, "true");
                if (field.GetProperty("value").GetString() != "") continue;
                if (label == "Full name") return Action("fill", target, "Synthetic Candidate");
                if (type == "email") return Action("fill", target, "synthetic@example.invalid");
                if (type == "textarea") return Action("fill", target, root.GetProperty("task").GetProperty("coverLetter").GetString()!);
                if (type == "file") return Action("upload", target);
            }
            var submit = controls.Single(x => x.GetProperty("isSubmit").GetBoolean());
            BeforeSubmit?.Invoke();
            return Action("submit", submit.GetProperty("id").GetString()!);
        }
        public static Task<AgentExecutionResult> Action(string action, string target = "", string value = "", string reason = "", object[]? vacancies = null, string url = "") =>
            Result(JsonSerializer.Serialize(new { action, target, value, url, reason, vacancies = vacancies ?? [] }));
        private static Task<AgentExecutionResult> Result(string output) => Task.FromResult(new AgentExecutionResult(Guid.NewGuid(), "Succeeded", output));
    }

    private sealed class MailHandler(Fixture fixture, bool receipt) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Assert.Equal("gmail.googleapis.com", request.RequestUri!.Host);
            object payload;
            if (request.RequestUri.AbsolutePath.EndsWith("/messages"))
                payload = receipt && fixture.Submissions > 0 ? new { messages = new[] { new { id = "reply-1" } } } : (object)new { messages = Array.Empty<object>() };
            else
                payload = new { id = "reply-1", internalDate = DateTimeOffset.UtcNow.AddSeconds(1).ToUnixTimeMilliseconds().ToString(),
                    payload = new { mimeType = "text/plain", headers = new[] { new { name = "Subject", value = "Interview invitation" } },
                        body = new { data = Convert.ToBase64String(Encoding.UTF8.GetBytes("We invite you to an interview for " + fixture.BaseUrl + "/jobs/1")).TrimEnd('=').Replace('+', '-').Replace('/', '_') } } };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json") });
        }
    }
}
