using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using JobFlow.Application;
using JobFlow.Domain;
using JobFlow.Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace JobFlow.IntegrationTests;

public class CliModelCatalogTests
{
    private static CliModels Models() => new([new("synthetic-model", "Synthetic Model", true, "low", ["low", "medium"])], DateTime.UtcNow);

    [Fact]
    public async Task ConcurrentReadsShareCacheAndFailedRefreshDoesNotReplaceIt()
    {
        var source = new FakeSource();
        var catalog = new CodexModelCatalog(source);
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => catalog.GetAsync(false, default)));
        Assert.Equal(1, source.Calls);
        Assert.All(results, x => Assert.Equal("synthetic-model", x.DefaultModel));
        source.Fail = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => catalog.GetAsync(true, default));
        Assert.Same(results[0], await catalog.GetAsync(false, default));
        source.Fail = false;
        Assert.NotSame(results[0], await catalog.GetAsync(true, default));
        Assert.Equal(3, source.Calls);
    }

    [Fact]
    public async Task EndpointUsesSelectedProviderAndSupportsDisabledProviderWithoutModelTurn()
    {
        var directory = Path.Combine(Path.GetTempPath(), "jobflow-model-api-" + Guid.NewGuid().ToString("N"));
        var previous = Environment.GetEnvironmentVariable("JOBFLOW_DATA_DIR");
        Environment.SetEnvironmentVariable("JOBFLOW_DATA_DIR", directory);
        try
        {
            var source = new FakeSource();
            await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            { services.RemoveAll<ICliModelSource>(); services.AddSingleton<ICliModelSource>(source); }));
            using var client = factory.CreateClient();
            client.DefaultRequestHeaders.Add("X-JobFlow-Client", "local-ui");
            var created = await client.PostAsJsonAsync("/api/providers", new { name = "Synthetic CLI", kind = "codex-cli", enabled = false });
            created.EnsureSuccessStatusCode();
            var provider = await created.Content.ReadFromJsonAsync<JsonElement>();
            var id = provider.GetProperty("id").GetString();
            var response = await client.GetFromJsonAsync<CliModels>("/api/provider-models?providerId=" + id + "&kind=unsupported");
            Assert.Equal("synthetic-model", response!.DefaultModel);
            Assert.Equal(new[] { "low", "medium" }, Assert.Single(response.Models).SupportedReasoningEfforts);
            Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/provider-models?providerId=" + Guid.NewGuid())).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/provider-models?kind=unsupported")).StatusCode);
            source.Fail = true;
            Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/provider-models?kind=codex-cli&refresh=true")).StatusCode);
            Assert.Equal(2, source.Calls);
            var profileResponse = await client.PostAsJsonAsync("/api/profiles", new { name = "Synthetic" });
            profileResponse.EnsureSuccessStatusCode();
            var profile = await profileResponse.Content.ReadFromJsonAsync<JsonElement>();
            var agent = new { name = "Synthetic agent", candidateProfileId = profile.GetProperty("id").GetGuid(), modelProviderId = Guid.Parse(id!), reasoning = "ultra" };
            Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/agents", agent)).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/agents", new { agent.name, agent.candidateProfileId, agent.modelProviderId, reasoning = "unknown-effort" })).StatusCode);
            using var scope = factory.Services.CreateScope();
            Assert.Equal(0, await scope.ServiceProvider.GetRequiredService<JobFlowDb>().Set<AgentExecution>().CountAsync());
        }
        finally
        {
            Environment.SetEnvironmentVariable("JOBFLOW_DATA_DIR", previous);
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Theory]
    [InlineData("agent-choice", "provider-choice", "agent-choice", 0)]
    [InlineData("", "provider-choice", "provider-choice", 0)]
    [InlineData("", "", "synthetic-model", 1)]
    public async Task ExecutorPassesAndRecordsResolvedModel(string agentModel, string providerModel, string expected, int catalogReads)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:"); await connection.OpenAsync();
        await using var db = new JobFlowDb(new DbContextOptionsBuilder<JobFlowDb>().UseSqlite(connection).Options);
        await db.Database.MigrateAsync();
        var owner = new User(); var candidate = new CandidateProfile { UserId = owner.Id };
        var provider = new ModelProvider { Model = providerModel, Enabled = true };
        var agent = new AgentProfile { CandidateProfileId = candidate.Id, ModelProviderId = provider.Id, Model = agentModel, Enabled = true };
        db.AddRange(owner, candidate, provider, agent); await db.SaveChangesAsync();
        var source = new FakeSource(); var modelProvider = new FakeProvider();
        await new AgentExecutor(db, modelProvider, new CodexModelCatalog(source)).ExecuteAsync(new(agent.Id, "Synthetic input"), default);
        Assert.Equal(expected, modelProvider.Request!.Model);
        Assert.Equal(expected, (await db.Set<AgentExecution>().SingleAsync()).Model);
        var completion = await db.Audit.SingleAsync(x => x.Action == "AgentExecutionCompleted");
        using var completionJson = System.Text.Json.JsonDocument.Parse(completion.Details);
        Assert.Equal("Succeeded", completionJson.RootElement.GetProperty("status").GetString());
        Assert.Equal(expected, completionJson.RootElement.GetProperty("model").GetString());
        Assert.True(completionJson.RootElement.GetProperty("elapsedMs").GetInt64() >= 0);
        Assert.DoesNotContain("Synthetic input", completion.Details);
        Assert.Equal(catalogReads, source.Calls);
    }

    private sealed class FakeSource : ICliModelSource
    {
        public int Calls; public bool Fail;
        public async Task<CliModels> ReadAsync(CancellationToken ct)
        {
            Calls++; await Task.Delay(10, ct);
            if (Fail) throw new InvalidOperationException("Synthetic catalog unavailable");
            return Models();
        }
    }
    private sealed class FakeProvider : IModelProvider
    {
        public ModelRequest? Request;
        public Task<ModelResponse> GenerateAsync(ModelRequest request, CancellationToken ct)
        { Request = request; return Task.FromResult(new ModelResponse("{}")); }
    }
}
