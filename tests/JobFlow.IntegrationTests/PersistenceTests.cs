using JobFlow.Domain;
using JobFlow.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
namespace JobFlow.IntegrationTests;
public class PersistenceTests
{
    [Fact]
    public async Task BrowserMigrationKeepsExistingWorkflowsInPreparationMode()
    {
        var file = Path.Combine(Path.GetTempPath(), "jobflow-upgrade-" + Guid.NewGuid() + ".db");
        await using var db = new JobFlowDb(new DbContextOptionsBuilder<JobFlowDb>().UseSqlite($"Data Source={file};Pooling=False").Options);
        try
        {
            var migrator = db.GetService<IMigrator>();
            await migrator.MigrateAsync("20261115184804_StatusAndMailMonitoring");
            var user = new User(); var candidate = new CandidateProfile { UserId = user.Id };
            var resume = new Resume { CandidateProfileId = candidate.Id }; var version = new ResumeVersion { ResumeId = resume.Id, Version = 1 };
            var platform = new JobPlatform();
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO Users (Id,CreatedAt,Name) VALUES ({user.Id},{user.CreatedAt},{user.Name})");
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO Profiles (Id,CreatedAt,UserId,Name,Email,Experience,Skills,Education,Languages,PreferredRoles,Geography,Salary,Relocation,ExcludedEmployers,Rules) VALUES ({candidate.Id},{candidate.CreatedAt},{user.Id},{candidate.Name},{candidate.Email},{candidate.Experience},{candidate.Skills},{candidate.Education},{candidate.Languages},{candidate.PreferredRoles},{candidate.Geography},{candidate.Salary},{candidate.Relocation},{candidate.ExcludedEmployers},{candidate.Rules})");
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO Resumes (Id,CreatedAt,CandidateProfileId,Name,IsPrimary) VALUES ({resume.Id},{resume.CreatedAt},{candidate.Id},{resume.Name},0)");
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO ResumeVersions (Id,CreatedAt,ResumeId,Version,StorageKey,Sha256,OriginalName) VALUES ({version.Id},{version.CreatedAt},{resume.Id},1,{version.StorageKey},{version.Sha256},{version.OriginalName})");
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO Platforms (Id,CreatedAt,Name,Url,Adapter,Geography,SearchFilters) VALUES ({platform.Id},{platform.CreatedAt},{platform.Name},{platform.Url},{platform.Adapter},{platform.Geography},{platform.SearchFilters})");
            var id = Guid.NewGuid(); var now = DateTime.UtcNow; var rules = "{}"; var name = "Existing workflow";
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO WorkflowDefinition (Id,CreatedAt,Name,CandidateProfileId,JobPlatformId,ResumeVersionId,RulesJson,Enabled,Revision) VALUES ({id},{now},{name},{candidate.Id},{platform.Id},{version.Id},{rules},1,1)");
            await migrator.MigrateAsync();
            var definition = await db.Set<WorkflowDefinition>().SingleAsync();
            Assert.Equal("prepare", definition.ExecutionMode);
            Assert.False(definition.AutoSubmit);
            Assert.Equal(10, definition.MaxApplicationsPerRun);
            Assert.Equal(40, definition.MaxBrowserSteps);
            Assert.Null(definition.BrowserProfileId);
        }
        finally { await db.DisposeAsync(); File.Delete(file); }
    }
    [Fact] public async Task DatabaseRejectsDuplicateApplicationsAndPersistsAcrossRestart()
    {
        var file = Path.Combine(Path.GetTempPath(), $"jobflow-{Guid.NewGuid()}.db");
        var options = new DbContextOptionsBuilder<JobFlowDb>().UseSqlite($"Data Source={file};Pooling=False;Foreign Keys=True").Options;
        try
        {
            await using (var db = new JobFlowDb(options))
            {
                await db.Database.MigrateAsync();
                var user = new User(); var profile = new CandidateProfile { UserId = user.Id };
                var resume = new Resume { CandidateProfileId = profile.Id }; var version = new ResumeVersion { ResumeId = resume.Id, Version = 1 };
                var platform = new JobPlatform(); var vacancy = new Vacancy { JobPlatformId = platform.Id, ExternalId = "test" };
                db.AddRange(user, profile, resume, version, platform, vacancy);
                db.Add(new JobApplication { CandidateProfileId = profile.Id, VacancyId = vacancy.Id, ResumeVersionId = version.Id });
                await db.SaveChangesAsync();
                db.Add(new JobApplication { CandidateProfileId = profile.Id, VacancyId = vacancy.Id, ResumeVersionId = version.Id });
                await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
            }
            await using var restarted = new JobFlowDb(options);
            Assert.Equal(1, await restarted.Applications.CountAsync());
        }
        finally { File.Delete(file); }
    }
}
