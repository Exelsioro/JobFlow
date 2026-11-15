namespace JobFlow.Domain;
public class WorkflowDefinition : Entity
{
    public string Name { get; set; } = "";
    public Guid CandidateProfileId { get; set; }
    // Legacy primary platform is retained for existing workflows and API clients.
    public Guid JobPlatformId { get; set; }
    public Guid[] JobPlatformIds { get; set; } = [];
    public Guid[] SelectedPlatforms() => JobPlatformIds.Length == 0 && JobPlatformId != Guid.Empty
        ? [JobPlatformId]
        : JobPlatformIds;
    public Guid ResumeVersionId { get; set; }
    public Guid? EvaluationAgentId { get; set; }
    public Guid? LetterAgentId { get; set; }
    public string RulesJson { get; set; } = "{}";
    public bool Enabled { get; set; }
    public DateTime? LeaseUntil { get; set; }
    public string? LeaseToken { get; set; }
    public int Revision { get; set; }
}
public class WorkflowRun : Entity
{
    public Guid WorkflowDefinitionId { get; set; }
    public Guid CandidateProfileId { get; set; }
    public string Status { get; set; } = "Running";
    public string Stage { get; set; } = "Discovery";
    public int Prepared { get; set; }
    public int Rejected { get; set; }
    public string Error { get; set; } = "";
    public DateTime? FinishedAt { get; set; }
}
