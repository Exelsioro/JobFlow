using System.Text.Json;
using JobFlow.Application;
using JobFlow.Domain;
using JobFlow.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace JobFlow.IntegrationTests;

public class CoverLetterServiceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PartialSourceQuotesAreAcceptedAndUnsupportedClaimsRequireVerifiedCorrection(bool correction)
    {
        await using var f = await Fixture.CreateAsync(correction ? "correct" : "supported");
        var letter = await f.GenerateAsync();
        Assert.Equal("I built a C# application.", letter);
        Assert.Equal(correction ? 4 : 2, f.Agent.Calls);
        Assert.DoesNotContain("B2", letter);
        var validations = await f.Db.Audit.Where(x => x.Action == "CoverLetterValidated").ToListAsync();
        Assert.Equal(correction ? 2 : 1, validations.Count);
        Assert.Contains("\"isValid\":true", validations.Last().Details);
        Assert.All(await f.Db.Set<AgentExecution>().ToListAsync(), x => Assert.NotEmpty(x.Output));
    }

    [Theory]
    [InlineData("unknown-id", 2)]
    [InlineData("unsupported", 4)]
    public async Task InvalidEvidenceCannotPassAfterTheBoundedCorrection(string mode, int calls)
    {
        await using var f = await Fixture.CreateAsync(mode);
        await Assert.ThrowsAsync<HumanReviewRequiredException>(f.GenerateAsync);
        Assert.Equal(calls, f.Agent.Calls);
        Assert.Equal(2, await f.Db.Audit.CountAsync(x => x.Action == "CoverLetterValidated"));
        Assert.All(await f.Db.Set<AgentExecution>().ToListAsync(), x => Assert.NotEmpty(x.Output));
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string file = Path.Combine(Path.GetTempPath(), "jobflow-letter-" + Guid.NewGuid().ToString("N") + ".db");
        public JobFlowDb Db { get; private set; } = null!;
        public Writer Agent { get; private set; } = null!;
        private CandidateProfile profile = null!;
        private Guid writer;
        public static async Task<Fixture> CreateAsync(string mode)
        {
            var f = new Fixture();
            f.Db = new(new DbContextOptionsBuilder<JobFlowDb>().UseSqlite($"Data Source={f.file};Pooling=False").Options);
            await f.Db.Database.MigrateAsync();
            var user = new User(); f.profile = new() { UserId = user.Id, Name = "Synthetic", Dossier = "Built a C# application. English level is not confirmed." };
            var provider = new ModelProvider(); var agent = new AgentProfile { CandidateProfileId = f.profile.Id, ModelProviderId = provider.Id, TaskType = "CoverLetter" };
            f.Db.AddRange(user, f.profile, provider, agent); await f.Db.SaveChangesAsync();
            f.writer = agent.Id; f.Agent = new(f.Db, agent.Id, mode); return f;
        }
        public Task<string> GenerateAsync() => new CoverLetterService(Db, Agent).PrepareAsync(writer, profile, new() { Title = "C# role" },
            new("NeedsReview", "Questions remain", ["C#"], [], [], [], "Medium"), new(), default);
        public async ValueTask DisposeAsync() { await Db.DisposeAsync(); File.Delete(file); }
    }
    public sealed class Writer(JobFlowDb db, Guid id, string mode) : IAgentExecutor
    {
        public int Calls { get; private set; }
        public async Task<AgentExecutionResult> ExecuteAsync(AgentExecutionRequest request, CancellationToken ct)
        {
            Calls++;
            var execution = new AgentExecution { AgentProfileId = id, Status = "Succeeded" }; db.Add(execution); await db.SaveChangesAsync(ct);
            string output;
            if (request.OutputSchema == AgentTemplates.LetterVerificationSchema)
            {
                var supported = mode != "unsupported" && (mode != "correct" || Calls >= 4);
                output = JsonSerializer.Serialize(new { supported, issues = supported ? Array.Empty<string>() : new[] { "English B2 is not confirmed" },
                    unsupportedClaims = supported ? Array.Empty<string>() : new[] { "I speak English at B2" } });
            }
            else
            {
                using var input = JsonDocument.Parse(request.Input);
                var fact = input.RootElement.GetProperty("factCatalog").EnumerateArray().Single(x => x.GetProperty("sourceField").GetString() == "Dossier");
                output = JsonSerializer.Serialize(new { letter = mode == "unsupported" || mode == "correct" && Calls == 1 ?
                    "I built a C# application. I speak English at B2." : "I built a C# application.",
                    citations = new[] { new { factId = mode == "unknown-id" ? "invented" : fact.GetProperty("id").GetString(), quote = "Built a C# application." } } });
            }
            return new(execution.Id, "Succeeded", output);
        }
    }
}
