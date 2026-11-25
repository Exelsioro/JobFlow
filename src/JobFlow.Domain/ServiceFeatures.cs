namespace JobFlow.Domain;

public class ServiceState : Entity
{
    public string Role { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public DateTime? HeartbeatAt { get; set; }
    public string Status { get; set; } = "Offline";
    public int ProcessId { get; set; }
}

public class Notification : Entity
{
    public Guid? CandidateProfileId { get; set; }
    public string Kind { get; set; } = "Information";
    public string Title { get; set; } = "";
    public string Message { get; set; } = "";
    public string Url { get; set; } = "";
    public Guid? RelatedId { get; set; }
    public DateTime? ReadAt { get; set; }
}

public class DossierProposal : Entity
{
    public Guid CandidateProfileId { get; set; }
    public Guid ResumeVersionId { get; set; }
    public Guid AgentExecutionId { get; set; }
    public string FactsJson { get; set; } = "[]";
    public string ProfileJson { get; set; } = "{}";
    public DateTime? ConfirmedAt { get; set; }
}
