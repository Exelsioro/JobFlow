using System.Diagnostics;
using System.Net;
using System.ServiceProcess;
using JobFlow.Domain;
using JobFlow.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace JobFlow.Api;
public static class ServiceEndpoints
{
    public static void MapServiceEndpoints(this WebApplication app)
    {
        app.MapGet("/api/services", async (JobFlowDb db) =>
        {
            var states = await db.Set<ServiceState>().AsNoTracking().ToListAsync();
            return new[] { "search", "monitor" }.Select(role =>
            {
                var state = states.SingleOrDefault(s => s.Role == role);
                return new { id = role, role, status = state?.HeartbeatAt > DateTime.UtcNow.AddSeconds(-20) ? state.Status : "Offline",
                    enabled = state?.Enabled ?? true, state?.HeartbeatAt, state?.ProcessId };
            });
        });
        app.MapPost("/api/services/{role}/{action}", async (string role, string action, JobFlowDb db, CancellationToken ct) =>
        {
            if (role is not ("search" or "monitor") || action is not ("start" or "pause" or "stop")) throw new ArgumentException("Unknown service action.");
            var state = await db.Set<ServiceState>().SingleOrDefaultAsync(s => s.Role == role, ct);
            if (state == null) { state = new() { Role = role }; db.Add(state); }
            state.Enabled = action == "start";
            if (action == "stop")
            {
                await db.Tasks.Where(t => role == "monitor" ? t.Kind == "Integration" : t.Kind != "Integration")
                    .Where(t => t.LeaseUntil > DateTime.UtcNow).ExecuteUpdateAsync(s => s.SetProperty(t => t.CancelRequested, true), ct);
            }
            await db.SaveChangesAsync(ct);
            if (action == "start" && (state.Status is "Stopped" or "Offline" || state.HeartbeatAt == null || state.HeartbeatAt < DateTime.UtcNow.AddSeconds(-20)))
            {
                if (!app.Configuration.GetValue("Services:UseWindowsServices", true) || !ControlWindowsService(role, true))
                {
                    var workerPath = app.Configuration["Services:WorkerPath"] ?? Path.GetFullPath(Path.Combine(app.Environment.ContentRootPath, "..", "JobFlow.Worker", "bin", "Debug", "net10.0", "JobFlow.Worker.dll"));
                    if (!File.Exists(workerPath)) throw new InvalidOperationException("Worker executable not found. Configure Services:WorkerPath or install the Windows services.");
                    var start = new ProcessStartInfo(workerPath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? "dotnet" : workerPath)
                        { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(workerPath)! };
                    if (workerPath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) start.ArgumentList.Add(workerPath);
                    start.ArgumentList.Add("--ServiceRole"); start.ArgumentList.Add(role);
                    start.Environment["JOBFLOW_DATA_DIR"] = Storage.Root;
                    using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start service process.");
                    state.ProcessId = process.Id; state.Status = "Starting"; await db.SaveChangesAsync(ct);
                }
            }
            if (action == "stop")
            {
                if (!app.Configuration.GetValue("Services:UseWindowsServices", true) || !ControlWindowsService(role, false))
                {
                    using var http = HostClient(role);
                    try { using var response = await http.PostAsync("/shutdown", null, ct); response.EnsureSuccessStatusCode(); }
                    catch (HttpRequestException) when (state.HeartbeatAt == null || state.HeartbeatAt < DateTime.UtcNow.AddSeconds(-20)) { }
                }
            }
            return Results.Ok(new { action, role });
        });
        app.MapGet("/api/notifications", async (JobFlowDb db, CancellationToken ct) => await NotificationQueries.ReadAsync(db, ct));
        app.MapGet("/api/generated-resumes", async (JobFlowDb db) => await (from v in db.ResumeVersions join r in db.Resumes on v.ResumeId equals r.Id
            where v.Origin == "AgentGenerated" orderby v.CreatedAt descending
            select new { v.Id, versionId = v.Id, r.CandidateProfileId, v.OriginalName, v.SourceResumeVersionId, v.VacancyId, v.AgentExecutionId, v.CreatedAt, v.AdaptationJson }).ToListAsync());
        app.MapPost("/api/notifications/{id:guid}/read", async (Guid id, JobFlowDb db, CancellationToken ct) =>
        { await db.Set<Notification>().Where(n => n.Id == id).ExecuteUpdateAsync(s => s.SetProperty(n => n.ReadAt, DateTime.UtcNow), ct); return Results.Ok(); });
        app.MapGet("/api/dossier-proposals", async (JobFlowDb db) => await db.Set<DossierProposal>().AsNoTracking().OrderByDescending(n => n.CreatedAt).Take(100).ToListAsync());
        app.MapPost("/api/resumes/versions/{id:guid}/dossier", async (Guid id, DossierInput input, ResumeAgentService resumes, CancellationToken ct) =>
            Results.Ok(await resumes.ProposeAsync(input.CandidateProfileId, id, input.AgentId, ct)));
        app.MapPost("/api/dossier-proposals/{id:guid}/confirm", async (Guid id, ResumeAgentService resumes, CancellationToken ct) =>
        { await resumes.ConfirmAsync(id, ct); return Results.Ok(); });
    }
    private static bool ControlWindowsService(string role, bool start)
    {
        if (!OperatingSystem.IsWindows()) return false;
        var name = role == "search" ? "JobFlow.Search" : "JobFlow.Monitor";
        ServiceController? found = null;
        foreach (var candidate in ServiceController.GetServices()) { if (candidate.ServiceName == name) found = candidate; else candidate.Dispose(); }
        using var service = found;
        if (service == null) return false;
        try
        {
            if (start && service.Status == ServiceControllerStatus.Stopped) service.Start();
            if (!start && service.Status is not (ServiceControllerStatus.Stopped or ServiceControllerStatus.StopPending)) service.Stop();
            return true;
        }
        catch (InvalidOperationException) { throw new InvalidOperationException("Windows denied service control. Grant this API account permission to manage the JobFlow services."); }
    }
    public static HttpClient HostClient(string role) => CreateClient(new Uri("http://127.0.0.1:" + (role == "search" ? 5272 : 5273)));
    private static HttpClient CreateClient(Uri uri)
    {
        var http = new HttpClient(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false }) { BaseAddress = uri, Timeout = TimeSpan.FromSeconds(40) };
        http.DefaultRequestHeaders.Add("X-JobFlow-Client", "local-ui"); return http;
    }
    public static void UseWorkerBrowser(this WebApplication app)
    {
        if (!app.Configuration.GetValue<bool>("Services:UseWorkerBrowser")) return;
        app.Use(async (context, next) =>
        {
            var path = context.Request.Path.Value ?? "";
            var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var controls = parts.Length == 4 && parts[0] == "api" && parts[1] == "browser-profiles" && Guid.TryParse(parts[2], out _) &&
                new[] { "open", "close", "control", "tabs", "screen" }.Contains(parts[3]);
            var list = path == "/api/browser-profiles" && context.Request.Method == "GET";
            if (parts.Length == 3 && parts[0] == "api" && parts[1] == "browser-profiles" && context.Request.Method == "DELETE")
            {
                using var statusClient = HostClient("search");
                try
                {
                    var profiles = await statusClient.GetFromJsonAsync<System.Text.Json.JsonElement[]>("/browser-status", context.RequestAborted);
                    if (profiles?.Any(p => p.GetProperty("id").GetString() == parts[2] && p.GetProperty("busy").GetBoolean()) == true)
                    { context.Response.StatusCode = 409; await context.Response.WriteAsJsonAsync(new { error = "Close this browser profile before deleting it." }); return; }
                }
                catch (HttpRequestException) { }
            }
            var draft = parts.Length == 5 && parts[0] == "api" && parts[1] == "applications" && Guid.TryParse(parts[2], out _) && parts[3] == "browser-draft";
            if (!controls && !list && !draft) { await next(); return; }
            using var http = HostClient("search");
            // A saved visible Chromium profile can take over 40 s to start on low-memory PCs.
            if (controls && parts[3] == "open") http.Timeout = TimeSpan.FromSeconds(120);
            try
            {
                using var request = new HttpRequestMessage(new HttpMethod(context.Request.Method), list ? "/browser-status" : path + context.Request.QueryString);
                if (context.Request.ContentLength > 0)
                { request.Content = new StreamContent(context.Request.Body); request.Content.Headers.ContentType = new("application/json"); }
                using var response = await http.SendAsync(request, context.RequestAborted);
                context.Response.StatusCode = (int)response.StatusCode;
                context.Response.ContentType = response.Content.Headers.ContentType?.ToString();
                context.Response.Headers.CacheControl = "no-store";
                await response.Content.CopyToAsync(context.Response.Body, context.RequestAborted);
            }
            catch (OperationCanceledException) when (!context.RequestAborted.IsCancellationRequested)
            {
                if (context.Response.HasStarted) throw;
                context.Response.StatusCode = 504;
                await context.Response.WriteAsJsonAsync(new { error = "Служба поиска не успела выполнить действие с браузером. Он мог уже открыться; проверьте профиль и повторите действие." });
            }
            catch (HttpRequestException)
            {
                if (list) { await next(); return; }
                context.Response.StatusCode = 503; await context.Response.WriteAsJsonAsync(new { error = "Служба поиска недоступна. Запустите её в разделе «Службы»." });
            }
        });
    }
    public sealed record DossierInput(Guid CandidateProfileId, Guid AgentId);
}
