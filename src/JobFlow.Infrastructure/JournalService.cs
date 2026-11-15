using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using JobFlow.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.VisualBasic.FileIO;
namespace JobFlow.Infrastructure;
public record JournalRow(string ExternalId, string Title, string Employer, string Url, string Description, string ApplicationState);
public sealed class JournalService(JobFlowDb db)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public async Task<JournalImport> PreviewAsync(Guid integrationId, string csv, CancellationToken ct)
    {
        var integration = await db.Set<Integration>().SingleAsync(x => x.Id == integrationId, ct);
        if (integration.Kind is not ("csv" or "google-sheets")) throw new ArgumentException("This endpoint imports CSV only.");
        if (Encoding.UTF8.GetByteCount(csv) > 5 * 1024 * 1024) throw new ArgumentException("CSV limit is 5 MB.");
        var mapping = JsonSerializer.Deserialize<Dictionary<string,string>>(integration.MappingJson) ?? throw new ArgumentException("Mapping must be an object.");
        using var parser = new TextFieldParser(new StringReader(csv)) { TextFieldType = FieldType.Delimited, HasFieldsEnclosedInQuotes = true };
        parser.SetDelimiters(",");
        var headers = parser.ReadFields() ?? throw new ArgumentException("CSV header missing.");
        if (headers.Distinct(StringComparer.OrdinalIgnoreCase).Count() != headers.Length) throw new ArgumentException("CSV has duplicate column names.");
        foreach (var key in new[] { "externalId", "title" })
            if (!mapping.TryGetValue(key, out var column) || !headers.Contains(column, StringComparer.OrdinalIgnoreCase)) throw new ArgumentException("Map externalId and title to existing CSV columns.");
        var rows = new List<JournalRow>();
        try
        {
            while (!parser.EndOfData)
            {
                ct.ThrowIfCancellationRequested();
                var fields = parser.ReadFields()!;
                if (fields.Length != headers.Length) throw new ArgumentException("CSV row has the wrong number of columns.");
                string Get(string key)
                {
                    if (!mapping.TryGetValue(key, out var column)) return "";
                    var index = Array.FindIndex(headers, x => string.Equals(x,column,StringComparison.OrdinalIgnoreCase));
                    return index < 0 ? "" : fields[index].Trim();
                }
                var row = new JournalRow(Get("externalId"), Get("title"), Get("employer"), Get("url"), Get("description"), Get("applicationState"));
                if (string.IsNullOrWhiteSpace(row.ExternalId) || string.IsNullOrWhiteSpace(row.Title)) throw new ArgumentException("Every row needs externalId and title.");
                if (row.ExternalId.Length > 500 || row.Title.Length > 500) throw new ArgumentException("ID/title exceeds 500 characters.");
                rows.Add(row); if (rows.Count > 5000) throw new ArgumentException("Maximum 5000 CSV rows.");
            }
        }
        catch (MalformedLineException) { throw new ArgumentException("Malformed CSV quoting."); }
        if (rows.Count == 0) throw new ArgumentException("CSV contains no data rows.");
        var hash=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(csv)));
        var existing=await db.Set<JournalImport>().AsNoTracking().Where(x=>x.IntegrationId==integrationId&&x.ContentHash==hash).OrderByDescending(x=>x.CreatedAt).FirstOrDefaultAsync(ct);
        if(existing!=null)return existing;
        var import = new JournalImport { IntegrationId = integrationId, ContentHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(csv))),
            RowsJson = JsonSerializer.Serialize(rows,Json), RowCount = rows.Count };
        integration.ImportConfirmed = false;
        db.Add(import); await db.SaveChangesAsync(ct); return import;
    }
    public async Task<JournalImport> ConfirmAsync(Guid id, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        // Claim inside a write transaction: repeated confirmations return the committed result.
        var claimed = await db.Set<JournalImport>().Where(x=>x.Id==id && x.Status=="Preview")
            .ExecuteUpdateAsync(s=>s.SetProperty(x=>x.Status,"Committing"),ct);
        var import = await db.Set<JournalImport>().AsNoTracking().SingleAsync(x=>x.Id==id,ct);
        if (claimed == 0)
        {
            if (import.Status=="Committed") return import;
            throw new InvalidOperationException("Import is already being processed.");
        }
        var integration = await db.Set<Integration>().SingleAsync(x=>x.Id==import.IntegrationId,ct);
        var rows=JsonSerializer.Deserialize<JournalRow[]>(import.RowsJson,Json)!;
        foreach(var row in rows)
        {
            var vacancy=await db.Vacancies.SingleOrDefaultAsync(x=>x.JobPlatformId==integration.JobPlatformId && x.ExternalId==row.ExternalId,ct);
            if(vacancy==null)
            {
                vacancy=new Vacancy{JobPlatformId=integration.JobPlatformId!.Value,ExternalId=row.ExternalId,Title=row.Title,Employer=row.Employer,Url=row.Url,Description=row.Description};
                db.Add(vacancy);import.Added++; await db.SaveChangesAsync(ct);
            }
            else
            {
                import.Duplicates++;
                if(vacancy.Title!=row.Title||vacancy.Description!=row.Description)
                {
                    import.Conflicts++;
                    if(integration.ConflictPolicy=="update-vacancies"){vacancy.Title=row.Title;vacancy.Description=row.Description;vacancy.Employer=row.Employer;vacancy.Url=row.Url;}
                }
            }
            if(!string.IsNullOrWhiteSpace(row.ApplicationState)&&
                !await db.Set<HistoricalApplication>().AnyAsync(x=>x.CandidateProfileId==integration.CandidateProfileId&&x.VacancyId==vacancy.Id,ct))
            {
                db.Add(new HistoricalApplication{IntegrationId=integration.Id,CandidateProfileId=integration.CandidateProfileId,VacancyId=vacancy.Id,SourceKey=row.ExternalId,SourceState=row.ApplicationState});
                var pending=await db.Applications.SingleOrDefaultAsync(x=>x.CandidateProfileId==integration.CandidateProfileId&&x.VacancyId==vacancy.Id,ct);
                if(pending!=null&&pending.State is not (ApplicationState.Submitted or ApplicationState.EmployerResponded or ApplicationState.Closed or ApplicationState.SubmissionUnknown))
                {
                    pending.State=ApplicationState.Blocked;pending.Revision++;
                    db.Audit.Add(new AuditEvent{EntityId=pending.Id,EntityType=nameof(JobApplication),Action="BlockedByImportedHistory",Details="Imported history requires reconciliation; do not resend."});
                }
            }
            await db.SaveChangesAsync(ct);
        }
        import.Status="Committed";import.CommittedAt=DateTime.UtcNow;
        await db.Set<JournalImport>().Where(x=>x.Id==id).ExecuteUpdateAsync(s=>s.SetProperty(x=>x.Status,import.Status)
            .SetProperty(x=>x.CommittedAt,import.CommittedAt).SetProperty(x=>x.Added,import.Added)
            .SetProperty(x=>x.Duplicates,import.Duplicates).SetProperty(x=>x.Conflicts,import.Conflicts),ct);
        integration.ImportConfirmed=!await db.Set<JournalImport>().AnyAsync(x=>x.IntegrationId==integration.Id&&x.Status=="Preview",ct);
        integration.LastStatus="Imported";integration.LastCheckedAt=DateTime.UtcNow;
        db.Audit.Add(new AuditEvent{EntityId=integration.Id,EntityType=nameof(Integration),Action="ImportConfirmed",Details=$"Rows={import.RowCount}, added={import.Added}, conflicts={import.Conflicts}"});
        await db.SaveChangesAsync(ct);await tx.CommitAsync(ct);return import;
    }
}
