using System.Net.Http.Headers;
using System.Text.Json;
using JobFlow.Domain;
using Microsoft.EntityFrameworkCore;
namespace JobFlow.Infrastructure;
public sealed class RemoteIntegrationService(JobFlowDb db, SecretStore secrets, HttpClient http, JobFlow.Application.IAgentExecutor? agents = null)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public async Task<object> PullAsync(Guid id, CancellationToken ct)
    {
        var integration = await db.Set<Integration>().SingleAsync(x=>x.Id==id,ct);
        if(!integration.Enabled)throw new InvalidOperationException("Enable the integration before accessing its remote source.");
        var endpoint=new Uri(integration.Endpoint,UriKind.Absolute);
        ValidateEndpoint(integration.Kind,endpoint);
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(ct);deadline.CancelAfter(TimeSpan.FromSeconds(90));
        ct=deadline.Token;
        try
        {
            var headers=await new GoogleOAuth(secrets).HeadersAsync(integration,http,ct);
            if(integration.Kind=="google-sheets")
            {
                using var response=await GetAsync(endpoint,headers,ct);
                if(!response.RootElement.TryGetProperty("values",out var values))throw new InvalidOperationException("Sheet has no values.");
                var table=values.EnumerateArray().Select(x=>x.EnumerateArray().Select(y=>y.ToString()).ToArray()).ToArray();
                if(table.Length<2||table.Length>5001)throw new InvalidOperationException("Sheet requires a header and 1–5000 rows.");
                var width=table[0].Length;
                static string Cell(string s)=>"\"" + s.Replace("\"","\"\"") + "\"";
                var csv=string.Join("\r\n",table.Select(row=>string.Join(",",Enumerable.Range(0,width).Select(i=>Cell(i<row.Length?row[i]:"")))));
                var import=await new JournalService(db).PreviewAsync(id,csv,ct);
                integration.LastStatus="AwaitingConfirmation";integration.LastCheckedAt=DateTime.UtcNow;await db.SaveChangesAsync(ct);
                return new{import.Id,import.RowCount,import.Status};
            }
            if (integration.Kind is "hh-status" or "status-json")
            {
                PlatformStatusCheck.Observation[] observations;
                if (integration.Kind == "hh-status")
                    observations = await ReadHeadHunterAsync(endpoint, headers, ct);
                else
                {
                    using var response = await GetAsync(endpoint, headers, ct);
                    observations = PlatformStatusCheck.ReadFeed(response.RootElement);
                }
                var updated = await new PlatformStatusCheck(db).ApplyAsync(integration, observations, ct);
                integration.LastStatus = "Succeeded";
                integration.LastCheckedAt = DateTime.UtcNow;
                await db.SaveChangesAsync(ct);
                return new { updated, checkedAt = integration.LastCheckedAt };
            }
            var messages=new List<IncomingFeedback>();
            if(integration.Kind=="feedback-json")
            {
                using var response=await GetAsync(endpoint,headers,ct);
                messages=JsonSerializer.Deserialize<List<IncomingFeedback>>(response.RootElement,Json)??throw new InvalidOperationException("Feedback must be a JSON array.");
            }
            else if(integration.Kind=="gmail")
            {
                string? token=null;
                for(var page=0;page<10;page++)
                {
                    var listUri=token==null?endpoint:new Uri(endpoint+(string.IsNullOrEmpty(endpoint.Query)?"?":"&")+"pageToken="+Uri.EscapeDataString(token));
                    using var response=await GetAsync(listUri,headers,ct);
                    if(response.RootElement.TryGetProperty("messages",out var list))
                        foreach(var reference in list.EnumerateArray())
                        {
                            var messageId=reference.GetProperty("id").GetString()!;
                            if(await db.Set<FeedbackEvent>().AnyAsync(x=>x.IntegrationId==id&&x.SourceEventId==messageId,ct))continue;
                            using var message=await GetAsync(new Uri(endpoint.GetLeftPart(UriPartial.Path)+"/"+Uri.EscapeDataString(messageId)+"?format=full"),headers,ct);
                            var source=message.RootElement;
                            var subject=source.TryGetProperty("payload",out var payload)&&payload.TryGetProperty("headers",out var h)?
                                h.EnumerateArray().Where(x=>x.GetProperty("name").GetString()!.Equals("Subject",StringComparison.OrdinalIgnoreCase)).Select(x=>x.GetProperty("value").GetString()).FirstOrDefault()??"(no subject)":"(no subject)";
                            var body = GmailContent.Extract(payload, source.TryGetProperty("snippet", out var snippet) ? snippet.GetString() ?? "" : "");
                            var received=source.TryGetProperty("internalDate",out var date)&&long.TryParse(date.GetString(),out var epoch)?DateTimeOffset.FromUnixTimeMilliseconds(epoch).UtcDateTime:DateTime.UtcNow;
                            messages.Add(new(messageId,subject,body,received));
                        }
                    token=response.RootElement.TryGetProperty("nextPageToken",out var next)?next.GetString():null;
                    if(token==null)break;
                    if(page==9)throw new InvalidOperationException("Gmail query exceeds 10 pages; narrow the query. Source not fully checked.");
                }
            }
            else throw new ArgumentException("This integration cannot be polled remotely.");
            if(messages.Count>5000||messages.Any(x=>string.IsNullOrWhiteSpace(x.SourceEventId)||x.Subject.Length>2000||x.Body.Length>100000))throw new ArgumentException("Feedback data is invalid or too large.");
            var added=0;
            await using (var tx=await db.Database.BeginTransactionAsync(ct))
            {
                foreach (var message in messages.DistinctBy(x => x.SourceEventId))
                {
                    if (await db.Set<FeedbackEvent>().AnyAsync(x => x.IntegrationId == id && x.SourceEventId == message.SourceEventId, ct))
                        continue;
                    db.Add(new FeedbackEvent
                    {
                        IntegrationId = id, CandidateProfileId = integration.CandidateProfileId, SourceEventId = message.SourceEventId,
                        Subject = message.Subject, Body = message.Body, ReceivedAt = message.ReceivedAt
                    });
                    added++;
                }
                await db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
            }
            if (integration.Kind == "gmail")
                await new FeedbackProcessing(db, agents ?? new AgentExecutor(db, new CodexCliProvider())).ProcessAsync(integration, ct);
            integration.LastStatus = "Succeeded";
            integration.LastCheckedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            return new { added, checkedAt = integration.LastCheckedAt };
        }
        catch
        {
            foreach (var entry in db.ChangeTracker.Entries().Where(x => x.Entity is JobApplication or EmployerInteraction or AuditEvent or FeedbackEvent &&
                x.State is EntityState.Added or EntityState.Modified).ToList()) entry.State = EntityState.Detached;
            await db.Entry(integration).ReloadAsync(CancellationToken.None);
            await db.Set<Integration>().Where(x=>x.Id==id).ExecuteUpdateAsync(s=>s.SetProperty(x=>x.LastStatus,"Failed"),CancellationToken.None);
            ct.ThrowIfCancellationRequested();
            throw new InvalidOperationException("Source check failed. Check the endpoint, credentials and mail agent configuration.");
        }
    }
    public static void ValidateEndpoint(string kind,Uri endpoint)
    {
        if(endpoint.Scheme!="https"&&!((kind is "feedback-json" or "status-json")&&endpoint.Scheme=="http"&&endpoint.IsLoopback))throw new ArgumentException("Remote integration requires HTTPS.");
        if(kind=="google-sheets"&&(endpoint.Host!="sheets.googleapis.com"||!endpoint.AbsolutePath.StartsWith("/v4/spreadsheets/")||!endpoint.AbsolutePath.Contains("/values/")))throw new ArgumentException("Use a Google Sheets values.get endpoint.");
        if(kind=="gmail"&&(endpoint.Host!="gmail.googleapis.com"||!endpoint.AbsolutePath.StartsWith("/gmail/v1/users/")||!endpoint.AbsolutePath.EndsWith("/messages")))throw new ArgumentException("Use a Gmail messages.list endpoint, optionally with a job-related query.");
        if (kind == "hh-status" && (endpoint.Host != "api.hh.ru" || !endpoint.IsDefaultPort || endpoint.Query.Length != 0 ||
            endpoint.AbsolutePath != "/negotiations"))
            throw new ArgumentException("Use https://api.hh.ru/negotiations for HeadHunter applicant status.");
        if(!string.IsNullOrEmpty(endpoint.UserInfo)||!string.IsNullOrEmpty(endpoint.Fragment))throw new ArgumentException("Credentials and fragments do not belong in the URL.");
    }
    private async Task<PlatformStatusCheck.Observation[]> ReadHeadHunterAsync(Uri endpoint,
        Dictionary<string, string> headers, CancellationToken ct)
    {
        if (!headers.Keys.Any(x => x.Equals("Authorization", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("HeadHunter requires an applicant access token.");
        var observations = new List<PlatformStatusCheck.Observation>();
        for (var page = 0; page < 20; page++)
        {
            using var payload = await GetAsync(new Uri(endpoint + $"?per_page=100&page={page}"), headers, ct);
            var totalPages = payload.RootElement.GetProperty("pages").GetInt32();
            if (totalPages > 20) throw new InvalidOperationException("HeadHunter returned more than 20 pages.");
            observations.AddRange(PlatformStatusCheck.ReadHeadHunter(payload.RootElement));
            if (page + 1 >= totalPages) return observations.ToArray();
        }
        return observations.ToArray();
    }

    private async Task<JsonDocument> GetAsync(Uri uri,Dictionary<string,string> headers,CancellationToken ct)
    {
        using var request=new HttpRequestMessage(HttpMethod.Get,uri);
        if (uri.Host == "api.hh.ru") request.Headers.UserAgent.ParseAdd("JobFlow/1.0");
        foreach(var pair in headers)if(!request.Headers.TryAddWithoutValidation(pair.Key,pair.Value))throw new ArgumentException("Unsupported authentication header.");
        using var response=await http.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,ct);
        response.EnsureSuccessStatusCode();
        await response.Content.LoadIntoBufferAsync(5*1024*1024,ct);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
    }
    public record IncomingFeedback(string SourceEventId,string Subject,string Body,DateTime ReceivedAt);
}

