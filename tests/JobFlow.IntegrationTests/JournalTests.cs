using System.Net;
using System.Text;
using System.Text.Json;
using JobFlow.Domain;
using JobFlow.Infrastructure;
using Microsoft.EntityFrameworkCore;
namespace JobFlow.IntegrationTests;
public class JournalTests
{
    [Fact] public async Task PreviewConfirmDeduplicateAndBlockHistoricalApplications()
    {
        await using var fixture=await Fixture.Create();
        var db=fixture.Db;var service=new JournalService(db);
        var csv="Id,Title,Status\r\na,\"Engineer, backend\",Submitted\r\na,\"Engineer, backend\",Submitted";
        var preview=await service.PreviewAsync(fixture.Integration.Id,csv,default);
        Assert.Equal(0,await db.Vacancies.CountAsync());Assert.False(fixture.Integration.ImportConfirmed);
        var committed=await service.ConfirmAsync(preview.Id,default);
        Assert.Equal("Committed",committed.Status);Assert.Equal(1,committed.Added);Assert.Equal(1,committed.Duplicates);
        Assert.Equal(1,await db.Set<HistoricalApplication>().CountAsync());Assert.True(fixture.Integration.ImportConfirmed);
        await service.ConfirmAsync(preview.Id,default);
        Assert.Equal(1,await db.Vacancies.CountAsync());
        Assert.Equal(preview.Id,(await service.PreviewAsync(fixture.Integration.Id,csv,default)).Id);
        await Assert.ThrowsAsync<ArgumentException>(()=>service.PreviewAsync(fixture.Integration.Id,"Wrong,Columns\r\na,b",default));
    }
    [Fact] public async Task FeedbackDeduplicatesAndFailureDoesNotReportSuccess()
    {
        await using var fixture=await Fixture.Create();
        fixture.Integration.Kind="feedback-json";fixture.Integration.Endpoint="https://feedback.example.invalid/events";fixture.Integration.Enabled=true;
        await fixture.Db.SaveChangesAsync();
        var handler=new Handler("""[{"sourceEventId":"one","subject":"Interview","body":"Synthetic invitation","receivedAt":"2026-11-15T10:00:00Z"}]""");
        using var http=new HttpClient(handler);
        var service=new RemoteIntegrationService(fixture.Db,new SecretStore(fixture.Root),http);
        await service.PullAsync(fixture.Integration.Id,default);await service.PullAsync(fixture.Integration.Id,default);
        Assert.Equal(1,await fixture.Db.Set<FeedbackEvent>().CountAsync());
        var last=fixture.Integration.LastCheckedAt;
        handler.Fail=true;
        await Assert.ThrowsAsync<InvalidOperationException>(()=>service.PullAsync(fixture.Integration.Id,default));
        await fixture.Db.Entry(fixture.Integration).ReloadAsync();
        Assert.Equal("Failed",fixture.Integration.LastStatus);Assert.Equal(last,fixture.Integration.LastCheckedAt);
    }
    [Fact] public async Task GoogleSheetsReadsValuesIntoUnconfirmedImport()
    {
        await using var fixture=await Fixture.Create();
        fixture.Integration.Kind="google-sheets";fixture.Integration.Endpoint="https://sheets.googleapis.com/v4/spreadsheets/synthetic/values/A1:C10";fixture.Integration.Enabled=true;
        await fixture.Db.SaveChangesAsync();
        using var http=new HttpClient(new Handler("""{"values":[["Id","Title","Status"],["one","Test engineer","Submitted"]]}"""));
        await new RemoteIntegrationService(fixture.Db,new SecretStore(fixture.Root),http).PullAsync(fixture.Integration.Id,default);
        Assert.Equal("Preview",(await fixture.Db.Set<JournalImport>().SingleAsync()).Status);
        Assert.False(fixture.Integration.ImportConfirmed);Assert.Equal(0,await fixture.Db.Vacancies.CountAsync());
    }
    [Fact] public async Task GmailReadsTextAndDeduplicatesMessages()
    {
        await using var fixture=await Fixture.Create();
        fixture.Integration.Kind="gmail";fixture.Integration.Endpoint="https://gmail.googleapis.com/gmail/v1/users/me/messages?q=subject%3Ainterview";fixture.Integration.Enabled=true;
        await fixture.Db.SaveChangesAsync();
        using var http=new HttpClient(new GmailHandler());
        var service=new RemoteIntegrationService(fixture.Db,new SecretStore(fixture.Root),http);
        await service.PullAsync(fixture.Integration.Id,default);await service.PullAsync(fixture.Integration.Id,default);
        var feedback=await fixture.Db.Set<FeedbackEvent>().SingleAsync();Assert.Equal("Interview",feedback.Subject);
        Assert.Contains("https://hh.ru/vacancy/123", feedback.Body);
        Assert.Contains("Interview invitation", feedback.Body);
    }
    [Fact]
    public async Task SheetsExportRequiresFreshPreviewAndUsesRawValues()
    {
        await using var fixture = await Fixture.Create();
        var integration = fixture.Integration;
        integration.Kind = "google-sheets";
        integration.Endpoint = "https://sheets.googleapis.com/v4/spreadsheets/synthetic/values/Import!A1:C10";
        integration.Enabled = true;
        integration.ImportConfirmed = true;
        await fixture.Db.SaveChangesAsync();
        using var handler = new SheetsHandler();
        using var http = new HttpClient(handler);
        var writer = new SheetsWriteBack(fixture.Db, new SecretStore(fixture.Root), http);
        var preview = await writer.PreviewAsync(integration.Id, "Export", default);
        Assert.Equal(0, handler.Writes);
        handler.Cells = """{"values":[["Unrelated data"]]}""";
        await Assert.ThrowsAsync<InvalidOperationException>(() => writer.PreviewAsync(integration.Id, "Export", default, preview.Digest));
        handler.Cells = System.Text.Json.JsonSerializer.Serialize(new { values = new[] { preview.Rows[0], new[] { "old-id" } } });
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => writer.PreviewAsync(integration.Id, "Export", default, preview.Digest));
        Assert.Equal(0, handler.Writes);
        var fresh = await writer.PreviewAsync(integration.Id, "Export", default);
        await writer.PreviewAsync(integration.Id, "Export", default, fresh.Digest);
        Assert.Equal(1, handler.Writes);
        Assert.Contains(await fixture.Db.Audit.ToListAsync(), x => x.Action == "SheetsExported");
    }

    [Theory]
    [InlineData(false, "Invitation")]
    [InlineData(true, "Rejection")]
    public async Task MailClassifiesAndLinksOnlyAnUnambiguousApplication(bool useAgent, string expected)
    {
        await using var fixture = await Fixture.Create();
        var db = fixture.Db;
        var integration = fixture.Integration;
        integration.Kind = "gmail";
        var app = await AddApplication(fixture, "one");
        var agent = new MailAgent();
        if (useAgent)
        {
            var provider = new ModelProvider();
            var config = new AgentProfile { CandidateProfileId = integration.CandidateProfileId, Enabled = true, TaskType = "MailAnalysis", ModelProviderId = provider.Id };
            db.AddRange(provider, config);
            integration.MailAgentId = config.Id;
        }
        var exact = new FeedbackEvent
        {
            CandidateProfileId = integration.CandidateProfileId, IntegrationId = integration.Id, SourceEventId = "one",
            Subject = "Interview invitation", Body = "https://hh.ru/vacancy/one?from=email", ReceivedAt = DateTime.UtcNow.AddSeconds(1)
        };
        var ambiguous = new FeedbackEvent
        {
            CandidateProfileId = integration.CandidateProfileId, IntegrationId = integration.Id, SourceEventId = "two",
            Subject = "Interview invitation", Body = "No vacancy reference", ReceivedAt = exact.ReceivedAt
        };
        db.AddRange(exact, ambiguous);
        await db.SaveChangesAsync();
        var service = new FeedbackProcessing(db, agent);
        await service.ProcessAsync(integration, default);
        await service.ProcessAsync(integration, default);
        Assert.Equal(expected, app.ResponseStatus);
        Assert.Equal(ApplicationState.EmployerResponded, app.State);
        Assert.Equal(expected, (await db.Set<EmployerInteraction>().SingleAsync()).Category);
        Assert.False(exact.NeedsAction);
        Assert.True(ambiguous.NeedsAction);
        Assert.Equal(useAgent ? 2 : 0, agent.Calls);
    }

    [Fact]
    public async Task HeadHunterStatusReadsApplicantNegotiationsAndKeepsNewestObservation()
    {
        await using var fixture = await Fixture.Create();
        var db = fixture.Db;
        var app = await AddApplication(fixture, "123");
        var integration = fixture.Integration;
        integration.Kind = "hh-status";
        integration.Enabled = true;
        integration.Endpoint = "https://api.hh.ru/negotiations";
        await db.SaveChangesAsync();
        var secrets = new SecretStore(fixture.Root);
        await secrets.SaveAsync(integration.Id, new() { ["Authorization"] = "Bearer synthetic" }, default);
        var timestamp = DateTime.UtcNow.AddSeconds(1).ToString("O");
        var json = $$"""{"pages":1,"items":[{"vacancy":{"id":"123"},"state":{"id":"discard"},"updated_at":"{{timestamp}}"},{"vacancy":{"id":"123"},"state":{"id":"invitation"},"updated_at":"2000-01-01T00:00:00Z"}]}""";
        using var http = new HttpClient(new Handler(json));
        var source = new RemoteIntegrationService(db, secrets, http);
        await source.PullAsync(integration.Id, default);
        await source.PullAsync(integration.Id, default);
        Assert.Equal("Rejection", app.ResponseStatus);
        Assert.Equal("Succeeded", integration.LastStatus);
        Assert.Equal(1, await db.Audit.CountAsync(x => x.Action == "PlatformStatusObserved"));
        Assert.Empty(await db.Set<ApplicationAttempt>().ToListAsync());
    }

    private static async Task<JobApplication> AddApplication(Fixture fixture, string externalId)
    {
        var resume = new Resume { CandidateProfileId = fixture.Integration.CandidateProfileId };
        var version = new ResumeVersion { ResumeId = resume.Id, Version = 1 };
        var vacancy = new Vacancy { JobPlatformId = fixture.Integration.JobPlatformId!.Value, ExternalId = externalId, Url = "https://hh.ru/vacancy/" + externalId };
        var app = new JobApplication { CandidateProfileId = resume.CandidateProfileId, ResumeVersionId = version.Id, VacancyId = vacancy.Id, State = ApplicationState.Submitted };
        fixture.Db.AddRange(resume, version, vacancy, app);
        await fixture.Db.SaveChangesAsync();
        return app;
    }

    private sealed class MailAgent : JobFlow.Application.IAgentExecutor
    {
        public int Calls;
        public Task<JobFlow.Application.AgentExecutionResult> ExecuteAsync(JobFlow.Application.AgentExecutionRequest request, CancellationToken ct)
        {
            Calls++;
            Assert.Contains("Interview invitation", request.Input);
            return Task.FromResult(new JobFlow.Application.AgentExecutionResult(Guid.NewGuid(), "Succeeded", """{"category":"Rejection"}"""));
        }
    }

    private sealed class SheetsHandler : HttpMessageHandler
    {
        public string Cells = "{}";
        public int Writes;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Assert.Contains("'Export'!A1:F501", Uri.UnescapeDataString(request.RequestUri!.AbsolutePath));
            if (request.Method == HttpMethod.Put)
            {
                Assert.Equal("?valueInputOption=RAW", request.RequestUri.Query);
                using var payload = System.Text.Json.JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                var rows = payload.RootElement.GetProperty("values");
                Assert.Equal(2, rows.GetArrayLength());
                Assert.All(rows[1].EnumerateArray(), cell => Assert.Equal("", cell.GetString()));
                Writes++;
            }
            else
            {
                Assert.Equal(HttpMethod.Get, request.Method);
                Assert.Equal("?valueRenderOption=FORMULA", request.RequestUri.Query);
            }
            return new(HttpStatusCode.OK) { Content = new StringContent(Cells, Encoding.UTF8, "application/json") };
        }
    }

    private class Handler(string json):HttpMessageHandler
    {
        public bool Fail;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        {
            Assert.Equal(HttpMethod.Get,request.Method);
            return Task.FromResult(new HttpResponseMessage(Fail?HttpStatusCode.Unauthorized:HttpStatusCode.OK){Content=new StringContent(json,Encoding.UTF8,"application/json")});
        }
    }
    private class GmailHandler:HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        {
            Assert.Equal(HttpMethod.Get,request.Method);
            var body = """{"messages":[{"id":"m1"}]}""";
            if (request.RequestUri!.AbsolutePath.EndsWith("/m1"))
            {
                Assert.Equal("?format=full", request.RequestUri.Query);
                var data = Convert.ToBase64String(Encoding.UTF8.GetBytes("""<a href="https://hh.ru/vacancy/123">Interview invitation</a>"""))
                    .TrimEnd('=').Replace('+', '-').Replace('/', '_');
                body = $$$$"""{"id":"m1","snippet":"Synthetic feedback","internalDate":"1794736800000","payload":{"mimeType":"text/html","headers":[{"name":"Subject","value":"Interview"}],"body":{"data":"{{{{data}}}}"}}}""";
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(body,Encoding.UTF8,"application/json")});
        }
    }
    private sealed class Fixture:IAsyncDisposable
    {
        public string Root=Path.Combine(Path.GetTempPath(),"jobflow-journal-"+Guid.NewGuid());
        public JobFlowDb Db=null!;public Integration Integration=null!;
        public static async Task<Fixture> Create()
        {
            var f=new Fixture();Directory.CreateDirectory(f.Root);
            f.Db=new(new DbContextOptionsBuilder<JobFlowDb>().UseSqlite($"Data Source={Path.Combine(f.Root,"test.db")};Pooling=False").Options);
            await f.Db.Database.MigrateAsync();
            var user=new User();var profile=new CandidateProfile{UserId=user.Id};var platform=new JobPlatform();
            f.Integration=new Integration{Name="Journal",CandidateProfileId=profile.Id,JobPlatformId=platform.Id,MappingJson="""{"externalId":"Id","title":"Title","applicationState":"Status"}"""};
            f.Db.AddRange(user,profile,platform,f.Integration);await f.Db.SaveChangesAsync();return f;
        }
        public async ValueTask DisposeAsync(){await Db.DisposeAsync();Directory.Delete(Root,true);}
    }
}

