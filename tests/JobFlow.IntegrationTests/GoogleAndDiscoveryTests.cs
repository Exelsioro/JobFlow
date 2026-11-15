using System.Net;
using System.Security.Cryptography;
using System.Text;
using JobFlow.Domain;
using JobFlow.Infrastructure;

namespace JobFlow.IntegrationTests;

public class GoogleAndDiscoveryTests
{
    [Fact]
    public async Task GoogleGrantIsSingleUseAndRefreshesWithoutLosingRefreshToken()
    {
        var root = Path.Combine(Path.GetTempPath(), "jobflow-oauth-" + Guid.NewGuid());
        const string variable = "JOBFLOW_GOOGLE_CLIENT_ID";
        var previous = Environment.GetEnvironmentVariable(variable);
        Environment.SetEnvironmentVariable(variable, "synthetic-client");
        try
        {
            var secrets = new SecretStore(root);
            var oauth = new GoogleOAuth(secrets);
            var integration = new Integration { Kind = "gmail" };
            var query = new Uri(oauth.Start(integration)).Query.TrimStart('?').Split('&')
                .Select(x => x.Split('=', 2)).ToDictionary(x => x[0], x => Uri.UnescapeDataString(x[1]));
            var requests = 0;
            using var http = new HttpClient(new FixtureHandler(async request =>
            {
                Assert.Equal("https://oauth2.googleapis.com/token", request.RequestUri!.AbsoluteUri);
                Assert.Equal(HttpMethod.Post, request.Method);
                var body = await request.Content!.ReadAsStringAsync();
                if (++requests == 1)
                {
                    Assert.Contains("grant_type=authorization_code", body);
                    var verifier = body.Split('&').Single(x => x.StartsWith("code_verifier=")).Split('=')[1];
                    var challenge = Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)))
                        .TrimEnd('=').Replace('+','-').Replace('/','_');
                    Assert.Equal(query["code_challenge"], challenge);
                    return Json("""{"access_token":"first","refresh_token":"refresh","token_type":"Bearer","expires_in":1}""");
                }
                Assert.Contains("refresh_token=refresh", body);
                return Json("""{"access_token":"renewed","token_type":"Bearer","expires_in":3600}""");
            }));
            await Assert.ThrowsAsync<ArgumentException>(() => oauth.CompleteAsync("wrong", "code", http, default));
            Assert.Equal(0, requests);
            await oauth.CompleteAsync(query["state"], "code", http, default);
            await Assert.ThrowsAsync<ArgumentException>(() => oauth.CompleteAsync(query["state"], "code", http, default));
            var worker = new GoogleOAuth(secrets);
            Assert.Equal("Bearer renewed", (await worker.HeadersAsync(integration, http, default))["Authorization"]);
            Assert.Equal("Bearer renewed", (await worker.HeadersAsync(integration, http, default))["Authorization"]);
            Assert.Equal(2, requests);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, previous);
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task HeadHunterUsesOnlyOfficialSearchAndMapsUnknownSalary()
    {
        var platform = new JobPlatform { Adapter = "headhunter", Url = "https://example.invalid", SearchFilters = "C# & SQL" };
        using var http = new HttpClient(new FixtureHandler(request =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("https://api.hh.ru/vacancies?per_page=50&text=C%23%20%26%20SQL", request.RequestUri!.AbsoluteUri);
            return Task.FromResult(Json("""{"items":[{"id":"123","name":"Engineer","employer":{"name":"Synthetic"},"salary":null,"snippet":{"requirement":"<highlighttext>C#</highlighttext>"}}]}"""));
        }));
        var vacancy = Assert.Single(await new HeadHunterDiscovery(http).DiscoverAsync(platform, default));
        Assert.Equal(platform.Id, vacancy.JobPlatformId);
        Assert.Equal("https://hh.ru/vacancy/123", vacancy.Url);
        Assert.Contains("C#", vacancy.Description);
        Assert.DoesNotContain("highlighttext", vacancy.Description);
        Assert.Null(vacancy.SalaryMax);
    }

    [Fact]
    public async Task GreenhouseReadsPublicBoardWithoutSendingAnApplication()
    {
        var platform = new JobPlatform { Adapter = "greenhouse", Name = "Synthetic", SearchFilters = "test-board" };
        using var http = new HttpClient(new FixtureHandler(request =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("https://boards-api.greenhouse.io/v1/boards/test-board/jobs?content=true", request.RequestUri!.AbsoluteUri);
            return Task.FromResult(Json("""{"jobs":[{"id":12,"title":"Engineer","content":"&lt;p&gt;SQL&lt;/p&gt;","location":{"name":"Remote"}}]}"""));
        }));
        var vacancy = Assert.Single(await new GreenhouseDiscovery(http).DiscoverAsync(platform, default));
        Assert.Equal("SQL", vacancy.Description);
        Assert.Equal("https://job-boards.greenhouse.io/test-board/jobs/12", vacancy.Url);
        Assert.Null(vacancy.PublishedAt);
    }

    private static HttpResponseMessage Json(string content) =>
        new(HttpStatusCode.OK) { Content = new StringContent(content, Encoding.UTF8, "application/json") };

    private sealed class FixtureHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handle) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => handle(request);
    }
}
