using JobFlow.Domain;
namespace JobFlow.Application;

public static class Workflow
{
    public static bool CanTransition(ApplicationState from, ApplicationState to) => (from, to) switch
    {
        (ApplicationState.Discovered, ApplicationState.Evaluating) => true,
        (ApplicationState.Evaluating, ApplicationState.Rejected or ApplicationState.Drafting or ApplicationState.Blocked) => true,
        (ApplicationState.Drafting, ApplicationState.ReadyForReview or ApplicationState.Blocked) => true,
        (ApplicationState.ReadyForReview, ApplicationState.Drafting or ApplicationState.ReadyToSubmit or ApplicationState.Rejected) => true,
        (ApplicationState.ReadyToSubmit, ApplicationState.Submitting or ApplicationState.Blocked) => true,
        (ApplicationState.Submitting, ApplicationState.Submitted or ApplicationState.SubmissionUnknown or ApplicationState.Blocked) => true,
        (ApplicationState.SubmissionUnknown, ApplicationState.Submitted or ApplicationState.Closed) => true,
        (ApplicationState.Submitted, ApplicationState.EmployerResponded or ApplicationState.Closed) => true,
        (ApplicationState.EmployerResponded, ApplicationState.Closed) => true,
        (ApplicationState.Blocked, ApplicationState.Drafting or ApplicationState.Closed) => true,
        _ => false
    };
    public static void Transition(JobApplication application, ApplicationState target)
    {
        if (!CanTransition(application.State, target))
            throw new InvalidOperationException($"Transition {application.State} -> {target} is not permitted.");
        application.State = target;
        application.Revision++;
    }
}
public record ModelRequest(string Prompt, string Schema, string Model, string Reasoning, int TimeoutSeconds);
public record ModelResponse(string Json);
public interface IModelProvider { Task<ModelResponse> GenerateAsync(ModelRequest request, CancellationToken cancellationToken); }
public record AgentExecutionRequest(Guid AgentProfileId, string Input);
public record AgentExecutionResult(Guid ExecutionId, string Status, string Output);
public interface IAgentExecutor { Task<AgentExecutionResult> ExecuteAsync(AgentExecutionRequest request, CancellationToken cancellationToken); }
public record BrowserResult(bool Confirmed, string Evidence);
public interface IBrowserAutomationProvider { Task<BrowserResult> DryRunAsync(CancellationToken cancellationToken); }
public interface IJobDiscoveryProvider { Task<IReadOnlyList<Vacancy>> DiscoverAsync(JobPlatform platform, CancellationToken cancellationToken); }
public interface IJobApplicationProvider { Task<BrowserResult> SubmitAsync(JobApplication application, CancellationToken cancellationToken); }
public interface IEmployerFeedbackProvider { Task<IReadOnlyList<EmployerInteraction>> PollAsync(CancellationToken cancellationToken); }
public interface IExternalJournalProvider { Task<string> ExportAsync(IReadOnlyList<Vacancy> vacancies, CancellationToken cancellationToken); }

public record EvaluationResult(string Decision, string Rationale, string[] Matches, string[] MissingSkills, string[] UnverifiedRequirements, string[] Clarifications, string Confidence);
public static class ConservativeEvaluator
{
    public static EvaluationResult Evaluate(CandidateProfile profile, Vacancy vacancy)
    {
        var excluded = profile.ExcludedEmployers.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (excluded.Any(x => string.Equals(x, vacancy.Employer, StringComparison.OrdinalIgnoreCase)))
            return new("Reject", "Employer is excluded by this candidate profile.", [], [], [], [], "High");
        var matches = profile.Skills.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Where(x => vacancy.Description.Contains(x, StringComparison.OrdinalIgnoreCase)).ToArray();
        return new("NeedsReview", "Keyword overlap is not proof of experience or eligibility. Review the full requirements.", matches, [],
            ["Experience, seniority and mandatory requirements have not been verified."],
            ["Confirm salary, location, work format, publication date and profile-specific rules."], "Low");
    }
}
