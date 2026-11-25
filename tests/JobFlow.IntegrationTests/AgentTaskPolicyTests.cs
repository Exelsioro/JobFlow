using JobFlow.Application;
using JobFlow.Domain;
using JobFlow.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace JobFlow.IntegrationTests;

public class AgentTaskPolicyTests
{
    private static CliModels Catalog()=>new([
        new("gpt-6-luna","Luna",false,"medium",["low","medium","high"]),
        new("gpt-6.1-terra","Terra",false,"low",["low","medium","high"]),
        new("gpt-6.2-sol","Sol",true,"low",["low","medium","high"])],DateTime.UtcNow);

    [Theory]
    [InlineData("VacancyFreshness","gpt-6-luna","high")]
    [InlineData("BrowserAutomation/search","gpt-6.1-terra","medium")]
    [InlineData("BrowserAutomation/visual-search","gpt-6.1-terra","medium")]
    [InlineData("ResumeSelection","gpt-6-luna","high")]
    [InlineData("ResumeAdaptation","gpt-6.2-sol","medium")]
    [InlineData("CoverLetter","gpt-6.1-terra","low")]
    [InlineData("CoverLetterVerification","gpt-6.2-sol","medium")]
    [InlineData("MailAnalysis","gpt-6-luna","high")]
    public void SelectionUsesAvailableModelsForTheActualOperation(string operation,string model,string reasoning)
    {var result=AgentTaskPolicy.Resolve(operation,Catalog());Assert.Equal(model,result.Model);Assert.Equal(reasoning,result.Reasoning);}

    [Fact]
    public void MissingModelFallsBackOnlyToAvailableModelAndSupportedReasoning()
    {
        var result=AgentTaskPolicy.Resolve("VacancyFreshness",new([new("gpt-6.2-sol","Sol",true,"low",["low"])],DateTime.UtcNow));
        Assert.Equal("gpt-6.2-sol",result.Model);Assert.Equal("low",result.Reasoning);
        Assert.Throws<InvalidOperationException>(()=>AgentTaskPolicy.Resolve("Evaluation",new([],DateTime.UtcNow)));
    }

    [Fact]
    public async Task VisualImageAndSchemaReachTheProviderAndExecutionHistory()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:"); await connection.OpenAsync();
        await using var db = new JobFlowDb(new DbContextOptionsBuilder<JobFlowDb>().UseSqlite(connection).Options); await db.Database.EnsureCreatedAsync();
        var user = new User(); var profile = new CandidateProfile { UserId = user.Id }; var provider = new ModelProvider { Enabled = true };
        var agent = new AgentProfile { CandidateProfileId = profile.Id, ModelProviderId = provider.Id, Enabled = true, TaskType = "BrowserAutomation", UseTaskModelPresets = true };
        db.AddRange(user, profile, provider, agent); await db.SaveChangesAsync();
        var model = new CaptureProvider();
        byte[] image = [137, 80, 78, 71, 13, 10, 26, 10];
        await new AgentExecutor(db, model, new FixedCatalog()).ExecuteAsync(new(agent.Id, "{\"phase\":\"visual-search\"}", "One visual action", BrowserWorkflowDriver.VisualActionSchema) { ImagePng = image }, default);
        Assert.Same(image, model.Request!.ImagePng);
        Assert.Equal(BrowserWorkflowDriver.VisualActionSchema, model.Request.Schema);
        Assert.Equal("gpt-6.1-terra", model.Request.Model);
        Assert.Equal("BrowserAutomation/visual-search", (await db.Set<AgentExecution>().SingleAsync()).Operation);
    }

    [Fact]
    public async Task ActualModelReasoningAndResponseLanguageAreRecordedAndDocumentsHaveIndependentLanguage()
    {
        await using var connection=new SqliteConnection("Data Source=:memory:");await connection.OpenAsync();
        await using var db=new JobFlowDb(new DbContextOptionsBuilder<JobFlowDb>().UseSqlite(connection).Options);await db.Database.EnsureCreatedAsync();
        var user=new User();var profile=new CandidateProfile{UserId=user.Id};var provider=new ModelProvider{Enabled=true,Model="gpt-6.2-sol"};
        var agent=new AgentProfile{CandidateProfileId=profile.Id,ModelProviderId=provider.Id,Enabled=true,TaskType="Resume",ResponseLanguage="ru",UseTaskModelPresets=true};
        db.AddRange(user,profile,provider,agent);await db.SaveChangesAsync();
        var model=new CaptureProvider();var executor=new AgentExecutor(db,model,new FixedCatalog());
        await executor.ExecuteAsync(new(agent.Id,"{}","Select supplied document.",ResumeAgentService.SelectionSchema),default);
        Assert.Equal("gpt-6-luna",model.Request!.Model);Assert.Equal("high",model.Request.Reasoning);
        Assert.Contains("русский",model.Request.Prompt);Assert.Contains("NEVER selects the language",model.Request.Prompt);Assert.Contains("evidence quotations unchanged",model.Request.Prompt);
        var execution=await db.Set<AgentExecution>().SingleAsync();Assert.Equal("ResumeSelection",execution.Operation);Assert.Equal("high",execution.Reasoning);Assert.Equal("ru",execution.ResponseLanguage);
        Assert.Contains("Language policy",execution.TaskInstructions);
    }

    [Theory]
    [InlineData("Browser read: Vacancy is closed or archived.","Information")]
    [InlineData("Browser read: Publication date exceeds the configured age limit.","Information")]
    [InlineData("Resume verification requires attention: unsupported claim", "Review")]
    [InlineData("Synthetic connection failure", "Error")]
    public void LegacyAttentionNotificationsHaveMeaningfulSeverity(string message,string kind)
    {
        var result=NotificationQueries.Present(new Notification{Kind="Attention",Message=message},new Vacancy{Title="Synthetic role"});
        Assert.Equal(kind,result.Kind);
    }

    [Fact]
    public void EvidenceAllowsUnicodeWhitespaceButNotChangedFacts()
    {
        Assert.True(BrowserWorkflowDriver.IsClosed("Вакансия в\u00a0архиве и\u00a0больше не\u00a0принимает отклики"));
        Assert.False(BrowserWorkflowDriver.IsClosed("Вакансия открыта. Разработка архивов документов."));
        Assert.True(EvidenceText.Contains("SQL\u00a0Server\nexperience","SQL Server experience"));
        Assert.False(EvidenceText.Contains("7 years of experience","8 years of experience"));
        Assert.False(EvidenceText.Contains("No commercial PostgreSQL","commercial SQL Server"));
    }
    private sealed class FixedCatalog:ICliModelCatalog{public Task<CliModels> GetAsync(bool refresh,CancellationToken ct)=>Task.FromResult(Catalog());}
    private sealed class CaptureProvider:IModelProvider
    {public ModelRequest? Request;public Task<ModelResponse> GenerateAsync(ModelRequest request,CancellationToken ct){Request=request;return Task.FromResult(new ModelResponse("{}"));}}
}
