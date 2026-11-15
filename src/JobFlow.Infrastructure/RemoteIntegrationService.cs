using System.Net.Http.Headers;
using System.Text.Json;
using JobFlow.Domain;
using Microsoft.EntityFrameworkCore;
namespace JobFlow.Infrastructure;
public sealed class RemoteIntegrationService(JobFlowDb db, SecretStore secrets, HttpClient http)
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
            var headers=await secrets.ReadAsync(id,ct);
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
                            using var message=await GetAsync(new Uri(endpoint.GetLeftPart(UriPartial.Path)+"/"+Uri.EscapeDataString(messageId)+"?format=metadata&metadataHeaders=Subject"),headers,ct);
                            var source=message.RootElement;
                            var subject=source.TryGetProperty("payload",out var payload)&&payload.TryGetProperty("headers",out var h)?
                                h.EnumerateArray().Where(x=>x.GetProperty("name").GetString()!.Equals("Subject",StringComparison.OrdinalIgnoreCase)).Select(x=>x.GetProperty("value").GetString()).FirstOrDefault()??"(no subject)":"(no subject)";
                            var body=source.TryGetProperty("snippet",out var snippet)?snippet.GetString()??"":"";
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
            await using var tx=await db.Database.BeginTransactionAsync(ct);
            foreach(var message in messages.DistinctBy(x=>x.SourceEventId))
            {
                if(await db.Set<FeedbackEvent>().AnyAsync(x=>x.IntegrationId==id&&x.SourceEventId==message.SourceEventId,ct))continue;
                db.Add(new FeedbackEvent{IntegrationId=id,CandidateProfileId=integration.CandidateProfileId,SourceEventId=message.SourceEventId,Subject=message.Subject,Body=message.Body,ReceivedAt=message.ReceivedAt});added++;
            }
            integration.LastStatus="Succeeded";integration.LastCheckedAt=DateTime.UtcNow;
            await db.SaveChangesAsync(ct);await tx.CommitAsync(ct);return new{added,checkedAt=integration.LastCheckedAt};
        }
        catch
        {
            foreach(var entry in db.ChangeTracker.Entries<FeedbackEvent>().Where(x=>x.State==EntityState.Added).ToList())entry.State=EntityState.Detached;
            await db.Entry(integration).ReloadAsync(CancellationToken.None);
            await db.Set<Integration>().Where(x=>x.Id==id).ExecuteUpdateAsync(s=>s.SetProperty(x=>x.LastStatus,"Failed"),CancellationToken.None);
            throw new InvalidOperationException("Source check failed. Verify endpoint, token and permissions. No successful check was recorded.");
        }
    }
    public static void ValidateEndpoint(string kind,Uri endpoint)
    {
        if(endpoint.Scheme!="https"&&!(kind=="feedback-json"&&endpoint.Scheme=="http"&&endpoint.IsLoopback))throw new ArgumentException("Remote integration requires HTTPS.");
        if(kind=="google-sheets"&&(endpoint.Host!="sheets.googleapis.com"||!endpoint.AbsolutePath.StartsWith("/v4/spreadsheets/")||!endpoint.AbsolutePath.Contains("/values/")))throw new ArgumentException("Use a Google Sheets values.get endpoint.");
        if(kind=="gmail"&&(endpoint.Host!="gmail.googleapis.com"||!endpoint.AbsolutePath.StartsWith("/gmail/v1/users/")||!endpoint.AbsolutePath.EndsWith("/messages")))throw new ArgumentException("Use a Gmail messages.list endpoint, optionally with a job-related query.");
        if(!string.IsNullOrEmpty(endpoint.UserInfo)||!string.IsNullOrEmpty(endpoint.Fragment))throw new ArgumentException("Credentials and fragments do not belong in the URL.");
    }
    private async Task<JsonDocument> GetAsync(Uri uri,Dictionary<string,string> headers,CancellationToken ct)
    {
        using var request=new HttpRequestMessage(HttpMethod.Get,uri);
        foreach(var pair in headers)if(!request.Headers.TryAddWithoutValidation(pair.Key,pair.Value))throw new ArgumentException("Unsupported authentication header.");
        using var response=await http.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,ct);
        response.EnsureSuccessStatusCode();
        await response.Content.LoadIntoBufferAsync(5*1024*1024,ct);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
    }
    public record IncomingFeedback(string SourceEventId,string Subject,string Body,DateTime ReceivedAt);
}

