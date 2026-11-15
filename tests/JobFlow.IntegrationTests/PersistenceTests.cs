using JobFlow.Domain;
using JobFlow.Infrastructure;
using Microsoft.EntityFrameworkCore;
namespace JobFlow.IntegrationTests;
public class PersistenceTests
{
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
