using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using JobFlow.Application;
using JobFlow.Domain;
using JobFlow.Infrastructure;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://127.0.0.1:5271");
builder.WebHost.ConfigureKestrel(x => x.Limits.MaxRequestBodySize = 12 * 1024 * 1024);
builder.Services.ConfigureHttpJsonOptions(x => x.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddDbContext<JobFlowDb>(x => x.UseSqlite(Storage.ConnectionString));
builder.Services.AddScoped<IModelProvider, CodexCliProvider>();
builder.Services.AddScoped<IAgentExecutor, AgentExecutor>();
builder.Services.AddSingleton<BrowserSessions>();
builder.Services.AddProblemDetails();
builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole();
var app = builder.Build();
using (var scope = app.Services.CreateScope())
    await scope.ServiceProvider.GetRequiredService<JobFlowDb>().Database.MigrateAsync();

app.Use(async (context, next) =>
{
    // A local socket alone does not prevent hostile websites from issuing requests.
    if (context.Request.Host.Host is not ("127.0.0.1" or "localhost" or "[::1]"))
    { context.Response.StatusCode = 403; return; }
    if (context.Request.Path.StartsWithSegments("/api") && context.Request.Method is not ("GET" or "HEAD") &&
        context.Request.Headers["X-JobFlow-Client"] != "local-ui")
    { context.Response.StatusCode = 403; return; }
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    try { await next(); }
    catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or DbUpdateException or JsonException or KeyNotFoundException)
    {
        if (context.Response.HasStarted) throw;
        context.Response.StatusCode = ex is DbUpdateException ? 409 : 400;
        await context.Response.WriteAsJsonAsync(new { error = ex is DbUpdateException ?
            "Conflict: duplicate record, stale revision, or invalid related record." : ex.Message });
    }
});
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/api/status", async (JobFlowDb db) => new {
    realSubmissionEnabled = false, profiles = await db.Profiles.CountAsync(), vacancies = await db.Vacancies.CountAsync(),
    applications = await db.Applications.CountAsync(), tasks = await db.Tasks.CountAsync(),
    capabilities = new[] { "Profiles", "PDF resumes", "Manual vacancy import", "Codex CLI", "Isolated browser dry-run", "Interval scheduler", "CSV export" },
    unavailable = new[] { "Real platform submission", "Google Sheets sync", "Gmail monitoring", "MCP execution", "BrowserAct" }
});
app.MapGet("/api/profiles", async (JobFlowDb db) => await db.Profiles.AsNoTracking().OrderBy(x => x.Name).ToListAsync());
app.MapPost("/api/profiles", async (CandidateProfile input, JobFlowDb db) =>
{
    Required(input.Name, "Profile name");
    var user = await db.Users.FirstOrDefaultAsync();
    if (user == null) { user = new User { Name = "Local owner" }; db.Add(user); }
    input.Id = Guid.NewGuid(); input.UserId = user.Id; input.CreatedAt = DateTime.UtcNow;
    db.Profiles.Add(input); Audit(db, input, "Created"); await db.SaveChangesAsync(); return Results.Ok(input);
});
app.MapPut("/api/profiles/{id:guid}", async (Guid id, CandidateProfile input, JobFlowDb db) =>
{
    Required(input.Name, "Profile name");
    var current = await db.Profiles.FindAsync(id) ?? throw new KeyNotFoundException("Profile not found.");
    input.Id = current.Id; input.UserId = current.UserId; input.CreatedAt = current.CreatedAt;
    db.Entry(current).CurrentValues.SetValues(input); Audit(db, current, "Updated"); await db.SaveChangesAsync(); return Results.Ok(current);
});
app.MapGet("/api/resumes", async (JobFlowDb db) => await (from r in db.Resumes join v in db.ResumeVersions on r.Id equals v.ResumeId
    select new { r.Id, r.CandidateProfileId, r.Name, r.IsPrimary, VersionId = v.Id, v.Version, v.OriginalName, v.CreatedAt }).ToListAsync());
app.MapPost("/api/resumes", async (HttpRequest request, JobFlowDb db) =>
{
    var form = await request.ReadFormAsync();
    if (!Guid.TryParse(form["profileId"], out var profileId) || !await db.Profiles.AnyAsync(x => x.Id == profileId))
        throw new ArgumentException("Choose a candidate profile.");
    var file = form.Files.GetFile("file") ?? throw new ArgumentException("Choose a PDF.");
    if (file.Length is < 5 or > 10485760) throw new ArgumentException("PDF size must be between 5 bytes and 10 MB.");
    await using var stream = file.OpenReadStream(); using var memory = new MemoryStream(); await stream.CopyToAsync(memory);
    var bytes = memory.ToArray();
    if (!bytes.AsSpan(0, 5).SequenceEqual("%PDF-"u8)) throw new ArgumentException("File does not have a PDF signature.");
    Resume resume;
    if (Guid.TryParse(form["resumeId"], out var resumeId))
        resume = await db.Resumes.SingleOrDefaultAsync(x => x.Id == resumeId && x.CandidateProfileId == profileId) ?? throw new ArgumentException("Resume does not belong to this profile.");
    else { resume = new Resume { CandidateProfileId = profileId, Name = Path.GetFileNameWithoutExtension(file.FileName) }; db.Add(resume); }
    var version = new ResumeVersion { ResumeId = resume.Id, Version = (await db.ResumeVersions.Where(x => x.ResumeId == resume.Id).MaxAsync(x => (int?)x.Version) ?? 0) + 1,
        OriginalName = Path.GetFileName(file.FileName), Sha256 = Convert.ToHexString(SHA256.HashData(bytes)), StorageKey = Guid.NewGuid().ToString("N") + ".pdf" };
    var directory = Path.Combine(Storage.Root, "resumes"); Directory.CreateDirectory(directory);
    var path = Path.Combine(directory, version.StorageKey);
    await File.WriteAllBytesAsync(path, bytes);
    try { db.Add(version); Audit(db, version, "Uploaded"); await db.SaveChangesAsync(); }
    catch { File.Delete(path); throw; }
    return Results.Ok(version);
});
app.MapGet("/api/resumes/versions/{id:guid}/download", async (Guid id, JobFlowDb db) =>
{
    var version = await db.ResumeVersions.FindAsync(id) ?? throw new KeyNotFoundException("Resume version not found.");
    return Results.File(Path.Combine(Storage.Root, "resumes", version.StorageKey), "application/pdf", version.OriginalName);
});
app.MapGet("/api/platforms", async (JobFlowDb db) => await db.Platforms.AsNoTracking().ToListAsync());
app.MapPost("/api/platforms", async (JobPlatform input, JobFlowDb db) =>
{
    Required(input.Name, "Platform name");
    if (!Uri.TryCreate(input.Url, UriKind.Absolute, out var url) || url.Scheme is not ("http" or "https")) throw new ArgumentException("Enter an HTTP(S) URL.");
    if (input.Adapter is not ("search-only" or "test-form")) throw new ArgumentException("Adapter not supported.");
    input.Id = Guid.NewGuid(); db.Add(input); Audit(db, input, "Created"); await db.SaveChangesAsync(); return Results.Ok(input);
});
app.MapPost("/api/platforms/{id:guid}/discover", async (Guid id, JobFlowDb db, CancellationToken ct) =>
{
    var platform = await db.Platforms.FindAsync(id) ?? throw new KeyNotFoundException("Platform not found.");
    var found = await new TestJobDiscoveryProvider().DiscoverAsync(platform, ct);
    var added = 0;
    foreach (var vacancy in found)
    {
        if (await db.Vacancies.AnyAsync(x => x.JobPlatformId == id && x.ExternalId == vacancy.ExternalId, ct)) continue;
        db.Add(vacancy); Audit(db, vacancy, "DiscoveredFromTestFixture"); added++;
    }
    await db.SaveChangesAsync(ct);
    return Results.Ok(new { discovered = found.Count, added, testOnly = true });
});
app.MapGet("/api/providers", async (JobFlowDb db) => await db.Providers.AsNoTracking().ToListAsync());
app.MapPost("/api/providers", async (ModelProvider input, JobFlowDb db) =>
{
    ValidateProvider(input); input.Id = Guid.NewGuid(); db.Add(input); Audit(db, input, "Created"); await db.SaveChangesAsync(); return Results.Ok(input);
});
app.MapPut("/api/providers/{id:guid}", async (Guid id, ModelProvider input, JobFlowDb db) =>
{
    ValidateProvider(input); var row = await db.Providers.FindAsync(id) ?? throw new KeyNotFoundException("Provider not found.");
    input.Id = id; input.CreatedAt = row.CreatedAt; db.Entry(row).CurrentValues.SetValues(input);
    Audit(db, row, "Updated"); await db.SaveChangesAsync(); return Results.Ok(row);
});
app.MapPost("/api/providers/{id:guid}/test", async (Guid id, JobFlowDb db, IModelProvider provider, CancellationToken ct) =>
{
    var p = await db.Providers.FindAsync(id) ?? throw new KeyNotFoundException("Provider not found.");
    if (!p.Enabled) throw new ArgumentException("Enable provider to authorize external model calls.");
    return Results.Ok(await provider.GenerateAsync(new("Return a JSON object with summary equal to connection-ok. Do not use tools.",
        """{"type":"object","properties":{"summary":{"type":"string"}},"required":["summary"],"additionalProperties":false}""", p.Model, "", p.TimeoutSeconds), ct));
});
app.MapGet("/api/agents", async (JobFlowDb db) => await db.Agents.AsNoTracking().ToListAsync());
app.MapPost("/api/agents", async (AgentProfile input, JobFlowDb db) =>
{
    ValidateAgent(input); input.Id = Guid.NewGuid(); input.Version = 1; db.Add(input); Audit(db, input, "Created"); await db.SaveChangesAsync(); return Results.Ok(input);
});
app.MapPut("/api/agents/{id:guid}", async (Guid id, AgentProfile input, JobFlowDb db) =>
{
    ValidateAgent(input);
    var row = await db.Agents.FindAsync(id) ?? throw new KeyNotFoundException("Agent not found.");
    if (input.Version != row.Version) return Results.Conflict(new { error = "Agent changed; refresh before editing." });
    Audit(db, row, "PreviousConfiguration", JsonSerializer.Serialize(row));
    input.Id = id; input.CreatedAt = row.CreatedAt; input.Version = row.Version + 1;
    db.Entry(row).CurrentValues.SetValues(input); await db.SaveChangesAsync(); return Results.Ok(row);
});
app.MapPost("/api/agents/{id:guid}/execute", async (Guid id, AgentInput input, IAgentExecutor executor, CancellationToken ct) =>
    Results.Ok(await executor.ExecuteAsync(new(id, input.Input), ct)));
app.MapGet("/api/vacancies", async (JobFlowDb db) => await db.Vacancies.AsNoTracking().OrderByDescending(x => x.CreatedAt).ToListAsync());
app.MapPost("/api/vacancies", async (Vacancy input, JobFlowDb db) =>
{
    Required(input.Title, "Title"); Required(input.ExternalId, "External ID");
    input.ExternalId = input.ExternalId.Trim(); input.Id = Guid.NewGuid(); db.Add(input); Audit(db, input, "Imported"); await db.SaveChangesAsync(); return Results.Ok(input);
});
app.MapPost("/api/vacancies/{id:guid}/evaluate", async (Guid id, ProfileSelection input, JobFlowDb db) =>
{
    var vacancy = await db.Vacancies.FindAsync(id) ?? throw new KeyNotFoundException("Vacancy not found.");
    var profile = await db.Profiles.FindAsync(input.ProfileId) ?? throw new KeyNotFoundException("Profile not found.");
    var result = ConservativeEvaluator.Evaluate(profile, vacancy);
    db.Add(new VacancyEvaluation { CandidateProfileId = profile.Id, VacancyId = id, ResultJson = JsonSerializer.Serialize(result) });
    await db.SaveChangesAsync(); return Results.Ok(result);
});
app.MapGet("/api/evaluations", async (JobFlowDb db) => await db.Set<VacancyEvaluation>().AsNoTracking().ToListAsync());
app.MapGet("/api/applications", async (JobFlowDb db) => await db.Applications.AsNoTracking().ToListAsync());
app.MapPost("/api/applications", async (JobApplication input, JobFlowDb db) =>
{
    if (!await (from v in db.ResumeVersions join r in db.Resumes on v.ResumeId equals r.Id
        where v.Id == input.ResumeVersionId && r.CandidateProfileId == input.CandidateProfileId select v).AnyAsync())
        throw new ArgumentException("Resume version must belong to the selected profile.");
    input.Id = Guid.NewGuid(); input.State = ApplicationState.Discovered; input.Revision = 0;
    db.Add(input); Audit(db, input, "Created"); await db.SaveChangesAsync(); return Results.Ok(input);
});
app.MapPost("/api/applications/{id:guid}/transition", async (Guid id, TransitionInput input, JobFlowDb db) =>
{
    var row = await db.Applications.FindAsync(id) ?? throw new KeyNotFoundException("Application not found.");
    if (row.Revision != input.Revision) return Results.Conflict(new { error = "Application changed; refresh first." });
    if (input.State is ApplicationState.Submitting or ApplicationState.Submitted or ApplicationState.SubmissionUnknown or ApplicationState.EmployerResponded)
        throw new ArgumentException("This transition requires verified adapter evidence. Real submission is disabled.");
    if (input.State == ApplicationState.ReadyToSubmit && string.IsNullOrWhiteSpace(row.CoverLetter)) throw new ArgumentException("Prepare a cover letter before review approval.");
    if (input.State == ApplicationState.ReadyToSubmit && await db.Set<Integration>().AnyAsync(x => x.CandidateProfileId == row.CandidateProfileId && !x.ImportConfirmed)) throw new ArgumentException("Confirm the existing journal import before approval.");
    var previous = row.State; Workflow.Transition(row, input.State);
    Audit(db, row, "StateChanged", $"{previous} -> {row.State}"); await db.SaveChangesAsync(); return Results.Ok(row);
});
app.MapGet("/api/tasks", async (JobFlowDb db) => await db.Tasks.AsNoTracking().ToListAsync());
app.MapPost("/api/tasks", async (ScheduledTask input, JobFlowDb db) =>
{
    Required(input.Name, "Task name");
    if (input.Kind is not ("HealthCheck" or "BrowserDryRun")) throw new ArgumentException("Only HealthCheck and BrowserDryRun are supported.");
    if (input.IntervalMinutes is < 1 or > 525600) throw new ArgumentException("Interval must be 1–525600 minutes.");
    try { TimeZoneInfo.FindSystemTimeZoneById(input.TimeZone); } catch { throw new ArgumentException("Unknown timezone."); }
    input.Id = Guid.NewGuid(); input.RunRequested = false; input.LeaseUntil = null; input.LeaseToken = null; input.NextRunAt = DateTime.UtcNow.AddMinutes(input.IntervalMinutes);
    db.Add(input); Audit(db, input, "Created"); await db.SaveChangesAsync(); return Results.Ok(input);
});
app.MapPost("/api/tasks/{id:guid}/{action}", async (Guid id, string action, JobFlowDb db) =>
{
    var row = await db.Tasks.FindAsync(id) ?? throw new KeyNotFoundException("Task not found.");
    switch (action)
    {
        case "run":
            if (row.LeaseUntil > DateTime.UtcNow) return Results.Conflict(new { error = "Task is running." });
            row.RunRequested = true; break;
        case "pause": row.Enabled = false; row.RunRequested = false; break;
        case "resume": row.Enabled = true; break;
        default: throw new ArgumentException("Unknown action.");
    }
    Audit(db, row, action); await db.SaveChangesAsync(); return Results.Ok(row);
});
app.MapGet("/api/runs", async (JobFlowDb db) => await db.Runs.AsNoTracking().OrderByDescending(x => x.CreatedAt).Take(200).ToListAsync());
app.MapGet("/api/logs", async (JobFlowDb db) => await db.Audit.AsNoTracking().OrderByDescending(x => x.CreatedAt).Take(200)
    .Select(x => new { x.Id, x.EntityType, x.EntityId, x.Action, x.CreatedAt }).ToListAsync());
app.MapGet("/api/executions", async (JobFlowDb db) => await db.Set<AgentExecution>().AsNoTracking().OrderByDescending(x => x.CreatedAt).Take(100)
    .Select(x => new { x.Id, x.AgentProfileId, x.ConfigurationVersion, x.Model, x.Status, x.Error, x.CreatedAt }).ToListAsync());
app.MapPost("/api/browser/dry-run", async (CancellationToken ct) => Results.Ok(await new PlaywrightBrowser().DryRunAsync(ct)));
app.MapGet("/api/browser-profiles", async (JobFlowDb db, BrowserSessions sessions) =>
    (await db.Set<BrowserProfile>().AsNoTracking().ToListAsync()).Select(x => new { x.Id, x.Name, busy = sessions.IsBusy(x.Id) }));
app.MapPost("/api/browser-profiles", async (BrowserProfile input, JobFlowDb db) =>
{
    Required(input.Name, "Name"); input.Id = Guid.NewGuid(); db.Add(input); Audit(db, input, "Created"); await db.SaveChangesAsync(); return Results.Ok(input);
});
app.MapPost("/api/browser-profiles/{id:guid}/open", async (Guid id, JobFlowDb db, BrowserSessions sessions) =>
{
    if (!await db.Set<BrowserProfile>().AnyAsync(x => x.Id == id)) return Results.NotFound();
    await sessions.OpenAsync(id); return Results.Ok(new { status = "Opened for manual authorization" });
});
app.MapPost("/api/browser-profiles/{id:guid}/close", async (Guid id, BrowserSessions sessions) =>
{ await sessions.CloseAsync(id); return Results.Ok(); });
app.MapDelete("/api/browser-profiles/{id:guid}", async (Guid id, bool confirm, JobFlowDb db, BrowserSessions sessions) =>
{
    if (!confirm) throw new ArgumentException("Explicit confirmation is required.");
    if (sessions.IsBusy(id)) throw new ArgumentException("Close the browser session first.");
    var row = await db.Set<BrowserProfile>().FindAsync(id) ?? throw new KeyNotFoundException("Browser profile not found.");
    if (await db.Set<PlatformAccount>().AnyAsync(x => x.BrowserProfileId == id)) throw new ArgumentException("Remove account bindings first.");
    db.Remove(row); Audit(db, row, "Deleted"); await db.SaveChangesAsync();
    var directory = Path.Combine(Storage.Root, "browser-profiles", id.ToString("N"));
    if (Directory.Exists(directory)) Directory.Delete(directory, true);
    return Results.NoContent();
});
app.MapGet("/api/export/vacancies.csv", async (JobFlowDb db, CancellationToken ct) =>
    Results.File(System.Text.Encoding.UTF8.GetBytes(await new CsvJournal().ExportAsync(await db.Vacancies.AsNoTracking().ToListAsync(ct), ct)), "text/csv", "vacancies.csv"));
app.MapGet("/api/integrations", () => new[] {
    new { name = "CSV", status = "Export available", detail = "Download vacancies from the local database." },
    new { name = "Google Sheets", status = "Not implemented", detail = "No account connected." },
    new { name = "Gmail", status = "Not implemented", detail = "No email access." },
    new { name = "MCP", status = "Not implemented", detail = "Client and tool permissions pending." }
});
app.MapFallbackToFile("index.html");
await app.RunAsync();
static void Required(string value, string field) { if (string.IsNullOrWhiteSpace(value) || value.Length > 500) throw new ArgumentException($"{field} is required (max 500 characters)."); }
static void ValidateProvider(ModelProvider input)
{
    Required(input.Name, "Provider name");
    if (input.Kind != "codex-cli") throw new ArgumentException("Only Codex CLI is implemented.");
    if (input.TimeoutSeconds is < 5 or > 900) throw new ArgumentException("Timeout must be 5–900 seconds.");
}
static void ValidateAgent(AgentProfile input)
{
    Required(input.Name, "Agent name"); using var schema = JsonDocument.Parse(input.OutputSchema);
    if (schema.RootElement.ValueKind != JsonValueKind.Object) throw new ArgumentException("Schema must be a JSON object.");
    if (input.UserPromptTemplate.Length > 50000 || input.SystemPrompt.Length > 50000) throw new ArgumentException("Prompt is too long.");
}
static void Audit(JobFlowDb db, Entity entity, string action, string details = "") =>
    db.Audit.Add(new AuditEvent { EntityId = entity.Id, EntityType = entity.GetType().Name, Action = action, Details = details });
record AgentInput(string Input);
record ProfileSelection(Guid ProfileId);
record TransitionInput(ApplicationState State, int Revision);
public partial class Program { }
