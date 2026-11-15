using JobFlow.Domain;
using JobFlow.Infrastructure;
using Microsoft.EntityFrameworkCore;
namespace JobFlow.Api;
public static class ResumeEndpoints
{
    public static void MapResumeEndpoints(this WebApplication app)
    {
        app.MapPost("/api/resumes/{id:guid}/primary",async(Guid id,JobFlowDb db,CancellationToken ct)=>{
            var resume=await db.Resumes.SingleAsync(x=>x.Id==id,ct);
            await using var tx=await db.Database.BeginTransactionAsync(ct);
            await db.Resumes.Where(x=>x.CandidateProfileId==resume.CandidateProfileId).ExecuteUpdateAsync(s=>s.SetProperty(x=>x.IsPrimary,x=>x.Id==id),ct);
            await tx.CommitAsync(ct);return Results.Ok(new{saved=true});
        });
        app.MapGet("/api/accounts",async(JobFlowDb db)=>await db.Set<PlatformAccount>().AsNoTracking().ToListAsync());
        app.MapPost("/api/accounts",async(PlatformAccount input,JobFlowDb db,CancellationToken ct)=>{
            await Validate(input,db,ct);input.Id=Guid.NewGuid();db.Add(input);await db.SaveChangesAsync(ct);return Results.Ok(input);
        });
        app.MapPut("/api/accounts/{id:guid}",async(Guid id,PlatformAccount input,JobFlowDb db,CancellationToken ct)=>{
            var row=await db.Set<PlatformAccount>().SingleAsync(x=>x.Id==id,ct);
            if(row.CandidateProfileId!=input.CandidateProfileId||row.JobPlatformId!=input.JobPlatformId)throw new ArgumentException("Account profile and platform cannot change.");
            await Validate(input,db,ct);row.BrowserProfileId=input.BrowserProfileId;row.ExternalResumeId=input.ExternalResumeId;
            await db.SaveChangesAsync(ct);return Results.Ok(row);
        });
    }
    private static async Task Validate(PlatformAccount row,JobFlowDb db,CancellationToken ct)
    {
        if(!await db.Profiles.AnyAsync(x=>x.Id==row.CandidateProfileId,ct)||!await db.Platforms.AnyAsync(x=>x.Id==row.JobPlatformId,ct))throw new ArgumentException("Choose an existing profile and platform.");
        if(row.BrowserProfileId!=null&&!await db.Set<BrowserProfile>().AnyAsync(x=>x.Id==row.BrowserProfileId,ct))throw new ArgumentException("Browser profile not found.");
        if(row.ExternalResumeId.Length>200)throw new ArgumentException("External resume ID is too long.");
    }
}
