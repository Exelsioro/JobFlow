using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using JobFlow.Domain;
using JobFlow.Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
[assembly: CollectionBehavior(DisableTestParallelization = true)]
namespace JobFlow.IntegrationTests;

public class ApiTests
{
    [Fact] public async Task EmptyInstallToProfileResumeAgentVacancyAndTask()
    {
        var directory = Path.Combine(Path.GetTempPath(), "jobflow-api-" + Guid.NewGuid().ToString("N"));
        var previous = Environment.GetEnvironmentVariable("JOBFLOW_DATA_DIR");
        Environment.SetEnvironmentVariable("JOBFLOW_DATA_DIR", directory);
        try
        {
            await using var factory = new WebApplicationFactory<Program>();
            using var client = factory.CreateClient();
            var status = await client.GetFromJsonAsync<JsonElement>("/api/status");
            Assert.Equal(0, status.GetProperty("profiles").GetInt32());
            Assert.False(status.GetProperty("realSubmissionEnabled").GetBoolean());
            Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/profiles", new { name = "Blocked" })).StatusCode);
            client.DefaultRequestHeaders.Add("X-JobFlow-Client", "local-ui");
            var profile = await Post(client, "/api/profiles", new { name = "Synthetic", skills = "C#, SQL" });
            var profileId = profile.GetProperty("id").GetGuid();
            using var upload = new MultipartFormDataContent();
            upload.Add(new StringContent(profileId.ToString()), "profileId");
            upload.Add(new ByteArrayContent(Encoding.ASCII.GetBytes("%PDF-1.4\n% synthetic upload fixture\n%%EOF")), "file", "synthetic.pdf");
            var uploaded = await client.PostAsync("/api/resumes", upload); uploaded.EnsureSuccessStatusCode();
            var version = await uploaded.Content.ReadFromJsonAsync<JsonElement>();
            var platform = await Post(client, "/api/platforms", new { name = "Test", url = "https://example.invalid", adapter = "search-only" });
            var vacancyRequest = new { jobPlatformId = platform.GetProperty("id").GetGuid(), externalId = "test-1", title = "Developer", description = "C# and SQL" };
            var vacancy = await Post(client, "/api/vacancies", vacancyRequest);
            Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync("/api/vacancies", vacancyRequest)).StatusCode);
            var applicationRequest = new { candidateProfileId = profileId, vacancyId = vacancy.GetProperty("id").GetGuid(), resumeVersionId = version.GetProperty("id").GetGuid() };
            var application = await Post(client, "/api/applications", applicationRequest);
            Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync("/api/applications", applicationRequest)).StatusCode);
            var appPath = "/api/applications/" + application.GetProperty("id").GetGuid() + "/transition";
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(appPath, new { state = "Submitted", revision = 0 })).StatusCode);
            await Post(client, appPath, new { state = "Evaluating", revision = 0 });
            Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync(appPath, new { state = "Drafting", revision = 0 })).StatusCode);
            var provider = await Post(client, "/api/providers", new { name = "CLI", kind = "codex-cli", enabled = false });
            var agent = await Post(client, "/api/agents", new { name = "Evaluator", candidateProfileId = profileId, modelProviderId = provider.GetProperty("id").GetGuid() });
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/agents/" + agent.GetProperty("id").GetGuid() + "/execute", new { input = "Synthetic data" })).StatusCode);
            var task = await Post(client, "/api/tasks", new { name = "Health", candidateProfileId = profileId, kind = "HealthCheck", intervalMinutes = 60, enabled = false, timeZone = "UTC" });
            await Post(client, "/api/tasks/" + task.GetProperty("id").GetGuid() + "/run", new { });
            var options = new DbContextOptionsBuilder<JobFlowDb>().UseSqlite(Storage.ConnectionString).Options;
            await using var first = new JobFlowDb(options); await using var second = new JobFlowDb(options);
            var results = await Task.WhenAll(new TaskProcessor(first).RunOneAsync(default), new TaskProcessor(second).RunOneAsync(default));
            Assert.Single(results, x => x);
            Assert.Equal(1, await first.Runs.CountAsync());
            Assert.Equal("Succeeded", (await first.Runs.SingleAsync()).Status);
            var export = await client.GetStringAsync("/api/export/vacancies.csv");
            Assert.Contains("Developer", export);
            var other = await Post(client, "/api/profiles", new { name = "Other" });
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/applications",
                new { candidateProfileId = other.GetProperty("id").GetGuid(), vacancyId = vacancy.GetProperty("id").GetGuid(), resumeVersionId = version.GetProperty("id").GetGuid() })).StatusCode);
        }
        finally { Environment.SetEnvironmentVariable("JOBFLOW_DATA_DIR", previous); Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
    private static async Task<JsonElement> Post(HttpClient client, string path, object value)
    {
        var response = await client.PostAsJsonAsync(path, value);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }
}

