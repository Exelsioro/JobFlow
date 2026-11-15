using System.Text.Json;
using JobFlow.Application;
using JobFlow.Domain;
using Microsoft.EntityFrameworkCore;
namespace JobFlow.Infrastructure;
public sealed class JobSearchWorkflow(JobFlowDb db, IAgentExecutor agents) : IJobSearchWorkflow
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public async Task<WorkflowRun> RunAsync(Guid definitionId, CancellationToken ct)
    {
        var definition = await db.Set<WorkflowDefinition>().AsNoTracking().SingleAsync(x => x.Id == definitionId, ct);
        if (!definition.Enabled) throw new InvalidOperationException("Enable this workflow before running it.");
        await ValidateAsync(definition, ct);
        var now = DateTime.UtcNow; var token = Guid.NewGuid().ToString("N");
        if (await db.Set<WorkflowDefinition>().Where(x => x.Id == definitionId && (x.LeaseUntil == null || x.LeaseUntil < now))
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.LeaseUntil, now.AddMinutes(20)).SetProperty(x => x.LeaseToken, token), ct) != 1)
            throw new InvalidOperationException("Workflow is already running.");
        await db.Set<WorkflowRun>().Where(x => x.WorkflowDefinitionId == definitionId && x.Status == "Running")
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, "Interrupted").SetProperty(x => x.FinishedAt, now), ct);
        var run = new WorkflowRun { WorkflowDefinitionId = definitionId, CandidateProfileId = definition.CandidateProfileId };
        db.Add(run); await db.SaveChangesAsync(ct);
        try
        {
            var platformIds = definition.SelectedPlatforms();
            var platforms = await db.Platforms.Where(x => platformIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, ct);
            var profile = await db.Profiles.SingleAsync(x => x.Id == definition.CandidateProfileId, ct);
            var discoveryFailures = new List<string>();
            using var http = PlatformDiscovery.CreateClient();
            foreach (var id in platformIds)
            {
                var platform = platforms[id];
                if (platform.Adapter == "search-only") continue;
                try
                {
                    await new PlatformDiscovery(db, http).ImportAsync(platform, ct);
                }
                catch (Exception ex) when (!ct.IsCancellationRequested &&
                    ex is HttpRequestException or OperationCanceledException or InvalidOperationException or ArgumentException or JsonException)
                {
                    discoveryFailures.Add(platform.Name);
                }
            }

            var statusSources = await db.Set<Integration>().Where(x => x.Enabled &&
                x.CandidateProfileId == profile.Id && x.JobPlatformId.HasValue && platformIds.Contains(x.JobPlatformId.Value) &&
                (x.Kind == "hh-status" || x.Kind == "status-json")).Select(x => new { x.Id, x.Name }).ToListAsync(ct);
            foreach (var source in statusSources)
            {
                try { await new RemoteIntegrationService(db, new SecretStore(), http, agents).PullAsync(source.Id, ct); }
                catch (InvalidOperationException) when (!ct.IsCancellationRequested) { discoveryFailures.Add(source.Name); }
            }

            // Imported vacancies remain usable even when a live source is temporarily unavailable.
            var vacancies = await db.Vacancies.Where(x => platformIds.Contains(x.JobPlatformId))
                .OrderBy(x => x.CreatedAt).ThenBy(x => x.Id).ToListAsync(ct);
            var rules = JsonSerializer.Deserialize<SearchRules>(definition.RulesJson, Json) ?? new();
            var facts = new[] { profile.Experience, profile.Skills, profile.Education, profile.Languages }.Where(x => !string.IsNullOrWhiteSpace(x)).ToArray();
            foreach (var vacancy in vacancies)
            {
                ct.ThrowIfCancellationRequested();
                if(await db.Set<HistoricalApplication>().AnyAsync(x=>x.CandidateProfileId==profile.Id&&x.VacancyId==vacancy.Id,ct))continue;
                var application = await db.Applications.SingleOrDefaultAsync(x => x.CandidateProfileId == profile.Id && x.VacancyId == vacancy.Id, ct);
                if (application != null && (application.WorkflowDefinitionId != definitionId ||
                    application.State is not (ApplicationState.Discovered or ApplicationState.Evaluating or ApplicationState.Drafting or ApplicationState.Blocked))) continue;
                if (run.Prepared + run.Rejected >= 10) break;
                if (application == null)
                {
                    application = new JobApplication { IsTest = platforms[vacancy.JobPlatformId].Adapter == "test-form", CandidateProfileId = profile.Id, VacancyId = vacancy.Id, ResumeVersionId = definition.ResumeVersionId, WorkflowDefinitionId = definitionId };
                    db.Add(application); await db.SaveChangesAsync(ct);
                }
                try
                {
                    run.Stage = "Evaluation";
                    if (application.State is ApplicationState.Discovered or ApplicationState.Blocked) Move(application, ApplicationState.Evaluating);
                    await db.SaveChangesAsync(ct);
                    EvaluationResult evaluation;
                    if (application.State == ApplicationState.Drafting)
                    {
                        var previous = await db.Set<VacancyEvaluation>().Where(x => x.CandidateProfileId == profile.Id && x.VacancyId == vacancy.Id)
                            .OrderByDescending(x => x.CreatedAt).FirstAsync(ct);
                        evaluation = JsonSerializer.Deserialize<EvaluationResult>(previous.ResultJson, Json)!;
                    }
                    else
                    {
                        evaluation = RuleEvaluator.Evaluate(profile, vacancy, rules, DateTime.UtcNow);
                        if (evaluation.Decision != "Reject" && definition.EvaluationAgentId is Guid evaluator)
                        {
                            var response = await agents.ExecuteAsync(new(evaluator, JsonSerializer.Serialize(new { candidate = profile, facts, vacancy, rules }, Json)), ct);
                            ValidateOutput(response.Output, AgentTemplates.EvaluationSchema);
                            var ai = JsonSerializer.Deserialize<EvaluationResult>(response.Output, Json)!;
                            var verifiedSkills = profile.Skills.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                            if (ai.Matches.Any(x => !verifiedSkills.Contains(x, StringComparer.OrdinalIgnoreCase)))
                                throw new InvalidOperationException("AI claimed skills not present in candidate evidence.");
                            evaluation = ai with { Clarifications = ai.Clarifications.Concat(evaluation.Clarifications).Distinct().ToArray() };
                        }
                        db.Add(new VacancyEvaluation { CandidateProfileId = profile.Id, VacancyId = vacancy.Id, ResultJson = JsonSerializer.Serialize(evaluation, Json) });
                        Move(application, evaluation.Decision == "Reject" ? ApplicationState.Rejected : ApplicationState.Drafting);
                        await db.SaveChangesAsync(ct);
                    }
                    if (application.State == ApplicationState.Rejected) { run.Rejected++; continue; }
                    run.Stage = "CoverLetter"; await db.SaveChangesAsync(ct);
                    var letter = $"Здравствуйте! Рассматриваю вакансию «{vacancy.Title}».\n\nПодтверждённые сведения из моего профиля:\n" +
                        string.Join("\n", facts) + "\n\nБуду рад обсудить требования и условия.\n" + profile.Name;
                    if (definition.LetterAgentId is Guid writer)
                    {
                        var response = await agents.ExecuteAsync(new(writer, JsonSerializer.Serialize(new { candidate = profile, facts, vacancy, evaluation }, Json)), ct);
                        ValidateOutput(response.Output, AgentTemplates.LetterSchema);
                        var draft = JsonSerializer.Deserialize<LetterDraft>(response.Output, Json)!;
                        if (draft.UsedFacts.Any(x => !facts.Contains(x, StringComparer.Ordinal)))
                            throw new InvalidOperationException("AI cited facts not present in candidate evidence.");
                        letter = draft.Letter;
                    }
                    if (string.IsNullOrWhiteSpace(letter) || letter.Length > 30000) throw new InvalidOperationException("Cover letter is empty or too long.");
                    run.Stage = "Preparation";
                    application.CoverLetter = letter; Move(application, ApplicationState.ReadyForReview); run.Prepared++;
                    await db.SaveChangesAsync(ct);
                }
                catch
                {
                    if (application.State is ApplicationState.Evaluating or ApplicationState.Drafting) Move(application, ApplicationState.Blocked);
                    await db.SaveChangesAsync(CancellationToken.None); throw;
                }
            }
            if (discoveryFailures.Count > 0)
            {
                run.Error = "Platform checks failed: " + string.Join(", ", discoveryFailures);
                throw new InvalidOperationException(run.Error);
            }
            run.Status = "Succeeded"; run.Stage = "Review";
            return run;
        }
        catch (Exception ex)
        {
            run.Status = ex is OperationCanceledException ? "Interrupted" : "Failed";
            if (string.IsNullOrEmpty(run.Error))
                run.Error = ex is OperationCanceledException ? "Cancelled or timed out." : "Workflow stage failed. Check configuration and agent execution history.";
            throw;
        }
        finally
        {
            run.FinishedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(CancellationToken.None);
            await db.Set<WorkflowDefinition>().Where(x => x.Id == definitionId && x.LeaseToken == token)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.LeaseUntil, (DateTime?)null).SetProperty(x => x.LeaseToken, (string?)null), CancellationToken.None);
        }
    }
    public async Task ValidateAsync(WorkflowDefinition definition, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(definition.Name)) throw new ArgumentException("Workflow name is required.");
        var rules = JsonSerializer.Deserialize<SearchRules>(definition.RulesJson, Json) ?? throw new ArgumentException("Rules must be an object.");
        if (rules.MinimumSalary < 0 || rules.MaxAgeDays < 1) throw new ArgumentException("Invalid salary or publication age.");
        if (rules.MinimumSalary.HasValue && string.IsNullOrWhiteSpace(rules.Currency)) throw new ArgumentException("Salary currency is required.");
        var platforms = definition.SelectedPlatforms();
        if (platforms.Length is < 1 or > 50 || platforms.Distinct().Count() != platforms.Length)
            throw new ArgumentException("Select 1–50 distinct platforms.");
        if (await db.Platforms.CountAsync(x => platforms.Contains(x.Id), ct) != platforms.Length)
            throw new ArgumentException("A selected platform does not exist.");
        if (!await (from v in db.ResumeVersions join r in db.Resumes on v.ResumeId equals r.Id
            where v.Id == definition.ResumeVersionId && r.CandidateProfileId == definition.CandidateProfileId select v).AnyAsync(ct))
            throw new ArgumentException("Resume version must belong to the workflow candidate.");
        foreach (var (id, kind) in new[] { (definition.EvaluationAgentId, "Evaluation"), (definition.LetterAgentId, "CoverLetter") })
            if (id.HasValue && !await db.Agents.AnyAsync(x => x.Id == id && x.CandidateProfileId == definition.CandidateProfileId && x.TaskType == kind, ct))
                throw new ArgumentException($"Select a {kind} agent belonging to the same candidate.");
    }
    public async Task SaveDraftAsync(Guid id, string letter, int revision, CancellationToken ct)
    {
        var application = await db.Applications.SingleAsync(x => x.Id == id, ct);
        if (application.Revision != revision) throw new DbUpdateConcurrencyException("Draft changed; refresh first.");
        if (application.State is not (ApplicationState.Drafting or ApplicationState.ReadyForReview)) throw new InvalidOperationException("Only drafts awaiting review can be edited.");
        if (string.IsNullOrWhiteSpace(letter) || letter.Length > 30000) throw new ArgumentException("Letter must contain 1–30000 characters.");
        application.CoverLetter = letter; application.Revision++;
        db.Audit.Add(new AuditEvent { EntityId = id, EntityType = nameof(JobApplication), Action = "DraftEdited" });
        await db.SaveChangesAsync(ct);
    }
    private void Move(JobApplication application, ApplicationState state)
    {
        var previous = application.State; Workflow.Transition(application, state);
        db.Audit.Add(new AuditEvent { EntityId = application.Id, EntityType = nameof(JobApplication), Action = "StateChanged", Details = $"{previous} -> {state}" });
    }
    private static void ValidateOutput(string output, string schema)
    {
        using var document = JsonDocument.Parse(output);
        if (!global::Json.Schema.JsonSchema.FromText(schema).Evaluate(document.RootElement).IsValid) throw new InvalidOperationException("Workflow output schema mismatch.");
    }
    private record LetterDraft(string Letter, string[] UsedFacts);
}

