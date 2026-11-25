using JobFlow.Application;
using JobFlow.Domain;

namespace JobFlow.UnitTests;

public class LetterEvidenceTests
{
    [Fact]
    public void ExactPartialQuoteUsesStableFactIdWithoutCopyingTheEntireDossier()
    {
        var profile = new CandidateProfile { Dossier = "Built a C# application. No Kubernetes experience." };
        var facts = LetterEvidence.Facts(profile);
        var draft = new LetterDraft("I built a C# application.", [new(facts[0].Id, "Built a C# application.")]);
        Assert.Empty(LetterEvidence.Validate(draft, facts));
        profile.Skills = "SQL";
        Assert.Equal(facts[0].Id, LetterEvidence.Facts(profile).Single(x => x.SourceField == "Dossier").Id);
    }

    [Theory]
    [InlineData("invented-id", "Built a C# application.")]
    [InlineData("correct", "I have ten years of Kubernetes experience.")]
    [InlineData("correct", "")]
    public void UnknownIdsFabricatedQuotesAndEmptyEvidenceAreRejected(string id, string quote)
    {
        var facts = LetterEvidence.Facts(new CandidateProfile { Dossier = "Built a C# application." });
        Assert.NotEmpty(LetterEvidence.Validate(new("Candidate letter", [new(id == "correct" ? facts[0].Id : id, quote)]), facts));
    }
}
