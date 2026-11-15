using System.Text.Json;
using JobFlow.Domain;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol.Client;
namespace JobFlow.Infrastructure;
public record ToolBinding(Guid ConnectionId, string ToolName, Dictionary<string, object?> Arguments);
public sealed class McpGateway(JobFlowDb db, SecretStore secrets)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public async Task<string> DiscoverAsync(Guid id, CancellationToken ct)
    {
        var connection = await db.Set<ToolConnection>().SingleAsync(x => x.Id == id, ct);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            await using var client = await ConnectAsync(connection, timeout.Token);
            var tools = await client.ListToolsAsync(cancellationToken: timeout.Token);
            connection.ToolsJson = JsonSerializer.Serialize(tools.Select(x => new { name = x.Name, description = x.Description, inputSchema = x.JsonSchema,
                readOnly = x.ProtocolTool.Annotations?.ReadOnlyHint == true }), Json);
            connection.Status = "Connected"; connection.LastCheckedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct); return connection.ToolsJson;
        }
        catch (Exception ex)
        {
            connection.Status = "Failed"; connection.LastCheckedAt = DateTime.UtcNow; await db.SaveChangesAsync(CancellationToken.None);
            throw new InvalidOperationException("MCP connection failed. Check transport, command, endpoint and credentials.", ex);
        }
    }
    public async Task<string> ReadForAgentAsync(AgentProfile agent, CancellationToken ct)
    {
        var bindings = JsonSerializer.Deserialize<ToolBinding[]>(agent.ToolBindingsJson, Json) ?? [];
        if (bindings.Length > 10) throw new ArgumentException("At most 10 MCP bindings per agent.");
        var results = new List<object>();
        foreach (var binding in bindings)
        {
            var connection = await db.Set<ToolConnection>().SingleAsync(x => x.Id == binding.ConnectionId, ct);
            var allowed = JsonSerializer.Deserialize<string[]>(connection.PermissionsJson) ?? [];
            if (!allowed.Contains(binding.ToolName, StringComparer.Ordinal)) throw new InvalidOperationException("MCP tool is not in the connection allowlist.");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(30));
            await using var client = await ConnectAsync(connection, timeout.Token);
            var tools = await client.ListToolsAsync(cancellationToken: timeout.Token);
            var tool = tools.SingleOrDefault(x => x.Name == binding.ToolName) ?? throw new InvalidOperationException("MCP tool no longer exists.");
            if (tool.ProtocolTool.Annotations?.ReadOnlyHint != true) throw new InvalidOperationException("Automatic MCP calls require an explicitly read-only tool.");
            var response = await client.CallToolAsync(binding.ToolName, binding.Arguments, cancellationToken: timeout.Token);
            if (response.IsError == true) throw new InvalidOperationException("MCP tool reported an error; source not successfully checked.");
            var content = JsonSerializer.Serialize(response, Json);
            if (content.Length > 100000) throw new InvalidOperationException("MCP result exceeds the context limit.");
            results.Add(new { connectionId = connection.Id, tool = binding.ToolName, untrustedToolData = content });
            db.Audit.Add(new AuditEvent { EntityType = nameof(ToolConnection), EntityId = connection.Id, Action = "ReadTool", Details = binding.ToolName });
            await db.SaveChangesAsync(ct);
        }
        return JsonSerializer.Serialize(results, Json);
    }
    public async Task<McpClient> ConnectAsync(ToolConnection connection, CancellationToken ct)
    {
        if (!connection.Enabled) throw new InvalidOperationException("Enable the MCP connection before use.");
        var credentials = await secrets.ReadAsync(connection.Id, ct);
        IClientTransport transport;
        if (connection.Transport == "stdio")
        {
            var env = StdioClientTransportOptions.GetDefaultEnvironmentVariables();
            foreach (var pair in credentials) env[pair.Key] = pair.Value;
            transport = new StdioClientTransport(new()
            {
                Name = connection.Name, Command = connection.EndpointOrCommand,
                Arguments = JsonSerializer.Deserialize<string[]>(connection.ArgumentsJson) ?? [],
                InheritEnvironmentVariables = false, EnvironmentVariables = env
            });
        }
        else if (connection.Transport == "http")
        {
            var uri = new Uri(connection.EndpointOrCommand, UriKind.Absolute);
            if (uri.Scheme != "https" && !(uri.Scheme == "http" && uri.IsLoopback)) throw new ArgumentException("Remote MCP requires HTTPS.");
            transport = new HttpClientTransport(new() { Endpoint = uri, TransportMode = HttpTransportMode.StreamableHttp,
                AdditionalHeaders = credentials, ConnectionTimeout = TimeSpan.FromSeconds(30) });
        }
        else throw new ArgumentException("MCP transport must be stdio or http.");
        return await McpClient.CreateAsync(transport, cancellationToken: ct);
    }
}
