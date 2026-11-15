namespace JobFlow.Domain;
public class JournalImport : Entity
{
    public Guid IntegrationId { get; set; }
    public string Status { get; set; } = "Preview";
    public string ContentHash { get; set; } = "";
    public string RowsJson { get; set; } = "[]";
    public int RowCount { get; set; }
    public int Added { get; set; }
    public int Duplicates { get; set; }
    public int Conflicts { get; set; }
    public DateTime? CommittedAt { get; set; }
}
public class HistoricalApplication : Entity
{
    public Guid IntegrationId { get; set; }
    public Guid CandidateProfileId { get; set; }
    public Guid VacancyId { get; set; }
    public string SourceState { get; set; } = "";
    public string SourceKey { get; set; } = "";
}
public class FeedbackEvent : Entity
{
    public Guid IntegrationId { get; set; }
    public Guid CandidateProfileId { get; set; }
    public string SourceEventId { get; set; } = "";
    public string Subject { get; set; } = "";
    public string Body { get; set; } = "";
    public DateTime ReceivedAt { get; set; }
    public bool NeedsAction { get; set; } = true;
}
