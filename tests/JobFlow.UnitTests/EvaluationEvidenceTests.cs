using System.Text.Json;
using JobFlow.Application;
using JobFlow.Domain;

namespace JobFlow.UnitTests;

public class EvaluationEvidenceTests
{
    private static EvaluationResult Proposed(params string[] skills) => new("Proceed", "Supplied facts match.", skills, [], [], [], "High");

    [Fact]
    public void StructuredRulesUseTheSameNamesWithoutUpgradingVersions()
    {
        var profile = new CandidateProfile { Skills = "C#; MS SQL Server; .NET 8" };
        var result = RuleEvaluator.Evaluate(profile, new() { RequiredSkills = "CSharp, SQL Server, .NET 10" }, new(), DateTime.UtcNow);
        Assert.Equal(new[] { "C#", "MS SQL Server" }, result.Matches);
        Assert.Equal(".NET 10", Assert.Single(result.MissingSkills));
    }

    [Theory]
    [InlineData("C#", "CSharp")]
    [InlineData("MS SQL Server", "SQL Server")]
    [InlineData("EF Core", "Entity Framework Core")]
    [InlineData("WPF", "Windows Presentation Foundation")]
    [InlineData(".NET 8", "dotnet 8.0")]
    [InlineData("ASP.NET Core", " ASP.NET\u00a0Core ")]
    public void EquivalentNamesRetainTheCandidateNameAndSource(string candidate, string claim)
    {
        var id = Guid.NewGuid();
        var result = EvaluationEvidence.Validate(new() { Skills = candidate }, Proposed(claim), id);
        Assert.Equal("Proceed", result.Decision);
        Assert.Equal(candidate, Assert.Single(result.Matches));
        Assert.True(result.Validation!.IsValid);
        Assert.Equal(id, result.Validation.AgentExecutionId);
        var check = Assert.Single(result.Validation.Skills);
        Assert.Equal("Skills", check.SourceField);
        Assert.Equal(candidate, check.SourceValue);
        Assert.Equal(claim, Assert.Single(result.Validation.AgentMatches));
    }

    [Theory]
    [InlineData("C#", "C++")]
    [InlineData(".NET", ".NET 8")]
    [InlineData(".NET 8", ".NET 10")]
    [InlineData(".NET Core", ".NET Framework")]
    [InlineData("EF Core", "Entity Framework")]
    [InlineData("JavaScript", "Java")]
    [InlineData("AWS (familiarity)", "AWS")]
    [InlineData("C#", "5 years of C# experience")]
    public void RelatedTechnologyVersionsAndProficiencyAreNeverInferred(string candidate, string claim)
    {
        var result = EvaluationEvidence.Validate(new() { Skills = candidate }, Proposed(claim), Guid.NewGuid());
        Assert.Equal("NeedsReview", result.Decision);
        Assert.Empty(result.Matches);
        Assert.False(result.Validation!.IsValid);
        Assert.Equal("Unsupported", Assert.Single(result.Validation.Skills).Status);
        Assert.Contains(result.Clarifications, x => x.Contains(claim));
    }

    [Fact]
    public void AQuotedMentionAndNegationInDossierAreNotProofOfExperience()
    {
        var profile = new CandidateProfile { Skills = "C#", Dossier = "No experience with Kubernetes. JavaScript project." };
        var result = EvaluationEvidence.Validate(profile, Proposed("Kubernetes", "Java"), Guid.NewGuid());
        Assert.False(result.Validation!.IsValid);
        Assert.Contains("Dossier", result.Validation.Skills[0].MentionedIn);
        Assert.Empty(result.Validation.Skills[1].MentionedIn);
        Assert.Equal("NeedsReview", result.Decision);
        Assert.Empty(result.Matches);
    }

    [Fact]
    public void CanonicalMatchesDeduplicateAndSurviveStoredJsonRoundTrip()
    {
        var result = EvaluationEvidence.Validate(new() { Skills = " C#;\n MS SQL Server " }, Proposed("CSharp", "C#", "MSSQL"), Guid.NewGuid());
        var stored = JsonSerializer.Deserialize<EvaluationResult>(JsonSerializer.Serialize(result))!;
        Assert.Equal(new[] { "C#", "MS SQL Server" }, stored.Matches);
        Assert.Equal(3, stored.Validation!.Skills.Length);
        Assert.Equal("Proceed", stored.Validation.AgentDecision);
    }

    [Fact]
    public void UnverifiedRequirementsPreventAutomaticProceedEvenWithValidSkills()
    {
        var result = EvaluationEvidence.Validate(new() { Skills = "C#" }, Proposed("C#") with { UnverifiedRequirements = ["Management experience"] }, Guid.NewGuid());
        Assert.True(result.Validation!.IsValid);
        Assert.Equal("NeedsReview", result.Decision);
        Assert.Equal("Proceed", result.Validation.AgentDecision);
        Assert.NotEmpty(result.Validation.Warnings);
    }

    [Fact]
    public void OptionalLearningGapDoesNotPreventProceedWhenMandatoryFactsAreConfirmed()
    {
        var proposed = Proposed("C#") with { Clarifications = ["Optional tool: learn the employer's preferred editor"] };
        var result = EvaluationEvidence.Validate(new() { Skills = "C#" }, proposed, Guid.NewGuid());
        Assert.Equal("Proceed", result.Decision);
        Assert.True(result.Validation!.IsValid);
        Assert.Empty(result.UnverifiedRequirements);
        Assert.Equal(proposed.Clarifications, result.Clarifications);
    }

    [Fact]
    public void OversizedMatchesCannotBeSilentlyAcceptedAfterTruncation()
    {
        var result = EvaluationEvidence.Validate(new() { Skills = "C#" }, Proposed(Enumerable.Repeat("C#", 101).ToArray()), Guid.NewGuid());
        Assert.False(result.Validation!.IsValid);
        Assert.True(result.Validation.Truncated);
        Assert.Equal(100, result.Validation.AgentMatches.Length);
        Assert.Equal("NeedsReview", result.Decision);
    }
}
