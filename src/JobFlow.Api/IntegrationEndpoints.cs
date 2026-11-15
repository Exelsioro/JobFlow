using System.Text.Json;
using JobFlow.Domain;
using JobFlow.Infrastructure;
using Microsoft.EntityFrameworkCore;
namespace JobFlow.Api;
public static class IntegrationEndpoints
{
    public static void MapIntegrationEndpoints(this WebApplication app)
    {
        app.MapGet("/api/integrations", async (JobFlowDb db, SecretStore secrets) =>
            (await db.Set<Integration>().AsNoTracking().ToListAsync()).Select(x => new { x.Id,x.Name,x.Kind,x.CandidateProfileId,x.JobPlatformId,
                x.MappingJson,x.ConflictPolicy,x.ImportConfirmed,x.LastStatus,x.LastCheckedAt,x.Endpoint,x.Enabled,hasSecrets=secrets.Exists(x.Id) }));
        app.MapPost("/api/integrations", async (Integration input, JobFlowDb db, CancellationToken ct) =>
        {
            await Validate(input,db,ct);input.Id=Guid.NewGuid();input.ImportConfirmed=false;input.LastStatus="NotChecked";input.LastCheckedAt=null;
            db.Add(input);await db.SaveChangesAsync(ct);return Results.Ok(input);
        });
        app.MapPut("/api/integrations/{id:guid}", async (Guid id, Integration input, JobFlowDb db,CancellationToken ct) =>
        {
            await Validate(input,db,ct);var row=await db.Set<Integration>().SingleAsync(x=>x.Id==id,ct);
            if(row.CandidateProfileId!=input.CandidateProfileId||row.JobPlatformId!=input.JobPlatformId||row.Kind!=input.Kind)throw new ArgumentException("Create a new integration to change its owner, platform or kind.");
            input.Id=id;input.CreatedAt=row.CreatedAt;input.ImportConfirmed=row.ImportConfirmed;input.LastStatus=row.LastStatus;input.LastCheckedAt=row.LastCheckedAt;
            db.Entry(row).CurrentValues.SetValues(input);await db.SaveChangesAsync(ct);return Results.Ok(row);
        });
        app.MapPost("/api/integrations/{id:guid}/secrets",async(Guid id,Dictionary<string,string> input,JobFlowDb db,SecretStore secrets,CancellationToken ct)=>{
            if(!await db.Set<Integration>().AnyAsync(x=>x.Id==id,ct))return Results.NotFound();
            await secrets.SaveAsync(id,input,ct);return Results.Ok(new{saved=true});
        });
        app.MapPost("/api/integrations/{id:guid}/preview",async(Guid id,CsvInput input,JournalService service,CancellationToken ct)=>Results.Ok(await service.PreviewAsync(id,input.Csv,ct)));
        app.MapPost("/api/integrations/{id:guid}/pull",async(Guid id,RemoteIntegrationService service,CancellationToken ct)=>Results.Ok(await service.PullAsync(id,ct)));
        app.MapGet("/api/imports",async(JobFlowDb db)=>await db.Set<JournalImport>().AsNoTracking().OrderByDescending(x=>x.CreatedAt).ToListAsync());
        app.MapPost("/api/imports/{id:guid}/confirm",async(Guid id,JournalService service,CancellationToken ct)=>Results.Ok(await service.ConfirmAsync(id,ct)));
        app.MapGet("/api/imported-applications",async(JobFlowDb db)=>await db.Set<HistoricalApplication>().AsNoTracking().ToListAsync());
        app.MapGet("/api/feedback",async(JobFlowDb db)=>await db.Set<FeedbackEvent>().AsNoTracking().OrderByDescending(x=>x.ReceivedAt).Take(500).ToListAsync());
        app.MapPost("/api/feedback/{id:guid}/acknowledge",async(Guid id,JobFlowDb db,CancellationToken ct)=>{
            var row=await db.Set<FeedbackEvent>().SingleAsync(x=>x.Id==id,ct);row.NeedsAction=false;await db.SaveChangesAsync(ct);return Results.Ok(row);
        });
    }
    private static async Task Validate(Integration input,JobFlowDb db,CancellationToken ct)
    {
        if(string.IsNullOrWhiteSpace(input.Name))throw new ArgumentException("Integration name is required.");
        if(input.Kind is not ("csv" or "google-sheets" or "gmail" or "feedback-json"))throw new ArgumentException("Unsupported integration kind.");
        if(input.ConflictPolicy is not ("keep-local" or "update-vacancies"))throw new ArgumentException("Unknown conflict policy.");
        if(!await db.Profiles.AnyAsync(x=>x.Id==input.CandidateProfileId,ct))throw new ArgumentException("Profile not found.");
        if(input.Kind is "csv" or "google-sheets")
        {
            if(!await db.Platforms.AnyAsync(x=>x.Id==input.JobPlatformId,ct))throw new ArgumentException("Choose a platform for the journal.");
            _=JsonSerializer.Deserialize<Dictionary<string,string>>(input.MappingJson)??throw new ArgumentException("Mapping must be an object.");
        }
        if(input.Kind!="csv"&&!Uri.TryCreate(input.Endpoint,UriKind.Absolute,out _))throw new ArgumentException("Integration endpoint is required.");
    }
    public record CsvInput(string Csv);
}

