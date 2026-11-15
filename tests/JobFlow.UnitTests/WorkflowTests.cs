using JobFlow.Application;
using JobFlow.Domain;
namespace JobFlow.UnitTests;
public class WorkflowTests
{
    [Theory]
    [InlineData(ApplicationState.SubmissionUnknown, ApplicationState.Submitting)]
    [InlineData(ApplicationState.Submitted, ApplicationState.Submitting)]
    [InlineData(ApplicationState.Discovered, ApplicationState.Submitted)]
    [InlineData(ApplicationState.ReadyForReview, ApplicationState.Submitted)]
    public void UnsafeTransitionsAreRejected(ApplicationState from, ApplicationState to)
    {
        var application = new JobApplication { State = from };
        Assert.Throws<InvalidOperationException>(() => Workflow.Transition(application, to));
        Assert.Equal(from, application.State);
    }
    [Fact] public void ConfirmedSubmissionRequiresSubmittingState()
    {
        var application = new JobApplication { State = ApplicationState.Submitting };
        Workflow.Transition(application, ApplicationState.Submitted);
        Assert.Equal(1, application.Revision);
    }
    [Fact] public void UnknownOutcomeCannotBeRetried()
    {
        var application = new JobApplication { State = ApplicationState.Submitting };
        Workflow.Transition(application, ApplicationState.SubmissionUnknown);
        Assert.False(Workflow.CanTransition(application.State, ApplicationState.ReadyToSubmit));
    }
    [Fact] public void ExclusionsAreProfileSpecific()
    {
        var vacancy = new Vacancy { Employer = "Example", Description = "C#" };
        Assert.Equal("Reject", ConservativeEvaluator.Evaluate(new() { ExcludedEmployers = "Example" }, vacancy).Decision);
        Assert.Equal("NeedsReview", ConservativeEvaluator.Evaluate(new() { Skills = "C#" }, vacancy).Decision);
    }
}
