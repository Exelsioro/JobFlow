using JobFlow.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
namespace JobFlow.Infrastructure;

public class JobFlowDb(DbContextOptions<JobFlowDb> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<CandidateProfile> Profiles => Set<CandidateProfile>();
    public DbSet<Resume> Resumes => Set<Resume>();
    public DbSet<ResumeVersion> ResumeVersions => Set<ResumeVersion>();
    public DbSet<JobPlatform> Platforms => Set<JobPlatform>();
    public DbSet<Vacancy> Vacancies => Set<Vacancy>();
    public DbSet<JobApplication> Applications => Set<JobApplication>();
    public DbSet<AgentProfile> Agents => Set<AgentProfile>();
    public DbSet<ModelProvider> Providers => Set<ModelProvider>();
    public DbSet<ScheduledTask> Tasks => Set<ScheduledTask>();
    public DbSet<TaskRun> Runs => Set<TaskRun>();
    public DbSet<AuditEvent> Audit => Set<AuditEvent>();
    protected override void OnModelCreating(ModelBuilder b)
    {
        foreach (var type in typeof(Entity).Assembly.GetTypes().Where(t => t.IsSubclassOf(typeof(Entity)) && !t.IsAbstract))
            b.Entity(type);
        b.Entity<ServiceState>().HasIndex(x => x.Role).IsUnique();
        b.Entity<JobPlatform>().Property(x => x.BrowserMode).HasDefaultValue("inherit");
        b.Entity<AgentProfile>().Property(x => x.ResponseLanguage).HasDefaultValue("ru");
        b.Entity<ResumeVersion>().HasOne<ResumeVersion>().WithMany().HasForeignKey(x => x.SourceResumeVersionId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<ResumeVersion>().HasOne<Vacancy>().WithMany().HasForeignKey(x => x.VacancyId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<ResumeVersion>().HasOne<AgentExecution>().WithMany().HasForeignKey(x => x.AgentExecutionId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<DossierProposal>().HasOne<CandidateProfile>().WithMany().HasForeignKey(x => x.CandidateProfileId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<DossierProposal>().HasOne<ResumeVersion>().WithMany().HasForeignKey(x => x.ResumeVersionId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<DossierProposal>().HasOne<AgentExecution>().WithMany().HasForeignKey(x => x.AgentExecutionId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<Notification>().HasOne<CandidateProfile>().WithMany().HasForeignKey(x => x.CandidateProfileId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<WorkflowDefinition>().HasOne<AgentProfile>().WithMany().HasForeignKey(x => x.ResumeAgentId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<WorkflowDefinition>().HasOne<CandidateProfile>().WithMany().HasForeignKey(x => x.CandidateProfileId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<WorkflowDefinition>().HasOne<JobPlatform>().WithMany().HasForeignKey(x => x.JobPlatformId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<WorkflowDefinition>().HasOne<ResumeVersion>().WithMany().HasForeignKey(x => x.ResumeVersionId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<WorkflowDefinition>().HasOne<AgentProfile>().WithMany().HasForeignKey(x => x.EvaluationAgentId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<WorkflowDefinition>().HasOne<AgentProfile>().WithMany().HasForeignKey(x => x.LetterAgentId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<WorkflowDefinition>().HasOne<AgentProfile>().WithMany().HasForeignKey(x => x.BrowserAgentId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<WorkflowDefinition>().HasOne<BrowserProfile>().WithMany().HasForeignKey(x => x.BrowserProfileId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<WorkflowDefinition>().Property(x => x.JobPlatformIds).HasDefaultValueSql("'[]'");
        b.Entity<WorkflowDefinition>().Property(x => x.MaxVacanciesPerPlatform).HasDefaultValue(50);
        b.Entity<WorkflowDefinition>().Property(x => x.MaxVacancyChecksPerRun).HasDefaultValue(10);
        b.Entity<WorkflowDefinition>().Property(x => x.VacancySource).HasDefaultValue("search");
        b.Entity<WorkflowDefinition>().Property(x => x.ApplicationStatusFilter).HasDefaultValue("pending");
        b.Entity<WorkflowDefinition>().Property(x => x.EvaluationResultFilter).HasDefaultValue("all");
        b.Entity<WorkflowDefinition>().Property(x => x.Revision).IsConcurrencyToken();
        b.Entity<WorkflowRun>().HasOne<WorkflowDefinition>().WithMany().HasForeignKey(x => x.WorkflowDefinitionId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<ScheduledTask>().HasOne<WorkflowDefinition>().WithMany().HasForeignKey(x => x.WorkflowDefinitionId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<ScheduledTask>().HasIndex(x => x.WorkflowDefinitionId).IsUnique().HasFilter("\"Kind\" = 'Workflow'");
        b.Entity<ScheduledTask>().HasIndex(x => new { x.CandidateProfileId, x.VacancyId }).IsUnique().HasFilter("\"Kind\" = 'PrepareApplication'");
        b.Entity<ScheduledTask>().HasOne<Vacancy>().WithMany().HasForeignKey(x => x.VacancyId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<JobApplication>().HasOne<WorkflowDefinition>().WithMany().HasForeignKey(x => x.WorkflowDefinitionId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<ScheduledTask>().HasOne<Integration>().WithMany().HasForeignKey(x=>x.IntegrationId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<Integration>().HasOne<JobPlatform>().WithMany().HasForeignKey(x=>x.JobPlatformId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<JournalImport>().HasOne<Integration>().WithMany().HasForeignKey(x=>x.IntegrationId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<HistoricalApplication>().HasOne<Integration>().WithMany().HasForeignKey(x=>x.IntegrationId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<HistoricalApplication>().HasOne<CandidateProfile>().WithMany().HasForeignKey(x=>x.CandidateProfileId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<HistoricalApplication>().HasOne<Vacancy>().WithMany().HasForeignKey(x=>x.VacancyId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<HistoricalApplication>().HasIndex(x=>new{x.CandidateProfileId,x.VacancyId}).IsUnique();
        b.Entity<FeedbackEvent>().HasOne<Integration>().WithMany().HasForeignKey(x=>x.IntegrationId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<FeedbackEvent>().HasOne<CandidateProfile>().WithMany().HasForeignKey(x=>x.CandidateProfileId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<FeedbackEvent>().HasIndex(x=>new{x.IntegrationId,x.SourceEventId}).IsUnique();
        b.Entity<CandidateProfile>().HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<Resume>().HasOne<CandidateProfile>().WithMany().HasForeignKey(x => x.CandidateProfileId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<ResumeVersion>().HasOne<Resume>().WithMany().HasForeignKey(x => x.ResumeId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<ResumeVersion>().HasIndex(x => new { x.ResumeId, x.Version }).IsUnique();
        b.Entity<Vacancy>().HasOne<JobPlatform>().WithMany().HasForeignKey(x => x.JobPlatformId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<Vacancy>().HasIndex(x => new { x.JobPlatformId, x.ExternalId }).IsUnique();
        b.Entity<JobApplication>().HasOne<CandidateProfile>().WithMany().HasForeignKey(x => x.CandidateProfileId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<JobApplication>().HasOne<Vacancy>().WithMany().HasForeignKey(x => x.VacancyId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<JobApplication>().HasOne<ResumeVersion>().WithMany().HasForeignKey(x => x.ResumeVersionId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<JobApplication>().HasIndex(x => new { x.CandidateProfileId, x.VacancyId }).IsUnique();
        b.Entity<JobApplication>().Property(x => x.Revision).IsConcurrencyToken();
        b.Entity<ApplicationAttempt>().HasOne<JobApplication>().WithMany().HasForeignKey(x => x.JobApplicationId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<ApplicationAttempt>().HasIndex(x => x.IdempotencyKey).IsUnique();
        b.Entity<EmployerInteraction>().HasOne<JobApplication>().WithMany().HasForeignKey(x => x.JobApplicationId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<EmployerInteraction>().HasIndex(x => new { x.JobApplicationId, x.SourceEventId }).IsUnique();
        b.Entity<AgentProfile>().Property(x => x.Version).IsConcurrencyToken();
        b.Entity<AgentProfile>().HasOne<CandidateProfile>().WithMany().HasForeignKey(x => x.CandidateProfileId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<AgentProfile>().HasOne<ModelProvider>().WithMany().HasForeignKey(x => x.ModelProviderId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<AgentExecution>().HasOne<AgentProfile>().WithMany().HasForeignKey(x => x.AgentProfileId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<ScheduledTask>().HasOne<CandidateProfile>().WithMany().HasForeignKey(x => x.CandidateProfileId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<TaskRun>().HasOne<ScheduledTask>().WithMany().HasForeignKey(x => x.ScheduledTaskId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<Integration>().HasOne<CandidateProfile>().WithMany().HasForeignKey(x => x.CandidateProfileId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<VacancyEvaluation>().HasOne<CandidateProfile>().WithMany().HasForeignKey(x => x.CandidateProfileId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<VacancyEvaluation>().HasOne<Vacancy>().WithMany().HasForeignKey(x => x.VacancyId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<PlatformAccount>().HasOne<CandidateProfile>().WithMany().HasForeignKey(x => x.CandidateProfileId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<PlatformAccount>().HasOne<JobPlatform>().WithMany().HasForeignKey(x => x.JobPlatformId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<PlatformAccount>().HasOne<BrowserProfile>().WithMany().HasForeignKey(x => x.BrowserProfileId).OnDelete(DeleteBehavior.Restrict);
    }
}
public static class Storage
{
    public static string Root => Path.GetFullPath(Environment.GetEnvironmentVariable("JOBFLOW_DATA_DIR") ??
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "JobFlow"));
    public static string ConnectionString
    {
        get { Directory.CreateDirectory(Root); return new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = Path.Combine(Root, "jobflow.db"), ForeignKeys = true }.ToString(); }
    }
}
public class DesignTimeFactory : IDesignTimeDbContextFactory<JobFlowDb>
{
    public JobFlowDb CreateDbContext(string[] args) => new(new DbContextOptionsBuilder<JobFlowDb>().UseSqlite(Storage.ConnectionString).Options);
}
