using System.Net;
using System.Net.Http.Json;
using JobFlow.Api;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;

namespace JobFlow.IntegrationTests;

public class LocalNetworkTests
{
    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("::1", true)]
    [InlineData("192.168.100.20", true)]
    [InlineData("10.0.0.2", true)]
    [InlineData("172.16.0.2", true)]
    [InlineData("172.31.255.254", true)]
    [InlineData("169.254.1.2", true)]
    [InlineData("::ffff:192.168.100.20", true)]
    [InlineData("fd00::2", true)]
    [InlineData("fe80::2", true)]
    [InlineData("172.15.0.2", false)]
    [InlineData("172.32.0.2", false)]
    [InlineData("203.0.113.2", false)]
    [InlineData("::ffff:203.0.113.2", false)]
    [InlineData("2001:db8::2", false)]
    [InlineData("0.0.0.0", false)]
    public void OnlyLocalPeerAddressesAreAllowed(string address, bool expected) =>
        Assert.Equal(expected, LocalNetworkAccess.IsLocalAddress(IPAddress.Parse(address)));

    [Fact]
    public async Task LanClientsCanReadAndWriteButPublicPeersAndForeignHostsAreRejected()
    {
        var directory = Path.Combine(Path.GetTempPath(), "jobflow-lan-" + Guid.NewGuid().ToString("N"));
        var previous = Environment.GetEnvironmentVariable("JOBFLOW_DATA_DIR");
        Environment.SetEnvironmentVariable("JOBFLOW_DATA_DIR", directory);
        try
        {
            await using var factory = new WebApplicationFactory<Program>();
            using var client = factory.CreateClient(new() { BaseAddress = new("http://192.168.100.16:5271") });
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/status")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/profiles", new { name = "Missing client header" })).StatusCode);
            client.DefaultRequestHeaders.Add("X-JobFlow-Client", "local-ui");
            Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/profiles", new { name = "LAN candidate" })).StatusCode);
            Assert.Contains("LAN candidate", await client.GetStringAsync("/api/profiles"));

            foreach (var (peer, host, expected) in new[]
            {
                ("192.168.100.20", "192.168.100.16:5271", 200),
                ("::ffff:192.168.100.20", "192.168.100.16:5271", 200),
                ("203.0.113.20", "192.168.100.16:5271", 403),
                ("192.168.100.20", "untrusted.example:5271", 403),
                ("192.168.100.20", "203.0.113.16:5271", 403)
            })
            {
                var response = await factory.Server.SendAsync(context =>
                {
                    context.Connection.RemoteIpAddress = IPAddress.Parse(peer);
                    context.Request.Method = "GET";
                    context.Request.Path = "/api/status";
                    context.Request.Host = new HostString(host);
                });
                Assert.Equal(expected, response.Response.StatusCode);
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("JOBFLOW_DATA_DIR", previous);
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }
}
