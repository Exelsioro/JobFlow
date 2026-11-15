using System.Text.Json;
using JobFlow.Domain;
using JobFlow.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
namespace JobFlow.IntegrationTests;
public class McpTests
{
    [Fact] public async Task StdioDiscoveryPermissionsToolCallsAndEncryptedSecretsWork()
    {
        var root = Path.Combine(Path.GetTempPath(), "jobflow-mcp-" + Guid.NewGuid()); Directory.CreateDirectory(root);
        try
        {
            var script = Path.Combine(root, "server.cjs");
            await File.WriteAllTextAsync(script, """
                const rl=require('node:readline').createInterface({input:process.stdin});
                rl.on('line',line=>{
                  const r=JSON.parse(line);if(r.id===undefined)return;if(!['initialize','tools/list','tools/call','ping'].includes(r.method)){process.stdout.write(JSON.stringify({jsonrpc:'2.0',id:r.id,error:{code:-32601,message:'Method not found'}})+'\n');return;}let result={};
                  if(r.method==='initialize')result={protocolVersion:r.params.protocolVersion,capabilities:{tools:{}},serverInfo:{name:'fixture',version:'1'}};
                  if(r.method==='tools/list')result={tools:[
                    {name:'echo',description:'Read-only fixture',inputSchema:{type:'object',properties:{text:{type:'string'}}},annotations:{readOnlyHint:true}},
                    {name:'write',description:'Write fixture',inputSchema:{type:'object'},annotations:{readOnlyHint:false}}
                  ]};
                  if(r.method==='tools/call')result={content:[{type:'text',text:r.params.arguments.text||'ok'}],isError:false};
                  process.stdout.write(JSON.stringify({jsonrpc:'2.0',id:r.id,result})+'\n');
                });
                """);
            await using var db = new JobFlowDb(new DbContextOptionsBuilder<JobFlowDb>().UseSqlite($"Data Source={Path.Combine(root,"mcp.db")};Pooling=False").Options);
            await db.Database.MigrateAsync();
            var connection = new ToolConnection { Name = "Fixture", Enabled = true, EndpointOrCommand = "node", ArgumentsJson = JsonSerializer.Serialize(new[] { script }), PermissionsJson = """["echo"]""" };
            db.Add(connection); await db.SaveChangesAsync();
            var secrets = new SecretStore(root);
            await secrets.SaveAsync(connection.Id, new() { ["TEST_SECRET"] = "synthetic-secret" }, default);
            Assert.DoesNotContain("synthetic-secret", await File.ReadAllTextAsync(Path.Combine(root,"secrets",connection.Id.ToString("N")+".protected")));
            Assert.Equal("synthetic-secret", (await secrets.ReadAsync(connection.Id, default))["TEST_SECRET"]);
            var gateway = new McpGateway(db, secrets);
            Assert.Contains("echo", await gateway.DiscoverAsync(connection.Id, default));
            var agent = new AgentProfile { ToolBindingsJson = JsonSerializer.Serialize(new[] { new ToolBinding(connection.Id, "echo", new() { ["text"] = "fixture-result" }) }, new JsonSerializerOptions(JsonSerializerDefaults.Web)) };
            Assert.Contains("fixture-result", await gateway.ReadForAgentAsync(agent, default));
            connection.PermissionsJson = "[]"; await db.SaveChangesAsync();
            await Assert.ThrowsAsync<InvalidOperationException>(() => gateway.ReadForAgentAsync(agent, default));
            connection.Enabled = false; await db.SaveChangesAsync();
            await Assert.ThrowsAsync<InvalidOperationException>(() => gateway.DiscoverAsync(connection.Id, default));
            Assert.Equal("Failed", connection.Status);
        }
        finally { if(Directory.Exists(root)) Directory.Delete(root,true); }
    }
    [Fact] public async Task HttpTransportInitializesAndListsTools()
    {
        var builder = WebApplication.CreateBuilder(); builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var server = builder.Build();
        server.MapPost("/mcp", async (HttpContext context) =>
        {
            using var doc = await JsonDocument.ParseAsync(context.Request.Body);
            var request = doc.RootElement;
            if (!request.TryGetProperty("id",out var id)) { context.Response.StatusCode=202; return; }
            if(request.GetProperty("method").GetString() is not ("initialize" or "tools/list" or "ping")){await context.Response.WriteAsJsonAsync(new{jsonrpc="2.0",id=id.Clone(),error=new{code=-32601,message="Method not found"}});return;}
            object result = request.GetProperty("method").GetString() switch {
                "initialize" => new { protocolVersion=request.GetProperty("params").GetProperty("protocolVersion").GetString(),capabilities=new {tools=new{}},serverInfo=new{name="http-fixture",version="1"} } as object,
                "tools/list" => new {tools=Array.Empty<object>()},
                _ => new {}
            };
            await context.Response.WriteAsJsonAsync(new {jsonrpc="2.0",id=id.Clone(),result});
        });
        await server.StartAsync();
        var root=Path.Combine(Path.GetTempPath(),"jobflow-http-"+Guid.NewGuid());Directory.CreateDirectory(root);
        try
        {
            await using var db=new JobFlowDb(new DbContextOptionsBuilder<JobFlowDb>().UseSqlite($"Data Source={Path.Combine(root,"test.db")};Pooling=False").Options);
            await db.Database.MigrateAsync();
            var connection=new ToolConnection{Name="HTTP",Transport="http",EndpointOrCommand=server.Urls.Single()+"/mcp",Enabled=true};
            db.Add(connection);await db.SaveChangesAsync();
            Assert.Equal("[]",await new McpGateway(db,new SecretStore(root)).DiscoverAsync(connection.Id,default));
        }
        finally { await server.StopAsync();Directory.Delete(root,true); }
    }
}

