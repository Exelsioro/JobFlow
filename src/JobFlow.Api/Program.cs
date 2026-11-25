using JobFlow.Api;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using JobFlow.Application;
using JobFlow.Domain;
using JobFlow.Infrastructure;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls(builder.Configuration["urls"] ?? "http://0.0.0.0:5271");
builder.WebHost.ConfigureKestrel(x => x.Limits.MaxRequestBodySize = 12 * 1024 * 1024);
builder.Services.ConfigureHttpJsonOptions(x => x.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddDbContext<JobFlowDb>(x => x.UseSqlite(Storage.ConnectionString));
builder.Services.AddScoped<IModelProvider, CodexCliProvider>();
builder.Services.AddSingleton<ICliModelSource, CodexAppServerModelSource>();
builder.Services.AddSingleton<ICliModelCatalog, CodexModelCatalog>();
builder.Services.AddScoped<IAgentExecutor, AgentExecutor>();
builder.Services.AddScoped(sp => new JobSearchWorkflow(sp.GetRequiredService<JobFlowDb>(), sp.GetRequiredService<IAgentExecutor>(), sp.GetRequiredService<IBrowserWorkflowDriver>(), monitorWithinSearch: false));
builder.Services.AddScoped<ResumeAgentService>();
builder.Services.AddScoped<IBrowserWorkflowDriver, BrowserWorkflowDriver>();
var browserOptions = builder.Configuration.GetSection("Browser").Get<BrowserWorkflowOptions>() ?? new();
browserOptions.Validate();
builder.Services.AddSingleton(browserOptions);
builder.Services.AddSingleton<SecretStore>();
builder.Services.AddSingleton<GoogleOAuth>();
builder.Services.AddScoped<McpGateway>();
builder.Services.AddScoped<JournalService>();
builder.Services.AddScoped<IBrowserAutomationProvider, PlaywrightBrowser>();
builder.Services.AddScoped<IJobApplicationProvider, TestApplicationProvider>();
builder.Services.AddScoped<TestSubmissionService>();
builder.Services.AddHttpClient<SheetsWriteBack>(client => client.Timeout = TimeSpan.FromSeconds(30))
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
builder.Services.AddHttpClient<RemoteIntegrationService>().ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
builder.Services.AddScoped<IJobSearchWorkflow>(sp => sp.GetRequiredService<JobSearchWorkflow>());
builder.Services.AddSingleton(_ => new BrowserSessions(builder.Configuration.GetValue<bool>("Browser:Headless")));
builder.Services.AddProblemDetails();
builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole();
builder.Logging.AddFilter("Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.Warning);
var app = builder.Build();
using (var scope = app.Services.CreateScope())
    await scope.ServiceProvider.GetRequiredService<JobFlowDb>().Database.MigrateAsync();

app.Use(async (context, next) =>
{
    // Allow LAN clients while rejecting public peers and hostile DNS-rebinding hosts.
    if (!LocalNetworkAccess.IsAllowedHost(context.Request.Host.Host) ||
        context.Connection.RemoteIpAddress is { } remote && !LocalNetworkAccess.IsLocalAddress(remote))
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
        var providerError = ModelProviderException.Find(ex);
        await context.Response.WriteAsJsonAsync(new { error = ex is DbUpdateException ?
            "Conflict: duplicate record, stale revision, or invalid related record." : ex.Message,
            providerCode = providerError?.Code, retryable = providerError?.Retryable, diagnosticId = providerError?.DiagnosticId });
    }
});
app.UseDefaultFiles();
app.UseWorkerBrowser();
app.UseStaticFiles();

app.MapGet("/api/status", async (JobFlowDb db) => new {
    realSubmissionEnabled = await db.Set<WorkflowDefinition>().AnyAsync(x => x.Enabled && x.ExecutionMode == "browser" && x.AutoSubmit), profiles = await db.Profiles.CountAsync(), vacancies = await db.Vacancies.CountAsync(),
    applications = await db.Applications.CountAsync(), tasks = await db.Tasks.CountAsync(),
    capabilities = new[] { "Profiles", "PDF resumes", "Manual vacancy import", "Codex CLI", "Isolated browser dry-run", "Interval scheduler", "CSV export", "Durable preparation workflow", "MCP stdio/HTTP read-only tools", "Confirmed CSV/Sheets import", "Gmail/JSON feedback", "Isolated idempotent test submission" },
    unavailable = new[] { "BrowserAct plugin" }, browserWorkflowAvailable = true
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
    select new { r.Id, r.CandidateProfileId, r.Name, r.IsPrimary, VersionId = v.Id, v.Version, v.OriginalName, v.CreatedAt, v.Origin, v.SourceResumeVersionId, v.VacancyId, v.AgentExecutionId }).ToListAsync());
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
    ValidatePlatform(input);
    input.Id = Guid.NewGuid(); db.Add(input); Audit(db, input, "Created"); await db.SaveChangesAsync(); return Results.Ok(input);
});
app.MapPut("/api/platforms/{id:guid}", async (Guid id, JobPlatform input, JobFlowDb db) =>
{
    ValidatePlatform(input);
    await using var transaction = await db.Database.BeginTransactionAsync();
    var row = await db.Platforms.FindAsync(id) ?? throw new KeyNotFoundException("Platform not found.");
    if (await db.Set<WorkflowDefinition>().AnyAsync(x => x.LeaseUntil > DateTime.UtcNow))
        return Results.Conflict(new { error = "Дождитесь завершения workflow перед изменением площадки." });
    input.Id = id; input.CreatedAt = row.CreatedAt;
    db.Entry(row).CurrentValues.SetValues(input);
    Audit(db, row, "Updated"); await db.SaveChangesAsync(); await transaction.CommitAsync(); return Results.Ok(row);
});
app.MapPost("/api/platforms/{id:guid}/discover", async (Guid id, JobFlowDb db, CancellationToken ct) =>
{
    var platform = await db.Platforms.FindAsync(id) ?? throw new KeyNotFoundException("Platform not found.");
    using var http = PlatformDiscovery.CreateClient();
    var (discovered, added) = await new PlatformDiscovery(db, http).ImportAsync(platform, ct);
    return Results.Ok(new { discovered, added, testOnly = platform.Adapter == "test-form" });
});
app.MapGet("/api/providers", async (JobFlowDb db) => await db.Providers.AsNoTracking().ToListAsync());
app.MapGet("/api/provider-models", async (Guid? providerId, string? kind, bool? refresh, JobFlowDb db, ICliModelCatalog catalog, CancellationToken ct) =>
{
    if (providerId.HasValue)
        kind = (await db.Providers.AsNoTracking().SingleOrDefaultAsync(x => x.Id == providerId.Value, ct)
            ?? throw new KeyNotFoundException("Provider not found.")).Kind;
    if (kind != "codex-cli") throw new ArgumentException("Выберите провайдера Codex CLI для загрузки моделей.");
    return Results.Ok(await catalog.GetAsync(refresh == true, ct));
});
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
app.MapGet("/api/vacancies", async (bool? includeArchived, JobFlowDb db) => await db.Vacancies.AsNoTracking()
    .Where(x => includeArchived == true || !x.IsArchived).OrderByDescending(x => x.CreatedAt).ToListAsync());
app.MapPost("/api/vacancies", async (Vacancy input, JobFlowDb db) =>
{
    Required(input.Title, "Title"); Required(input.ExternalId, "External ID");
    input.ExternalId = input.ExternalId.Trim(); input.IsArchived = false; input.Id = Guid.NewGuid(); db.Add(input); Audit(db, input, "Imported"); await db.SaveChangesAsync(); return Results.Ok(input);
});
app.MapDelete("/api/vacancies/{id:guid}", async (Guid id, JobFlowDb db) =>
{
    await using var transaction = await db.Database.BeginTransactionAsync();
    var row = await db.Vacancies.FindAsync(id) ?? throw new KeyNotFoundException("Vacancy not found.");
    if (await db.Set<WorkflowDefinition>().AnyAsync(x => x.LeaseUntil > DateTime.UtcNow))
        return Results.Conflict(new { error = "Дождитесь завершения workflow перед удалением вакансии." });
    if (!row.IsArchived) { row.IsArchived = true; Audit(db, row, "Archived"); await db.SaveChangesAsync(); }
    await transaction.CommitAsync(); return Results.Ok(row);
});
app.MapPost("/api/vacancies/{id:guid}/restore", async (Guid id, JobFlowDb db) =>
{
    var row = await db.Vacancies.FindAsync(id) ?? throw new KeyNotFoundException("Vacancy not found.");
    if (row.IsArchived) { row.IsArchived = false; Audit(db, row, "Restored"); await db.SaveChangesAsync(); }
    return Results.Ok(row);
});
app.MapPost("/api/vacancies/{id:guid}/evaluate", async (Guid id, ProfileSelection input, JobFlowDb db) =>
{
    await using var transaction = await db.Database.BeginTransactionAsync();
    var vacancy = await db.Vacancies.FindAsync(id) ?? throw new KeyNotFoundException("Vacancy not found.");
    if (vacancy.IsArchived) return Results.Conflict(new { error = "Вакансия удалена из поиска. Сначала восстановите её." });
    var profile = await db.Profiles.FindAsync(input.ProfileId) ?? throw new KeyNotFoundException("Profile not found.");
    var previous = await db.Set<VacancyEvaluation>().Where(x => x.CandidateProfileId == profile.Id && x.VacancyId == id)
        .OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.Id).FirstOrDefaultAsync();
    if (previous != null && !input.Reevaluate) return Results.Ok(JsonSerializer.Deserialize<JsonElement>(previous.ResultJson));
    var result = ConservativeEvaluator.Evaluate(profile, vacancy);
    var evaluation = new VacancyEvaluation { CandidateProfileId = profile.Id, VacancyId = id, ResultJson = JsonSerializer.Serialize(result) };
    db.Add(evaluation); Audit(db, evaluation, previous == null ? "Evaluated" : "Reevaluated");
    await db.SaveChangesAsync(); await transaction.CommitAsync(); return Results.Ok(result);
});
app.MapGet("/api/evaluations", async (JobFlowDb db) => await db.Set<VacancyEvaluation>().AsNoTracking().ToListAsync());
app.MapApplicationDetailsEndpoints();
app.MapGet("/api/applications", async (JobFlowDb db) =>
{
    var externalIds = (await db.Audit.AsNoTracking().Where(x => x.Action == ExternalApplicationDraft.AuditAction || x.Action == ExternalApplicationDraft.InvalidatedAction)
        .Select(x => new { x.EntityId, x.Action, x.CreatedAt, x.Id }).ToListAsync())
        .GroupBy(x => x.EntityId).Where(g => g.OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.Id).First().Action == ExternalApplicationDraft.AuditAction)
        .Select(g => g.Key).ToHashSet();
    return (await db.Applications.AsNoTracking().ToListAsync()).Select(x => new { x.Id, x.CandidateProfileId, x.VacancyId,
        x.ResumeVersionId, x.WorkflowDefinitionId, x.State, x.CoverLetter, x.Revision, x.IsTest, x.ResponseStatus,
        x.ResponseStatusAt, x.CreatedAt, externalDraft = externalIds.Contains(x.Id) });
});
app.MapPost("/api/applications", async (JobApplication input, JobFlowDb db) =>
{
    if (await db.Vacancies.AnyAsync(x => x.Id == input.VacancyId && x.IsArchived))
        return Results.Conflict(new { error = "Нельзя создать отклик на удалённую вакансию. Сначала восстановите её." });
    if (!await (from v in db.ResumeVersions join r in db.Resumes on v.ResumeId equals r.Id
        where v.Id == input.ResumeVersionId && r.CandidateProfileId == input.CandidateProfileId select v).AnyAsync())
        throw new ArgumentException("Resume version must belong to the selected profile.");
    if(await db.Set<HistoricalApplication>().AnyAsync(x=>x.CandidateProfileId==input.CandidateProfileId&&x.VacancyId==input.VacancyId))throw new ArgumentException("Imported application history exists; do not apply again.");
    input.IsTest=await (from v in db.Vacancies join p in db.Platforms on v.JobPlatformId equals p.Id where v.Id==input.VacancyId && p.Adapter=="test-form" select v).AnyAsync();
    input.Id = Guid.NewGuid(); input.State = ApplicationState.Discovered; input.Revision = 0;
    db.Add(input); Audit(db, input, "Created"); await db.SaveChangesAsync(); return Results.Ok(input);
});
app.MapPost("/api/applications/{id:guid}/transition", async (Guid id, TransitionInput input, JobFlowDb db) =>
{
    var row = await db.Applications.FindAsync(id) ?? throw new KeyNotFoundException("Application not found.");
    if (row.Revision != input.Revision) return Results.Conflict(new { error = "Application changed; refresh first." });
    if (await db.Tasks.AnyAsync(x => x.Kind == "PrepareApplication" && x.VacancyId == row.VacancyId && x.CandidateProfileId == row.CandidateProfileId &&
        (x.RunRequested || x.LeaseUntil > DateTime.UtcNow))) return Results.Conflict(new { error = "Отклик находится на доработке. Дождитесь завершения или остановите задание." });
    if (input.State is ApplicationState.Submitting or ApplicationState.Submitted or ApplicationState.SubmissionUnknown or ApplicationState.EmployerResponded)
        throw new ArgumentException("This transition requires submission evidence. Run the browser workflow or record a manually sent application.");
    if (input.State == ApplicationState.ReadyToSubmit && string.IsNullOrWhiteSpace(row.CoverLetter)) throw new ArgumentException("Prepare a cover letter before review approval.");
    if (input.State == ApplicationState.ReadyToSubmit && await JournalApproval.Pending(db).AnyAsync(x => x.CandidateProfileId == row.CandidateProfileId))
        throw new ArgumentException("Подтвердите импорт истории или отметьте, что старого журнала нет.");
    if(input.State==ApplicationState.ReadyToSubmit && await db.Set<HistoricalApplication>().AnyAsync(x=>x.CandidateProfileId==row.CandidateProfileId&&x.VacancyId==row.VacancyId))throw new ArgumentException("Imported application history blocks resubmission.");
    var previous = row.State; Workflow.Transition(row, input.State);
    Audit(db, row, "StateChanged", $"{previous} -> {row.State}"); await db.SaveChangesAsync(); return Results.Ok(row);
});
app.MapGet("/api/tasks", async (JobFlowDb db) => await db.Tasks.AsNoTracking().ToListAsync());
app.MapPost("/api/tasks", async (ScheduledTask input, JobFlowDb db) =>
{
    Required(input.Name, "Task name");
    if (input.Kind is not ("HealthCheck" or "BrowserDryRun" or "Integration")) throw new ArgumentException("Unsupported task kind.");
    if(input.TimeoutSeconds < 5 || input.TimeoutSeconds > TaskExecutionLimits.MaximumTimeoutSeconds(input.Kind) || input.MaxRetries is < 0 or > 5)throw new ArgumentException("Timeout: 5–900 seconds; retries: 0–5.");
    if(input.Kind=="Integration"&&!await db.Set<Integration>().AnyAsync(x=>x.Id==input.IntegrationId&&x.CandidateProfileId==input.CandidateProfileId))throw new ArgumentException("Integration must belong to the selected profile.");
    input.FailureCount=0;input.CancelRequested=false;
    if (input.IntervalMinutes is < 1 or > 525600) throw new ArgumentException("Interval must be 1–525600 minutes.");
    try { TimeZoneInfo.FindSystemTimeZoneById(input.TimeZone); } catch { throw new ArgumentException("Unknown timezone."); }
    TaskSchedule.Validate(input);
    input.Id = Guid.NewGuid(); input.RunRequested = false; input.LeaseUntil = null; input.LeaseToken = null; input.NextRunAt = TaskSchedule.Next(input, DateTime.UtcNow);
    db.Add(input); Audit(db, input, "Created"); await db.SaveChangesAsync(); return Results.Ok(input);
});
app.MapPut("/api/tasks/{id:guid}", async (Guid id, ScheduledTask input, JobFlowDb db) =>
{
    var row=await db.Tasks.FindAsync(id)??throw new KeyNotFoundException("Task not found.");
    Required(input.Name,"Task name");
    if(input.IntervalMinutes is < 1 or > 525600 || input.TimeoutSeconds < 5 || input.TimeoutSeconds > TaskExecutionLimits.MaximumTimeoutSeconds(row.Kind) || input.MaxRetries is < 0 or > 5)throw new ArgumentException($"Interval: 1–525600 minutes; timeout: 5–{TaskExecutionLimits.MaximumTimeoutSeconds(row.Kind)} seconds; retries: 0–5.");
    try { TimeZoneInfo.FindSystemTimeZoneById(input.TimeZone); } catch { throw new ArgumentException("Unknown timezone."); }
    if(row.Kind!=input.Kind || row.CandidateProfileId!=input.CandidateProfileId || row.IntegrationId!=input.IntegrationId)throw new ArgumentException("Task source cannot change; create a new task.");
    row.Name=input.Name;row.IntervalMinutes=input.IntervalMinutes;row.TimeoutSeconds=input.TimeoutSeconds;row.MaxRetries=input.MaxRetries;row.TimeZone=input.TimeZone;row.Enabled=input.Enabled;
    row.ScheduleKind=input.ScheduleKind;row.LocalTime=input.LocalTime;row.DaysOfWeek=input.DaysOfWeek;
    TaskSchedule.Validate(row); row.NextRunAt=TaskSchedule.Next(row,DateTime.UtcNow);
    Audit(db,row,"Updated");await db.SaveChangesAsync();return Results.Ok(row);
});
app.MapPost("/api/tasks/{id:guid}/{action}", async (Guid id, string action, JobFlowDb db) =>
{
    var row = await db.Tasks.FindAsync(id) ?? throw new KeyNotFoundException("Task not found.");
    switch (action)
    {
        case "run":
            if (row.LeaseUntil > DateTime.UtcNow) return Results.Conflict(new { error = "Task is running." });
            row.RunRequested = true; row.NextRunAt=DateTime.UtcNow; row.FailureCount=0; break;
        case "stop": row.Enabled=false;row.RunRequested=false;row.CancelRequested=true;row.PrepareOutsideAgeLimit=false;break;
        case "pause": row.Enabled = false; row.RunRequested = false; row.PrepareOutsideAgeLimit = false; break;
        case "resume": row.Enabled = true; break;
        default: throw new ArgumentException("Unknown action.");
    }
    Audit(db, row, action); await db.SaveChangesAsync(); return Results.Ok(row);
});
app.MapGet("/api/runs", async (JobFlowDb db) => await db.Runs.AsNoTracking().OrderByDescending(x => x.CreatedAt).Take(200).ToListAsync());
app.MapGet("/api/logs", async (JobFlowDb db) => await db.Audit.AsNoTracking().OrderByDescending(x => x.CreatedAt).Take(200)
    .Select(x => new { x.Id, x.EntityType, x.EntityId, x.Action, x.Details, x.CreatedAt }).ToListAsync());
app.MapGet("/api/executions", async (JobFlowDb db) => await db.Set<AgentExecution>().AsNoTracking().OrderByDescending(x => x.CreatedAt).Take(100)
    .Select(x => new { x.Id, x.AgentProfileId, x.ConfigurationVersion, x.Model, x.Status, x.Error, x.CreatedAt }).ToListAsync());
app.MapGet("/api/cli-diagnostics/{id:guid}", (Guid id) =>
{
    var path = Path.Combine(Storage.Root, "cli-diagnostics", id + ".json");
    return File.Exists(path) ? Results.File(path, "application/json") : Results.NotFound();
});
app.MapGet("/api/browser-diagnostics/{id:guid}", (Guid id) =>
{
    var path = Path.Combine(Storage.Root, "browser-diagnostics", id.ToString("D"), "failure.json");
    return File.Exists(path) ? Results.File(path, "application/json") : Results.NotFound();
});
app.MapGet("/api/browser-diagnostics/{id:guid}/screen", (Guid id) =>
{
    var path = Path.Combine(Storage.Root, "browser-diagnostics", id.ToString("D"), "screen.jpg");
    return File.Exists(path) ? Results.File(path, "image/jpeg") : Results.NotFound();
});
app.MapPost("/api/browser/dry-run", async (CancellationToken ct) => Results.Ok(await new PlaywrightBrowser().DryRunAsync(ct)));
app.MapGet("/api/browser-profiles", async (JobFlowDb db, BrowserSessions sessions) =>
    (await db.Set<BrowserProfile>().AsNoTracking().ToListAsync()).Select(x => new { x.Id, x.Name, busy = sessions.IsBusy(x.Id), browserMode = sessions.GetMode(x.Id) }));
app.MapPost("/api/browser-profiles", async (BrowserProfile input, JobFlowDb db) =>
{
    Required(input.Name, "Name"); input.Id = Guid.NewGuid(); db.Add(input); Audit(db, input, "Created"); await db.SaveChangesAsync(); return Results.Ok(input);
});
app.MapPost("/api/browser-profiles/{id:guid}/open", async (Guid id, JobFlowDb db, BrowserSessions sessions) =>
{
    if (!await db.Set<BrowserProfile>().AnyAsync(x => x.Id == id)) return Results.NotFound();
    await sessions.OpenAsync(id, "headed"); return Results.Ok(new { status = "Opened for manual authorization" });
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
    Results.File(System.Text.Encoding.UTF8.GetBytes(await new CsvJournal().ExportAsync(await db.Vacancies.AsNoTracking().Where(x => !x.IsArchived).ToListAsync(ct), ct)), "text/csv", "vacancies.csv"));
app.MapIntegrationEndpoints();
app.MapWorkflowEndpoints();
app.MapBrowserDraftEndpoints();
app.MapRemoteBrowserEndpoints();
app.MapResumeEndpoints();
app.MapServiceEndpoints();
app.MapMcpEndpoints();
app.MapFallbackToFile("index.html");
await app.RunAsync();
static void Required(string value, string field) { if (string.IsNullOrWhiteSpace(value) || value.Length > 500) throw new ArgumentException($"{field} is required (max 500 characters)."); }
static void ValidatePlatform(JobPlatform input)
{
    Required(input.Name, "Название площадки");
    if (!BrowserSessions.SupportsMode(input.BrowserMode)) throw new ArgumentException("Выберите поддерживаемый режим браузера.");
    if (!Uri.TryCreate(input.Url, UriKind.Absolute, out var url) || url.Scheme is not ("http" or "https") || url.UserInfo.Length > 0)
        throw new ArgumentException("Укажите HTTP(S)-адрес площадки без логина и пароля в ссылке.");
    if (input.Adapter is not ("search-only" or "test-form" or "headhunter" or "greenhouse" or "browser"))
        throw new ArgumentException("Выберите поддерживаемый способ работы с площадкой.");
    input.Name = input.Name.Trim(); input.Url = input.Url.Trim();
}
static void ValidateProvider(ModelProvider input)
{
    Required(input.Name, "Provider name");
    if (input.Kind != "codex-cli") throw new ArgumentException("Only Codex CLI is implemented.");
    if (input.TimeoutSeconds is < 5 or > 900) throw new ArgumentException("Timeout must be 5–900 seconds.");
}
static void ValidateAgent(AgentProfile input)
{
    if (!AgentTaskPolicy.Languages.ContainsKey(input.ResponseLanguage)) throw new ArgumentException("Выберите язык объяснений агента.");
    if (input.UseTaskModelPresets && !AgentTaskPolicy.Recommendations.Any(x => x.TaskType == input.TaskType)) throw new ArgumentException("Для этого этапа выберите модель вручную.");
    Required(input.Name, "Agent name"); using var schema = JsonDocument.Parse(input.OutputSchema);
    if (!string.IsNullOrWhiteSpace(input.Reasoning) && !CodexCliProvider.SupportsReasoning(input.Reasoning)) throw new ArgumentException("Unsupported reasoning effort.");
    if (schema.RootElement.ValueKind != JsonValueKind.Object) throw new ArgumentException("Schema must be a JSON object.");
    if (input.UserPromptTemplate.Length > 50000 || input.SystemPrompt.Length > 50000) throw new ArgumentException("Prompt is too long.");
}
static void Audit(JobFlowDb db, Entity entity, string action, string details = "") =>
    db.Audit.Add(new AuditEvent { EntityId = entity.Id, EntityType = entity.GetType().Name, Action = action, Details = details });
record AgentInput(string Input);
record ProfileSelection(Guid ProfileId, bool Reevaluate = false);
record TransitionInput(ApplicationState State, int Revision);
public partial class Program { }
