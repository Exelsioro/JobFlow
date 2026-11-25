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
    public string ResumeMode { get; set; } = "fixed";
    public Guid? ResumeAgentId { get; set; }
    public string Instructions { get; set; } = "";
    public int? FreshnessDays { get; set; } = 7;
    public Guid? EvaluationAgentId { get; set; }
    public Guid? LetterAgentId { get; set; }
    public string ExecutionMode { get; set; } = "prepare";
    public string VacancySource { get; set; } = "search";
    public string ApplicationStatusFilter { get; set; } = "pending";
    public const string ApprovedStatusFilter = "ReadyToSubmit";
    public bool SendsApprovedOnly() => ApplicationStatusFilter == ApprovedStatusFilter;
    [System.ComponentModel.DataAnnotations.Schema.NotMapped]
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IgnorePublicationAge { get; set; }
    public string EvaluationResultFilter { get; set; } = "all";
    public Guid? BrowserProfileId { get; set; }
    public Guid? BrowserAgentId { get; set; }
    public bool AutoSubmit { get; set; }
    public bool SubmitExternalApplications { get; set; }
    public bool CheckMail { get; set; } = true;
    public int MaxApplicationsPerRun { get; set; } = 10;
    public int MaxVacanciesPerPlatform { get; set; } = 50;
    public int MaxVacancyChecksPerRun { get; set; } = 10;
    public int MaxBrowserSteps { get; set; } = 40;
    public string RulesJson { get; set; } = "{}";
    public bool Enabled { get; set; }
    public DateTime? LeaseUntil { get; set; }
    public string? LeaseToken { get; set; }
    public int Revision { get; set; }
}
public class WorkflowRun : Entity
{
    public int Skipped { get; set; }
    public int Errors { get; set; }
    public string AttentionDetails { get; set; } = "";
    public Guid? TargetVacancyId { get; set; }
    public bool PreparationOnly { get; set; }
    public Guid WorkflowDefinitionId { get; set; }
    public Guid CandidateProfileId { get; set; }
    public string Status { get; set; } = "Running";
    public string Stage { get; set; } = "Discovery";
    public int Prepared { get; set; }
    public int Discovered { get; set; }
    public int Checked { get; set; }
    public int Rejected { get; set; }
    public int Submitted { get; set; }
    public int NeedsAttention { get; set; }
    public string Error { get; set; } = "";
    public DateTime? FinishedAt { get; set; }
}
