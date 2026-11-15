using System.Text.Json;
using JobFlow.Domain;
using JobFlow.Infrastructure;
using Microsoft.EntityFrameworkCore;
namespace JobFlow.Api;
public static class McpEndpoints
{
    public static void MapMcpEndpoints(this WebApplication app)
    {
        app.MapGet("/api/mcp", async (JobFlowDb db, SecretStore secrets) =>
            (await db.Set<ToolConnection>().AsNoTracking().ToListAsync()).Select(x => new { x.Id, x.Name, x.Transport, x.EndpointOrCommand,
                x.ArgumentsJson, x.PermissionsJson, x.Enabled, x.ToolsJson, x.Status, x.LastCheckedAt, hasSecrets = secrets.Exists(x.Id) }));
        app.MapPost("/api/mcp", async (ToolConnection input, JobFlowDb db) =>
        {
            Validate(input); input.Id = Guid.NewGuid(); input.ToolsJson = "[]"; input.Status = "NotChecked"; input.LastCheckedAt = null;
            db.Add(input); await db.SaveChangesAsync(); return Results.Ok(input);
        });
        app.MapPut("/api/mcp/{id:guid}", async (Guid id, ToolConnection input, JobFlowDb db) =>
        {
            Validate(input); var row = await db.Set<ToolConnection>().SingleAsync(x => x.Id == id);
            input.Id = id; input.CreatedAt = row.CreatedAt; input.ToolsJson = "[]"; input.Status = "NotChecked"; input.LastCheckedAt = null;
            db.Entry(row).CurrentValues.SetValues(input); await db.SaveChangesAsync(); return Results.Ok(row);
        });
        app.MapPost("/api/mcp/{id:guid}/secrets", async (Guid id, Dictionary<string,string> input, JobFlowDb db, SecretStore secrets, CancellationToken ct) =>
        {
            if (!await db.Set<ToolConnection>().AnyAsync(x => x.Id == id, ct)) return Results.NotFound();
            if (input.Count > 50 || input.Any(x => x.Key.Length > 200 || x.Value.Length > 20000)) throw new ArgumentException("Secret configuration is too large.");
            await secrets.SaveAsync(id, input, ct); return Results.Ok(new { saved = true });
        });
        app.MapPost("/api/mcp/{id:guid}/discover", async (Guid id, McpGateway gateway, CancellationToken ct) =>
            Results.Content(await gateway.DiscoverAsync(id, ct), "application/json"));
    }
    private static void Validate(ToolConnection input)
    {
        if (string.IsNullOrWhiteSpace(input.Name) || string.IsNullOrWhiteSpace(input.EndpointOrCommand)) throw new ArgumentException("Name and command/endpoint are required.");
        if (input.Transport is not ("stdio" or "http")) throw new ArgumentException("Unsupported MCP transport.");
        _ = JsonSerializer.Deserialize<string[]>(input.ArgumentsJson) ?? throw new ArgumentException("Arguments must be an array.");
        _ = JsonSerializer.Deserialize<string[]>(input.PermissionsJson) ?? throw new ArgumentException("Allowlist must be an array.");
        if (input.Transport == "http" && (!Uri.TryCreate(input.EndpointOrCommand, UriKind.Absolute, out var uri) ||
            (uri.Scheme != "https" && !(uri.Scheme == "http" && uri.IsLoopback)))) throw new ArgumentException("Remote MCP requires HTTPS.");
    }
}
