using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using JobFlow.Application;
using JobFlow.Domain;
using JobFlow.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using JobFlow.Api;
using Microsoft.Playwright;

namespace JobFlow.IntegrationTests;

public class ExternalApplicationDraftTests
{
    [Fact]
    public async Task HeadHunterProbeWaitsForDelayedNativeFormWithoutReplayingClickOrSending()
    {
        await using var f = await Fixture.CreateAsync("hh-native");
        await using var run = await f.Driver.BeginRunAsync(f.Workflow, default);
        Assert.False(await f.Driver.PrepareExternalDraftAsync(f.Workflow, f.Platform, f.Candidate, f.Vacancy, f.Application, default));
        var entry = Assert.Single(f.Db.Audit.Local, x => x.Action == "Browser/external-probe/EntryObserved");
        using var observed = JsonDocument.Parse(entry.Details);
        Assert.Equal("FormOpened", observed.RootElement.GetProperty("outcome").GetString());
        Assert.Equal("https://hh.ru/applicant/vacancy_response", observed.RootElement.GetProperty("path").GetString());
        Assert.Equal(1, f.EntryClicks);
        Assert.Equal(0, f.Writes);
        Assert.Equal(0, f.Submissions);
    }

    [Theory]
    [InlineData("hh-restricted", false)]
    [InlineData("hh-stalled", true)]
    public async Task HeadHunterReportsVisibilityAndPersonalAttestationBeforeConsideringTheSubmitButton(string scenario, bool usedObservedLink)
    {
        await using var f = await Fixture.CreateAsync(scenario);
        await using var run = await f.Driver.BeginRunAsync(f.Workflow, default);
        var error = await Assert.ThrowsAsync<HumanReviewRequiredException>(() =>
            f.Driver.PrepareExternalDraftAsync(f.Workflow, f.Platform, f.Candidate, f.Vacancy, f.Application, default));
        Assert.Contains("видимость", error.Message);
        Assert.Contains("достоверность", error.Message);
        var entry = Assert.Single(f.Db.Audit.Local, x => x.Action == "Browser/external-probe/EntryObserved");
        Assert.Contains("VisibilityAndAttestationRequired", entry.Details);
        using var result = JsonDocument.Parse(entry.Details);
        Assert.Equal(usedObservedLink, result.RootElement.GetProperty("usedObservedLink").GetBoolean());
        Assert.Equal(1, f.EntryClicks);
        Assert.Equal(0, f.Writes);
        Assert.Equal(0, f.Submissions);
    }

    [Fact]
    public async Task HeadHunterIgnoresCollapsedVisibilityNoticeForPublicResume()
    {
        await using var f = await Fixture.CreateAsync("hh-public");
        await using var run = await f.Driver.BeginRunAsync(f.Workflow, default);
        var result = await f.Driver.CheckHeadHunterEntryAsync(f.Workflow, f.Platform, f.Vacancy, default);
        Assert.Equal("AttestationRequired", result.Outcome);
        Assert.Equal(0, f.Writes);
        Assert.Equal(0, f.Submissions);
    }

    [Fact]
    public async Task VisualRecoveryCanFollowAnEmployerReferralAndKeepTheFilledFormForReview()
    {
        await using var f = await Fixture.CreateAsync("link");
        f.Agent.ProbeBlocked = true;
        f.Agent.VisualOverride = async _ =>
        {
            var page = f.Context.Pages.Single(x => x.Url.StartsWith("https://board.example/"));
            var box = (await page.GetByText("Apply", new() { Exact = true }).BoundingBoxAsync())!;
            f.Agent.ProbeBlocked = false;
            return new(Guid.NewGuid(), "Succeeded", JsonSerializer.Serialize(new { action = "click", x = (int)(box.X + box.Width / 2),
                y = (int)(box.Y + box.Height / 2), value = "", deltaY = 0, reason = "Open employer referral" }));
        };
        await using (var run = await f.Driver.BeginRunAsync(f.Workflow, default))
            Assert.True(await f.Driver.PrepareExternalDraftAsync(f.Workflow, f.Platform, f.Candidate, f.Vacancy, f.Application, default));
        var page = f.Sessions.GetDraftPage(f.Application.Id);
        Assert.Equal("https://company.example/jobs/1", page.Url);
        Assert.Equal(f.Candidate.Email, await page.Locator("input[type=email]").InputValueAsync());
        Assert.True((await ExternalApplicationDraft.ReadAsync(f.Db, f.Application.Id))!.Verified);
        Assert.Equal(0, f.Submissions);
        Assert.Equal(0, f.Writes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task VisualRecoveryFillsAnExternalFieldButCannotSubmitTheExternalForm(bool trySubmit)
    {
        await using var f = await Fixture.CreateAsync("link");
        f.Agent.ExternalBlocked = true;
        f.Agent.VisualOverride = async request =>
        {
            Assert.NotNull(request.ImagePng);
            var page = f.Context.Pages.Single(x => x.Url.StartsWith("https://company.example/"));
            var box = (await page.Locator(trySubmit ? "button[type=submit]" : "input[name=fullName]").BoundingBoxAsync())!;
            f.Agent.ExternalBlocked = false;
            return new(Guid.NewGuid(), "Succeeded", JsonSerializer.Serialize(new { action = trySubmit ? "click" : "fill",
                x = (int)(box.X + box.Width / 2), y = (int)(box.Y + box.Height / 2), value = trySubmit ? "" : "Synthetic Candidate", deltaY = 0, reason = "Synthetic screenshot decision" }));
        };
        await using (var run = await f.Driver.BeginRunAsync(f.Workflow, default))
            Assert.True(await f.Driver.PrepareExternalDraftAsync(f.Workflow, f.Platform, f.Candidate, f.Vacancy, f.Application, default));
        var page = f.Sessions.GetDraftPage(f.Application.Id);
        Assert.Equal(trySubmit ? "" : "Synthetic Candidate", await page.Locator("input[name=fullName]").InputValueAsync());
        Assert.Equal(0, f.Submissions);
        Assert.Equal(0, f.Writes);
        Assert.Equal(ApplicationState.ReadyForReview, f.Application.State);
        Assert.True((await ExternalApplicationDraft.ReadAsync(f.Db, f.Application.Id))!.Verified);
        Assert.Equal(!trySubmit, await f.Db.Audit.AnyAsync(x => x.Action == "Browser/external-draft/VisualRecovery"));
    }

    [Theory]
    [InlineData("link")]
    [InlineData("popup")]
    [InlineData("redirect")]
    [InlineData("script")]
    [InlineData("hirehi")]
    [InlineData("hirehi-broker")]
    [InlineData("delayed-vacancy")]
    public async Task VerifiedExternalFormIsFilledAndPreservedWithoutSubmitting(string entry)
    {
        await using var f = await Fixture.CreateAsync(entry);
        var owner = await f.Driver.BeginRunAsync(f.Workflow, default);
        Assert.True(await f.Driver.PrepareExternalDraftAsync(f.Workflow, f.Platform, f.Candidate, f.Vacancy, f.Application, default));
        Assert.Throws<InvalidOperationException>(() => f.Sessions.GetDraftPage(f.Application.Id));
        await owner.DisposeAsync();
        var page = f.Sessions.GetDraftPage(f.Application.Id);
        Assert.Equal("https://company.example/jobs/1", page.Url);
        Assert.Equal(f.Candidate.Email, await page.Locator("input[type=email]").InputValueAsync());
        Assert.Equal("Synthetic Candidate", await page.Locator("input[name=fullName]").InputValueAsync());
        Assert.Equal("Verified cover letter", await page.Locator("textarea").InputValueAsync());
        Assert.Equal(1, await page.Locator("input[type=file]").EvaluateAsync<int>("el=>el.files.length"));
        Assert.Equal(0, f.Submissions);
        Assert.Equal(0, f.Writes);
        Assert.Empty(await f.Db.Set<ApplicationAttempt>().ToListAsync());
        Assert.Equal(ApplicationState.ReadyForReview, f.Application.State);
        var report = await ExternalApplicationDraft.ReadAsync(f.Db, f.Application.Id);
        Assert.True(report!.Verified);
        Assert.Contains(report.Fields, x => x.Type == "file" && x.Value == "fixture.pdf");
        Assert.Contains(report.Fields, x => x.Value == f.Candidate.Email);
        Assert.Equal(report.Fingerprint, (await ExternalApplicationDraft.CaptureAsync(page)).Fingerprint);
        Assert.True(File.Exists(ExternalApplicationDraft.ScreenshotPath(f.Application.Id)));
        // Guard remains attached after handoff: even a synthetic spontaneous submit cannot send.
        await page.EvaluateAsync("document.querySelector('form').requestSubmit()");
        await page.WaitForTimeoutAsync(150);
        Assert.Equal(0, f.Submissions);
        Assert.True(await f.Driver.PrepareExternalDraftAsync(f.Workflow, f.Platform, f.Candidate, f.Vacancy, f.Application, default));
        Assert.Equal(0, f.Submissions);
        var released = await f.Sessions.ReleaseDraftAsync(f.Application.Id);
        Assert.Equal(f.Workflow.BrowserProfileId, released.BrowserId);
        Assert.Same(page, f.Sessions.GetManualPage(released.BrowserId, released.Tab));
    }

    [Fact]
    public async Task HireHiEntryWithoutAnOriginalVacancyStopsBeforeEnteringData()
    {
        await using var f = await Fixture.CreateAsync("hirehi-no-referral");
        await using var run = await f.Driver.BeginRunAsync(f.Workflow, default);
        await Assert.ThrowsAsync<HumanReviewRequiredException>(() =>
            f.Driver.PrepareExternalDraftAsync(f.Workflow, f.Platform, f.Candidate, f.Vacancy, f.Application, default));
        Assert.Equal(0, f.ExternalLoads);
        Assert.Equal(0, f.Writes);
        Assert.Empty(await f.Db.Set<ApplicationAttempt>().ToListAsync());
    }

    [Fact]
    public async Task HireHiBrokerResolvesTheJobBoardInsteadOfReusingTheOldIntermediateReport()
    {
        await using var f = await Fixture.CreateAsync("hirehi-linkedin");
        var intermediate = "https://hirehi.ru/contact-opening";
        var previous = new ExternalApplicationDraft(f.Application.Id, f.Workflow.BrowserProfileId!.Value,
            f.Vacancy.Url, intermediate, false, "Intermediary was mistaken for the destination", [], "",
            f.Application.Revision, ExternalApplicationDraft.Materials(f.Application), DateTime.UtcNow) { Outcome = "Suspicious" };
        f.Db.Audit.Add(new AuditEvent { EntityId = f.Application.Id, EntityType = nameof(JobApplication),
            Action = ExternalApplicationDraft.AuditAction, Details = JsonSerializer.Serialize(previous) });
        await f.Db.SaveChangesAsync();

        await using var run = await f.Driver.BeginRunAsync(f.Workflow, default);
        Assert.True(await f.Driver.PrepareExternalDraftAsync(f.Workflow, f.Platform, f.Candidate, f.Vacancy, f.Application, default));
        var report = await ExternalApplicationDraft.ReadAsync(f.Db, f.Application.Id, default);
        Assert.Equal("Routed", report!.Outcome);
        Assert.Equal("LinkedIn", report.RoutedPlatform);
        Assert.Equal("https://www.linkedin.com/jobs/view/4491900889/", report.Url);
        Assert.Equal(1, f.ContactOpens);
        Assert.Equal(0, f.ExternalLoads);
        Assert.Equal(0, f.Writes);
        Assert.Empty(await f.Db.Set<ApplicationAttempt>().ToListAsync());
    }

    [Theory]
    [InlineData("link", true, ApplicationState.Submitted, 1)]
    [InlineData("no-receipt", false, ApplicationState.SubmissionUnknown, 1)]
    [InlineData("hh", false, ApplicationState.ReadyForReview, 0)]
    public async Task ExternalAutoSubmitRequiresAReceiptAndDoesNotSendThroughAnotherBoard(string entry, bool confirmed,
        ApplicationState expectedState, int expectedPosts)
    {
        await using var f = await Fixture.CreateAsync(entry);
        f.Workflow.ApplicationStatusFilter = WorkflowDefinition.ApprovedStatusFilter;
        f.Workflow.VacancySource = "saved";
        f.Workflow.AutoSubmit = f.Workflow.SubmitExternalApplications = true;
        f.Application.State = ApplicationState.ReadyToSubmit;
        await f.Db.SaveChangesAsync();
        await using (var owner = await f.Driver.BeginRunAsync(f.Workflow, default))
            Assert.True(await f.Driver.PrepareExternalDraftAsync(f.Workflow, f.Platform, f.Candidate, f.Vacancy, f.Application, default));
        var savedReport = await ExternalApplicationDraft.ReadAsync(f.Db, f.Application.Id);
        Assert.True(expectedState == f.Application.State,
            $"Expected {expectedState}, actual {f.Application.State}, POSTs {f.Submissions}. {savedReport?.Reason}");
        Assert.Equal(expectedPosts, f.Submissions);
        if (expectedPosts == 1)
        {
            var attempt = await f.Db.Set<ApplicationAttempt>().SingleAsync();
            Assert.Equal(confirmed ? "Confirmed" : "SubmissionUnknown", attempt.Outcome);
        }
        else
        {
            Assert.Empty(await f.Db.Set<ApplicationAttempt>().ToListAsync());
            Assert.Equal("hh.ru", (await ExternalApplicationDraft.ReadAsync(f.Db, f.Application.Id))!.RoutedPlatform);
            Assert.Equal(0, f.ExternalLoads);
        }
    }

    [Theory]
    [InlineData("Unrelated Employer", "C# Engineer")]
    [InlineData("Synthetic Company", "Unrelated Role")]
    [InlineData("Missing quote", "Missing role")]
    public async Task EmployerOrVacancyMismatchNeverReceivesCandidateData(string employer, string role)
    {
        await using var f = await Fixture.CreateAsync("link", employer, role);
        await using (var owner = await f.Driver.BeginRunAsync(f.Workflow, default))
            Assert.True(await f.Driver.PrepareExternalDraftAsync(f.Workflow, f.Platform, f.Candidate, f.Vacancy, f.Application, default));
        var page = f.Sessions.GetDraftPage(f.Application.Id);
        Assert.Equal("", await page.Locator("input[type=email]").InputValueAsync());
        Assert.Equal(0, await page.Locator("input[type=file]").EvaluateAsync<int>("el=>el.files.length"));
        Assert.Equal(0, f.Writes);
        Assert.False((await ExternalApplicationDraft.ReadAsync(f.Db, f.Application.Id))!.Verified);
    }

    [Fact]
    public async Task PrivateDestinationIsNotOpenedOrFilled()
    {
        await using var f = await Fixture.CreateAsync("link", address: IPAddress.Loopback);
        await using (var owner = await f.Driver.BeginRunAsync(f.Workflow, default))
            Assert.True(await f.Driver.PrepareExternalDraftAsync(f.Workflow, f.Platform, f.Candidate, f.Vacancy, f.Application, default));
        Assert.Equal(0, f.ExternalLoads);
        Assert.False((await ExternalApplicationDraft.ReadAsync(f.Db, f.Application.Id))!.Verified);
        Assert.Equal(0, f.Writes);
    }

    [Fact]
    public async Task NativeApplicationFormKeepsTheExistingSubmissionFlow()
    {
        await using var f = await Fixture.CreateAsync("native");
        await using var owner = await f.Driver.BeginRunAsync(f.Workflow, default);
        Assert.False(await f.Driver.PrepareExternalDraftAsync(f.Workflow, f.Platform, f.Candidate, f.Vacancy, f.Application, default));
        Assert.Null(await ExternalApplicationDraft.ReadAsync(f.Db, f.Application.Id));
        Assert.False(f.Sessions.HasDraft(f.Application.Id));
        Assert.Equal(0, f.Writes);
    }

    [Fact]
    public async Task HumanTakeoverRequiresTheReviewedFormAndPreservesTheReportAfterBrowserClose()
    {
        await using var f = await Fixture.CreateAsync("link");
        await using (var owner = await f.Driver.BeginRunAsync(f.Workflow, default))
            await f.Driver.PrepareExternalDraftAsync(f.Workflow, f.Platform, f.Candidate, f.Vacancy, f.Application, default);
        var report = (await ExternalApplicationDraft.ReadAsync(f.Db, f.Application.Id))!;
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var connection = f.Db.Database.GetDbConnection().ConnectionString;
        builder.Services.AddScoped(_ => new JobFlowDb(new DbContextOptionsBuilder<JobFlowDb>().UseSqlite(connection).Options));
        builder.Services.AddSingleton(f.Sessions);
        await using var server = builder.Build();
        server.Use(async (context, next) =>
        {
            try { await next(); }
            catch (InvalidOperationException) { context.Response.StatusCode = 400; }
        });
        server.MapBrowserDraftEndpoints();
        await server.StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri(server.Urls.Single()) };
        var path = "/api/applications/" + f.Application.Id + "/browser-draft/";
        var preview = await client.GetFromJsonAsync<JsonElement>(path + "review");
        Assert.True(preview.GetProperty("live").GetBoolean());
        Assert.Equal("no-store", (await client.GetAsync(path + "review")).Headers.CacheControl!.ToString());
        var hadScreenshot = preview.GetProperty("hasScreenshot").GetBoolean();
        var screen = await client.GetAsync(path + "screen");
        Assert.Equal(hadScreenshot ? HttpStatusCode.OK : HttpStatusCode.NotFound, screen.StatusCode);
        if (hadScreenshot) Assert.Equal("image/jpeg", screen.Content.Headers.ContentType?.MediaType);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(path + "release", new { revision = f.Application.Revision, fingerprint = "changed" })).StatusCode);
        var page = f.Sessions.GetDraftPage(f.Application.Id);
        await page.Locator("input[type=email]").FillAsync("edited@example.invalid");
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(path + "release", new { revision = f.Application.Revision, report.Fingerprint })).StatusCode);
        await page.Locator("input[type=email]").FillAsync(f.Candidate.Email);
        var release = await client.PostAsJsonAsync(path + "release", new { revision = f.Application.Revision, report.Fingerprint });
        Assert.Equal(HttpStatusCode.OK, release.StatusCode);
        await f.Db.Entry(f.Application).ReloadAsync();
        Assert.Equal(ApplicationState.ReadyToSubmit, f.Application.State);
        Assert.Equal(0, f.Submissions);
        Assert.Empty(await f.Db.Set<ApplicationAttempt>().ToListAsync());
        await f.Sessions.CloseAsync(f.Workflow.BrowserProfileId!.Value);
        preview = await client.GetFromJsonAsync<JsonElement>(path + "review");
        Assert.False(preview.GetProperty("live").GetBoolean());
        Assert.Equal(hadScreenshot, preview.GetProperty("hasScreenshot").GetBoolean());
    }

    [Theory]
    [InlineData("http://company.example/jobs/1")]
    [InlineData("https://user:password@company.example/jobs/1")]
    [InlineData("https://127.0.0.1/")]
    [InlineData("https://company.local/")]
    [InlineData("https://company.example:8443/")]
    public async Task UnsafeDestinationIsRejectedBeforeResolving(string url) =>
        Assert.False(await ExternalApplicationDestination.IsAllowedAsync(url, default,
            (_, _) => throw new InvalidOperationException("Unsafe URL must be rejected before DNS")));

    private sealed class Resolver(IPAddress address) : IExternalApplicationAddressResolver
    {
        public Task<IPAddress[]> ResolveAsync(string host, CancellationToken ct) => Task.FromResult(new[] { address });
    }
    private sealed class Documents(Func<IRoute, ExternalApplicationDocument> load) : IExternalApplicationDocumentLoader
    {
        public Task<ExternalApplicationDocument> LoadAsync(IRoute route, CancellationToken ct) => Task.FromResult(load(route));
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "jobflow-external-" + Guid.NewGuid().ToString("N"));
        private readonly string? previous = Environment.GetEnvironmentVariable("JOBFLOW_DATA_DIR");
        public JobFlowDb Db = null!;
        public BrowserSessions Sessions = new(true);
        public BrowserWorkflowDriver Driver = null!;
        public WorkflowDefinition Workflow = null!;
        public JobPlatform Platform = null!;
        public Vacancy Vacancy = null!;
        public JobApplication Application = null!;
        public CandidateProfile Candidate = null!;
        public Agent Agent = null!;
        public IBrowserContext Context = null!;
        public int Submissions, Writes, ExternalLoads, ContactOpens, EntryClicks;
        public static async Task<Fixture> CreateAsync(string entry, string employer = "Synthetic Company", string role = "C# Engineer", IPAddress? address = null)
        {
            var f = new Fixture();
            Directory.CreateDirectory(f.root);
            Environment.SetEnvironmentVariable("JOBFLOW_DATA_DIR", f.root);
            f.Db = new(new DbContextOptionsBuilder<JobFlowDb>().UseSqlite($"Data Source={Path.Combine(f.root, "test.db")};Pooling=False").Options);
            await f.Db.Database.MigrateAsync();
            var user = new User();
            f.Candidate = new() { UserId = user.Id, Name = "Synthetic Candidate", Email = "synthetic@example.invalid" };
            var boardUrl = entry switch
            {
                "hh-native" or "hh-restricted" or "hh-stalled" or "hh-public" => "https://hh.ru/",
                _ when entry.StartsWith("hirehi", StringComparison.Ordinal) => "https://hirehi.ru/",
                _ => "https://board.example/"
            };
            f.Platform = new() { Name = "Synthetic Board", Url = boardUrl, Adapter = "browser" };
            var jobPath = boardUrl switch { "https://hirehi.ru/" => "jobs/c-developer-1", "https://hh.ru/" => "vacancy/12345", _ => "jobs/1" };
            f.Vacancy = new() { JobPlatformId = f.Platform.Id, Employer = "Synthetic Company", Title = "C# Engineer", Url = boardUrl + jobPath };
            var browser = new BrowserProfile();
            var resume = new Resume { CandidateProfileId = f.Candidate.Id };
            var version = new ResumeVersion { ResumeId = resume.Id, StorageKey = "fixture.pdf" };
            var provider = new ModelProvider { Enabled = true };
            var browserAgent = new AgentProfile { CandidateProfileId = f.Candidate.Id, ModelProviderId = provider.Id,
                TaskType = "BrowserAutomation", Enabled = true };
            f.Workflow = new() { CandidateProfileId = f.Candidate.Id, JobPlatformId = f.Platform.Id, BrowserProfileId = browser.Id,
                ResumeVersionId = version.Id, BrowserAgentId = browserAgent.Id, MaxBrowserSteps = 15, ExecutionMode = "browser" };
            f.Application = new() { CandidateProfileId = f.Candidate.Id, VacancyId = f.Vacancy.Id, ResumeVersionId = version.Id,
                WorkflowDefinitionId = f.Workflow.Id, State = ApplicationState.ReadyForReview, CoverLetter = "Verified cover letter" };
            Directory.CreateDirectory(Path.Combine(f.root, "resumes"));
            await File.WriteAllTextAsync(Path.Combine(f.root, "resumes", "fixture.pdf"), "%PDF Synthetic fixture");
            f.Db.AddRange(user, f.Candidate, f.Platform, f.Vacancy, browser, resume, version, provider, browserAgent, f.Workflow, f.Application);
            await f.Db.SaveChangesAsync();
            await f.Sessions.OpenAsync(browser.Id);
            var context = f.Sessions.GetManualPage(browser.Id).Context;
            f.Context = context;
            ExternalApplicationDocument Html(string body) => new(200, new() { ["content-type"] = "text/html; charset=utf-8" }, System.Text.Encoding.UTF8.GetBytes(body));
            ExternalApplicationDocument LoadDocument(string url)
            {
                if (url == "https://board.example/redirect")
                    return new(302, new() { ["Location"] = "https://company.example/jobs/1" }, []);
                if (entry is "hh-native" or "hh-restricted" or "hh-stalled" or "hh-public")
                {
                    if (url == "https://hh.ru/click-observed") { f.EntryClicks++; return Html(""); }
                    if (url == "https://hh.ru/applicant/vacancy_response?vacancyId=12345")
                    {
                        if (entry != "hh-native") return Html((entry == "hh-public"
                            ? "<style>[data-qa=hidden-resume-warning]{height:0;overflow:hidden}</style>" : "") + """
                            <div data-qa="task-question">Настоящим подтверждаю, что сведения являются достоверными. <input type="radio"></div>
                            <div data-qa="hidden-resume-warning">Чтобы откликнуться, поменяйте видимость резюме на «Видно всем работодателям»</div>
                            <form action="/submit" method="post"><button data-qa="vacancy-response-submit-popup">Откликнуться</button></form>
                            """);
                        return Html("<form action='/submit' method='post'><button data-qa='vacancy-response-submit-popup'>Отправить</button></form>");
                    }
                    if (entry == "hh-stalled") return Html("""
                        <a data-qa="vacancy-response-link-top" href="/applicant/vacancy_response?vacancyId=12345"
                           onclick="fetch('/click-observed');return false">Откликнуться</a>
                        """);
                    return Html("""
                        <a data-qa="vacancy-response-link-top" href="/applicant/vacancy_response?vacancyId=12345"
                           onclick="fetch('/click-observed'); fetch('/submit',{ method:'POST' }); const url=this.href; setTimeout(() => location.href=url, 1800); return false">Откликнуться</a>
                        <a data-qa="vacancy-response-link-top" href="/applicant/vacancy_response?vacancyId=12345">Откликнуться</a>
                        """);
                }
                if (url == "https://hirehi.ru/contact-opening")
                {
                    var destination = entry == "hirehi-linkedin" ? "https://www.linkedin.com/jobs/view/4491900889/" : "https://company.example/jobs/1";
                    return Html($"<p>Получаем контакт, сейчас откроем...</p><script>setTimeout(() => location.href='{destination}', 1800)</script>");
                }
                if (url.StartsWith(boardUrl, StringComparison.Ordinal))
                {
                    var link = entry switch
                    {
                        "popup" => "<button onclick=\"window.open('https://company.example/jobs/1','_blank')\">Apply</button>",
                        "script" => "<button onclick=\"location.href='https://company.example/jobs/1'\">Apply</button>",
                        "hirehi" => "<textarea aria-label='Вопрос Хайрику про вакансию'></textarea><a href='#' onclick=\"window.open('https://company.example/jobs/1','_blank'); return false\">Отклик</a>",
                        "hirehi-broker" or "hirehi-linkedin" => """
                            <textarea aria-label="Вопрос Хайрику про вакансию"></textarea>
                            <a href="#" onclick="openContact(); return false">Отклик</a>
                            <script>
                            function openContact() {
                              fetch('/unrelated', {method: 'POST'});
                              for (const job of ['2', '1', '1']) {
                                const form = document.createElement('form');
                                form.method = 'POST';
                                form.action = '/contact-opening';
                                form.target = '_blank';
                                for (const [name, value] of Object.entries({job_id: job, contact_ticket: 'fixture-ticket'})) {
                                  const item = document.createElement('input');
                                  item.type = 'hidden';
                                  item.name = name;
                                  item.value = value;
                                  form.append(item);
                                }
                                document.body.append(form);
                                form.submit();
                                form.remove();
                              }
                            }
                            </script>
                            """,
                        "hirehi-no-referral" => "<textarea aria-label='Вопрос Хайрику про вакансию'></textarea><a href='#' onclick=\"fetch('/probe',{method:'POST'}); return false\">Отклик</a>",
                        "redirect" => "<a href='/redirect'>Apply</a>",
                        "native" => "<form><label>Email<input type='email'></label><label>Cover letter<textarea></textarea></label><button>Send application</button></form>",
                        "hh" => "<a href='https://hh.ru/vacancy/12345'>Apply</a>",
                        _ => "<a href='https://company.example/jobs/1'>Apply</a>"
                    };
                    return Html("<h1>C# Engineer</h1><p>Synthetic Company</p><p>Published today. Full requirement: C# software development.</p>" + link);
                }
                f.ExternalLoads++;
                var content = $"<h1>{role}</h1><h2>{employer}</h2>" + """
                    <form action='/submit' method='post' enctype='multipart/form-data'>
                    <label>Full name<input name='fullName'></label>
                    <label>Email<input type='email' name='email'></label>
                    <label>Cover letter<textarea name='letter'></textarea></label>
                    <label>Resume<input type='file' name='resume'></label>
                    <button type='submit'>Send application</button></form>
                    """;
                return entry == "delayed-vacancy"
                    ? Html("<p>Jobs – Company</p><script>setTimeout(() => document.body.innerHTML = " +
                        JsonSerializer.Serialize(content) + ", 2800)</script>")
                    : Html(content);
            }
            await context.RouteAsync("**/*", async route =>
            {
                if (route.Request.Method == "POST")
                {
                    f.Writes++;
                    if (route.Request.Url.Contains("/submit")) f.Submissions++;
                    await route.FulfillAsync(new() { Status = 200, Body = "Application submitted" }); return;
                }
                var document = LoadDocument(route.Request.Url);
                await route.FulfillAsync(new() { Status = document.Status, Headers = document.Headers, BodyBytes = document.Body });
            });
            f.Agent = new Agent(employer, role);
            f.Driver = new(f.Sessions, f.Agent, f.Db, externalResolver: new Resolver(address ?? IPAddress.Parse("93.184.216.34")),
                externalDocuments: new Documents(route =>
                {
                    if (route.Request.Method != "POST") return LoadDocument(route.Request.Url);
                    if (route.Request.Url == "https://hirehi.ru/contact-opening")
                    {
                        f.ContactOpens++;
                        Assert.Contains("job_id=1&", route.Request.PostData);
                        return LoadDocument(route.Request.Url);
                    }
                    f.Submissions++;
                    return Html(entry == "no-receipt" ? "<p>Pending confirmation</p>" : "<p>Application submitted</p>");
                }));
            return f;
        }
        public async ValueTask DisposeAsync()
        {
            await Sessions.DisposeAsync(); await Db.DisposeAsync();
            Environment.SetEnvironmentVariable("JOBFLOW_DATA_DIR", previous);
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            for (var i=0;;i++)
            {
                try { Directory.Delete(root, true); break; }
                catch (IOException) when (i<15) { await Task.Delay(250); }
            }
        }
    }

    private sealed class Agent(string employer, string role) : IAgentExecutor
    {
        public bool ExternalBlocked;
        public bool ProbeBlocked;
        public Func<AgentExecutionRequest, Task<AgentExecutionResult>>? VisualOverride;
        public Task<AgentExecutionResult> ExecuteAsync(AgentExecutionRequest request, CancellationToken ct)
        {
            using var input = JsonDocument.Parse(request.Input);
            var root = input.RootElement;
            if (request.OutputSchema == BrowserWorkflowDriver.VisualActionSchema && VisualOverride != null) return VisualOverride(request);
            if (request.OutputSchema == BrowserWorkflowDriver.ExternalVerificationSchema)
                return Result(new { verified = true, employerEvidence = employer, vacancyEvidence = role, reason = "Employer and role match the board referral" });
            if (root.TryGetProperty("availabilityEvidenceChoices", out var choices))
                return Result(new { publishedAt = DateTime.UtcNow.ToString("O"), publicationEvidence = "Published today",
                    availability = "Open", availabilityEvidence = choices.EnumerateObject().First(x => x.Value.GetString() == "Apply").Name });
            if (ExternalBlocked && root.GetProperty("phase").GetString() == "external-draft")
                return Result(new { action = "blocked", target = "", value = "", url = "", reason = "Cannot locate the text field", vacancies = Array.Empty<object>() });
            if (ProbeBlocked && root.GetProperty("phase").GetString() == "apply")
                return Result(new { action = "blocked", target = "", value = "", url = "", reason = "Cannot locate the Apply control", vacancies = Array.Empty<object>() });
            var controls = root.GetProperty("page").GetProperty("controls").EnumerateArray().ToArray();
            if (root.GetProperty("phase").GetString() == "apply")
                return Action("click", controls.Single(x => x.GetProperty("label").GetString() == "Apply"));
            foreach (var field in controls)
            {
                if (field.GetProperty("value").GetString() != "") continue;
                var type = field.GetProperty("inputType").GetString();
                if (type == "email") return Action("fill", field, "synthetic@example.invalid");
                if (field.GetProperty("name").GetString() == "fullName") return Action("fill", field, "Synthetic Candidate");
                if (type == "textarea") return Action("fill", field, "Verified cover letter");
                if (type == "file") return Action("upload", field);
            }
            return Action("submit", controls.Single(x => x.GetProperty("isSubmit").GetBoolean()));
        }
        private static Task<AgentExecutionResult> Action(string action, JsonElement field, string value = "") =>
            Result(new { action, target = field.GetProperty("id").GetString(), value, url = "", reason = "", vacancies = Array.Empty<object>() });
        private static Task<AgentExecutionResult> Result(object output) => Task.FromResult(new AgentExecutionResult(Guid.NewGuid(), "Succeeded", JsonSerializer.Serialize(output)));
    }
}
