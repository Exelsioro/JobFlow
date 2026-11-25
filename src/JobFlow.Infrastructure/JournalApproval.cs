using JobFlow.Domain;

namespace JobFlow.Infrastructure;

public static class JournalApproval
{
    public const string NoPriorJournal = "NoPriorJournalDeclared";
    public const string SourceChanged = "JournalSourceChanged";

    // Normal search workflows keep the import gate. An explicit ready-only run can use this declaration.
    public static IQueryable<Integration> Pending(JobFlowDb db) =>
        db.Set<Integration>().Where(source => (source.Kind == "csv" || source.Kind == "google-sheets") &&
            !source.ImportConfirmed &&
            (db.Set<JournalImport>().Any(import => import.IntegrationId == source.Id && import.Status == "Preview") ||
             !db.Audit.Any(declaration => declaration.EntityId == source.Id && declaration.Action == NoPriorJournal &&
                !db.Audit.Any(change => change.EntityId == source.Id && change.Action == SourceChanged &&
                    change.CreatedAt >= declaration.CreatedAt))));
}
