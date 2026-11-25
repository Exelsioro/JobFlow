using JobFlow.Application;
using JobFlow.Domain;
using JobFlow.Infrastructure;
using JobFlow.Api;
using JobFlow.Worker;
using Microsoft.EntityFrameworkCore;
var builder = WebApplication.CreateBuilder(args);
var role = builder.Configuration["ServiceRole"] ?? "search";
if (role is not ("all" or "search" or "monitor")) throw new ArgumentException("ServiceRole must be search or monitor.");
var port = role == "search" ? 5272 : role == "monitor" ? 5273 : 0;
builder.WebHost.UseUrls("http://127.0.0.1:" + port);
builder.Logging.ClearProviders(); builder.Logging.AddJsonConsole();
builder.Services.AddWindowsService(o => o.ServiceName = role == "monitor" ? "JobFlow.Monitor" : "JobFlow.Search");
builder.Services.AddDbContext<JobFlowDb>(x => x.UseSqlite(Storage.ConnectionString));
builder.Services.AddScoped<IModelProvider, CodexCliProvider>();
builder.Services.AddSingleton<ICliModelSource, CodexAppServerModelSource>();
builder.Services.AddSingleton<ICliModelCatalog, CodexModelCatalog>();
builder.Services.AddScoped<IAgentExecutor, AgentExecutor>();
builder.Services.AddSingleton(_ => new BrowserSessions(builder.Configuration.GetValue("Browser:Headless", false)));
builder.Services.AddScoped<IBrowserWorkflowDriver, BrowserWorkflowDriver>();
var browserOptions = builder.Configuration.GetSection("Browser").Get<BrowserWorkflowOptions>() ?? new();
browserOptions.Validate();
builder.Services.AddSingleton(browserOptions);
builder.Services.AddScoped(sp => new TaskProcessor(sp.GetRequiredService<JobFlowDb>(), sp.GetRequiredService<IAgentExecutor>(), role, sp.GetRequiredService<IBrowserWorkflowDriver>()));
builder.Services.AddSingleton(new ServiceRuntime(role));
builder.Services.AddHostedService<Worker>();
if (role != "all") builder.Services.AddHostedService<ServiceHeartbeat>();
var app = builder.Build();
Directory.CreateDirectory(Storage.Root);
using var processLock = role == "all" ? null : new FileStream(Path.Combine(Storage.Root, "." + role + "-service-lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
using (var scope = app.Services.CreateScope()) await scope.ServiceProvider.GetRequiredService<JobFlowDb>().Database.MigrateAsync();
app.Use(async (context, next) =>
{
    if (context.Connection.RemoteIpAddress is not { } remote || !System.Net.IPAddress.IsLoopback(remote) || context.Request.Headers["X-JobFlow-Client"] != "local-ui")
    { context.Response.StatusCode = 403; return; }
    try { await next(); }
    catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or KeyNotFoundException)
    { context.Response.StatusCode = 400; await context.Response.WriteAsJsonAsync(new { error = ex.Message }); }
});
app.MapGet("/health", () => new { role, processId = Environment.ProcessId });
app.MapPost("/cli-check/{operation}", async (string operation, ICliModelCatalog catalog, IModelProvider provider, CancellationToken ct) =>
{
    if (operation is not ("BrowserAutomation" or "VacancyFreshness")) throw new ArgumentException("Unknown CLI check.");
    var selection = AgentTaskPolicy.Resolve(operation, await catalog.GetAsync(false, ct));
    const string schema = """{"type":"object","properties":{"ok":{"type":"boolean"}},"required":["ok"],"additionalProperties":false}""";
    var result = await provider.GenerateAsync(new("Проверка Unicode: русский, беларуская, polski — ąćęłńóśźż. Return only {\"ok\":true}. Do not use tools.", schema, selection.Model, selection.Reasoning, 120), ct);
    return Results.Ok(new { selection.Model, selection.Reasoning, result.Json });
});
app.MapPost("/shutdown", (IHostApplicationLifetime lifetime) => { lifetime.StopApplication(); return Results.Ok(); });
if (role == "search")
{
    app.MapRemoteBrowserEndpoints();
    app.MapBrowserDraftEndpoints();
    app.MapGet("/browser-status", async (JobFlowDb db, BrowserSessions sessions) =>
        (await db.Set<BrowserProfile>().AsNoTracking().ToListAsync()).Select(p => new { p.Id, p.Name, busy = sessions.IsBusy(p.Id), browserMode = sessions.GetMode(p.Id) }));
    app.MapPost("/api/browser-profiles/{id:guid}/open", async (Guid id, JobFlowDb db, BrowserSessions sessions) =>
    { if (!await db.Set<BrowserProfile>().AnyAsync(x => x.Id == id)) throw new ArgumentException("Browser profile not found."); await sessions.OpenAsync(id, "headed"); return Results.Ok(new { status = "Opened" }); });
    app.MapPost("/api/browser-profiles/{id:guid}/close", async (Guid id, BrowserSessions sessions) => { await sessions.CloseAsync(id); return Results.Ok(); });
}
await app.RunAsync();
