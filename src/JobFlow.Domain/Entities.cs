namespace JobFlow.Domain;

public abstract class Entity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
public class User : Entity { public string Name { get; set; } = ""; }
public class CandidateProfile : Entity
{
    public string Dossier { get; set; } = "";
    public Guid UserId { get; set; }
    public string Name { get; set; } = "";
    public string Email { get; set; } = "";
    public string Experience { get; set; } = "";
    public string Skills { get; set; } = "";
    public string Education { get; set; } = "";
    public string Languages { get; set; } = "";
    public string PreferredRoles { get; set; } = "";
    public string Geography { get; set; } = "";
    public string Salary { get; set; } = "";
    public string Relocation { get; set; } = "";
    public string ExcludedEmployers { get; set; } = "";
    public string Rules { get; set; } = "";
}
public class Resume : Entity
{
    public Guid CandidateProfileId { get; set; }
    public string Name { get; set; } = "";
    public bool IsPrimary { get; set; }
}
public class ResumeVersion : Entity
{
    public string ExtractedText { get; set; } = "";
    public string Origin { get; set; } = "Uploaded";
    public Guid? SourceResumeVersionId { get; set; }
    public Guid? VacancyId { get; set; }
    public Guid? AgentExecutionId { get; set; }
    public string AdaptationJson { get; set; } = "";
    public string DossierHash { get; set; } = "";
    public Guid ResumeId { get; set; }
    public int Version { get; set; }
    public string StorageKey { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public string OriginalName { get; set; } = "";
}
public class JobPlatform : Entity
{
    public string BrowserMode { get; set; } = "inherit";
    public string Name { get; set; } = "";
    public string Url { get; set; } = "";
    public string Adapter { get; set; } = "search-only";
    public string Geography { get; set; } = "";
    public string SearchFilters { get; set; } = "";
}
public class PlatformAccount : Entity
{
    public Guid CandidateProfileId { get; set; }
    public Guid JobPlatformId { get; set; }
    public Guid? BrowserProfileId { get; set; }
    public string ExternalResumeId { get; set; } = "";
}
public class Vacancy : Entity
{
    public bool IsArchived { get; set; }
    public string Availability { get; set; } = "Unknown";
    public DateTime? VerifiedAt { get; set; }
    public string PublicationEvidence { get; set; } = "";
    public string RequiredSkills { get; set; } = ""; public string Location { get; set; } = ""; public string WorkMode { get; set; } = ""; public string Level { get; set; } = ""; public double? SalaryMax { get; set; } public string Currency { get; set; } = ""; public DateTime? PublishedAt { get; set; }
    public Guid JobPlatformId { get; set; }
    public string ExternalId { get; set; } = "";
    public string Title { get; set; } = "";
    public string Employer { get; set; } = "";
    public string Url { get; set; } = "";
    public string Description { get; set; } = "";
}
public class VacancyEvaluation : Entity
{
    public Guid CandidateProfileId { get; set; }
    public Guid VacancyId { get; set; }
    public string ResultJson { get; set; } = "{}";
}
public enum ApplicationState { Discovered, Evaluating, Rejected, Drafting, ReadyForReview, ReadyToSubmit, Submitting, SubmissionUnknown, Submitted, Blocked, EmployerResponded, Closed, Skipped }
public class JobApplication : Entity
{
    public string ResponseStatus { get; set; } = "";
    public DateTime? ResponseStatusAt { get; set; }
    public bool IsTest { get; set; }
    public Guid? WorkflowDefinitionId { get; set; }
    public Guid CandidateProfileId { get; set; }
    public Guid VacancyId { get; set; }
    public Guid ResumeVersionId { get; set; }
    public ApplicationState State { get; set; }
    public string CoverLetter { get; set; } = "";
    public int Revision { get; set; }
}
public class ApplicationAttempt : Entity
{
    public Guid JobApplicationId { get; set; }
    public string IdempotencyKey { get; set; } = "";
    public string Outcome { get; set; } = "";
    public string Evidence { get; set; } = "";
}
public class EmployerInteraction : Entity
{
    public Guid JobApplicationId { get; set; }
    public string Category { get; set; } = "Uncertain";
    public string SourceEventId { get; set; } = "";
    public string Message { get; set; } = "";
}
public class ModelProvider : Entity
{
    public string Name { get; set; } = "";
    public string Kind { get; set; } = "codex-cli";
    public string Endpoint { get; set; } = "";
    public string Model { get; set; } = "";
    public int TimeoutSeconds { get; set; } = 120;
    public bool Enabled { get; set; }
}
public class AgentProfile : Entity
{
    public string ResponseLanguage { get; set; } = "ru";
    public bool UseTaskModelPresets { get; set; }
    public string ToolBindingsJson { get; set; } = "[]";
    public Guid CandidateProfileId { get; set; }
    public Guid ModelProviderId { get; set; }
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string TaskType { get; set; } = "Evaluation";
    public string Model { get; set; } = "";
    public string Reasoning { get; set; } = "";
    public string SystemPrompt { get; set; } = "";
    public string UserPromptTemplate { get; set; } = "{{input}}";
    public string OutputSchema { get; set; } = """{"type":"object","properties":{"summary":{"type":"string"}},"required":["summary"],"additionalProperties":false}""";
    public bool Enabled { get; set; }
    public int Version { get; set; } = 1;
}
public class AgentExecution : Entity
{
    public string Operation { get; set; } = "";
    public string Reasoning { get; set; } = "";
    public string ResponseLanguage { get; set; } = "";
    public string TaskInstructions { get; set; } = "";
    public Guid AgentProfileId { get; set; }
    public int ConfigurationVersion { get; set; }
    public string ConfigurationJson { get; set; } = "";
    public string Model { get; set; } = "";
    public string Status { get; set; } = "Pending";
    public string Output { get; set; } = "";
    public string Error { get; set; } = "";
}
public class ToolConnection : Entity
{
    public bool Enabled { get; set; } public string ToolsJson { get; set; } = "[]"; public string Status { get; set; } = "NotChecked"; public DateTime? LastCheckedAt { get; set; }
    public string Name { get; set; } = "";
    public string Transport { get; set; } = "stdio";
    public string EndpointOrCommand { get; set; } = "";
    public string ArgumentsJson { get; set; } = "[]";
    public string PermissionsJson { get; set; } = "[]";
}
public class BrowserProfile : Entity { public string Name { get; set; } = ""; }
public class ScheduledTask : Entity
{
    public bool PrepareOutsideAgeLimit { get; set; }
    public Guid? VacancyId { get; set; }
    public string ScheduleKind { get; set; } = "interval";
    public string LocalTime { get; set; } = "09:00";
    public string DaysOfWeek { get; set; } = "1,2,3,4,5";
    public Guid? IntegrationId { get; set; } public int TimeoutSeconds { get; set; } = 300; public int MaxRetries { get; set; } = 2; public int FailureCount { get; set; } public bool CancelRequested { get; set; }
    public Guid? WorkflowDefinitionId { get; set; }
    public Guid CandidateProfileId { get; set; }
    public string Name { get; set; } = "";
    public string Kind { get; set; } = "HealthCheck";
    public int IntervalMinutes { get; set; } = 60;
    public string TimeZone { get; set; } = "UTC";
    public bool Enabled { get; set; }
    public DateTime NextRunAt { get; set; } = DateTime.UtcNow;
    public bool RunRequested { get; set; }
    public DateTime? LeaseUntil { get; set; }
    public string? LeaseToken { get; set; }
}
public class TaskRun : Entity
{
    public Guid ScheduledTaskId { get; set; }
    public string Status { get; set; } = "Running";
    public DateTime? FinishedAt { get; set; }
    public string Message { get; set; } = "";
}
public class Integration : Entity
{
    public string Instructions { get; set; } = "";
    public Guid? MailAgentId { get; set; }
    public Guid? JobPlatformId { get; set; } public string MappingJson { get; set; } = "{\"externalId\":\"Id\",\"title\":\"Title\"}"; public string ConflictPolicy { get; set; } = "keep-local"; public string LastStatus { get; set; } = "NotChecked"; public DateTime? LastCheckedAt { get; set; } public string Endpoint { get; set; } = ""; public bool Enabled { get; set; }
    public Guid CandidateProfileId { get; set; }
    public string Name { get; set; } = "";
    public string Kind { get; set; } = "csv";
    public bool ImportConfirmed { get; set; }
}
public class AuditEvent : Entity
{
    public string EntityType { get; set; } = "";
    public Guid EntityId { get; set; }
    public string Action { get; set; } = "";
    public string Details { get; set; } = "";
}
