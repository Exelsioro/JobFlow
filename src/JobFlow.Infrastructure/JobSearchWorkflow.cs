using System.Text.Json;
using JobFlow.Application;
using JobFlow.Domain;
using Microsoft.EntityFrameworkCore;
namespace JobFlow.Infrastructure;
public sealed class JobSearchWorkflow(JobFlowDb db, IAgentExecutor agents, IBrowserWorkflowDriver? browser = null, HttpMessageHandler? integrationHandler = null, bool monitorWithinSearch = true) : IJobSearchWorkflow
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public async Task<WorkflowRun> RunAsync(Guid definitionId, CancellationToken ct) => await RunAsync(definitionId, ct, null, false);
    public async Task<WorkflowRun> RunAsync(Guid definitionId, CancellationToken ct, Guid? targetVacancyId, bool preparationOnly, bool outsideAgeLimit = false)
    {
        var definition = await db.Set<WorkflowDefinition>().AsNoTracking().SingleAsync(x => x.Id == definitionId, ct);
        if (preparationOnly != targetVacancyId.HasValue) throw new ArgumentException("Подготовка одного отклика требует конкретную вакансию и запрещает отправку.");
        if (outsideAgeLimit && !preparationOnly) throw new ArgumentException("Исключение по возрасту доступно только для разовой подготовки одной вакансии.");
        if (!definition.Enabled) throw new InvalidOperationException("Enable this workflow before running it.");
        await ValidateAsync(definition, ct);
        var approvedOnly = definition.SendsApprovedOnly();
        if (preparationOnly)
        {
            if (approvedOnly) throw new ArgumentException("Этот workflow только отправляет готовые отклики.");
            var target = await db.Vacancies.SingleAsync(x => x.Id == targetVacancyId, ct);
            if (target.IsArchived || !definition.SelectedPlatforms().Contains(target.JobPlatformId)) throw new ArgumentException("Вакансия не относится к выбранному workflow или удалена из поиска.");
            definition.JobPlatformIds = [target.JobPlatformId]; definition.JobPlatformId = target.JobPlatformId;
            definition.AutoSubmit = false; definition.CheckMail = false; definition.VacancySource = "saved";
            definition.ApplicationStatusFilter = "pending"; definition.EvaluationResultFilter = "all";
            definition.IgnorePublicationAge = outsideAgeLimit;
            definition.MaxApplicationsPerRun = 1; definition.MaxVacancyChecksPerRun = 1;
        }
        var now = DateTime.UtcNow; var token = Guid.NewGuid().ToString("N");
        var timeoutSeconds = await db.Tasks.Where(x => x.WorkflowDefinitionId == definitionId)
            .Select(x => (int?)x.TimeoutSeconds).MaxAsync(ct) ?? TaskExecutionLimits.DefaultBrowserWorkflowTimeoutSeconds;
        var leaseUntil = now + TaskExecutionLimits.LeaseDuration(timeoutSeconds);
        if (await db.Set<WorkflowDefinition>().Where(x => x.Id == definitionId && (x.LeaseUntil == null || x.LeaseUntil < now))
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.LeaseUntil, leaseUntil).SetProperty(x => x.LeaseToken, token), ct) != 1)
            throw new InvalidOperationException("Workflow is already running.");
        await db.Set<WorkflowRun>().Where(x => x.WorkflowDefinitionId == definitionId && x.Status == "Running")
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, "Interrupted").SetProperty(x => x.FinishedAt, now), ct);
        var run = new WorkflowRun { WorkflowDefinitionId = definitionId, CandidateProfileId = definition.CandidateProfileId, TargetVacancyId = targetVacancyId, PreparationOnly = preparationOnly };
        db.Add(run); await db.SaveChangesAsync(ct);
        IAsyncDisposable? browserRun = null;
        try
        {
            if (definition.ExecutionMode == "browser" && browser == null)
                throw new InvalidOperationException("Browser workflows must run through the API browser host.");
            if (definition.ExecutionMode == "browser") browserRun = await browser!.BeginRunAsync(definition, ct);
            await ReconcileInterruptedSubmissionsAsync(definition, ct);
            var platformIds = definition.SelectedPlatforms();
            var platforms = await db.Platforms.Where(x => platformIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, ct);
            var profile = await db.Profiles.SingleAsync(x => x.Id == definition.CandidateProfileId, ct);
            var duplicateReasons = approvedOnly ? await ApprovedApplicationDuplicates.CheckAsync(db, profile.Id, ct) : new Dictionary<Guid, string>();
            var discoveryFailures = new List<string>();
            var failureDetails = new List<string>();
            var technicalDetails = new List<string>();
            var discoveredIds = new HashSet<Guid>();
            var discoveryOrder = new Dictionary<Guid, int>();
            void Trace(string action, object details) => db.Audit.Add(new AuditEvent { EntityId = run.Id, EntityType = nameof(WorkflowRun),
                Action = action, Details = JsonSerializer.Serialize(new { runId = run.Id, workflowId = definitionId, stage = run.Stage, details }, Json) });
            Trace("WorkflowStarted", new { definition.ExecutionMode, definition.VacancySource, definition.ApplicationStatusFilter, definition.EvaluationResultFilter, definition.AutoSubmit, definition.ResumeMode, definition.FreshnessDays, outsideAgeLimit, definition.MaxApplicationsPerRun, definition.MaxVacancyChecksPerRun, definition.MaxVacanciesPerPlatform, definition.MaxBrowserSteps, platformIds });
            using var http = integrationHandler == null ? PlatformDiscovery.CreateClient() : new HttpClient(integrationHandler, false);
            async Task<bool> DiscoverPlatformAsync(Guid id)
            {
                run.Stage = "Discovery";
                var platform = platforms[id];
                if (definition.ExecutionMode != "browser" && platform.Adapter == "search-only") return true;
                try
                {
                    if (definition.ExecutionMode == "browser")
                    {
                        var found = await browser!.DiscoverAsync(definition, platform, profile, ct);
                        foreach (var vacancy in found.DistinctBy(x => x.ExternalId))
                        {
                            var existing = await db.Vacancies.SingleOrDefaultAsync(x => x.JobPlatformId == platform.Id && x.ExternalId == vacancy.ExternalId, ct);
                            if (existing == null) db.Add(vacancy);
                            else { existing.Title = vacancy.Title; existing.Employer = vacancy.Employer; existing.Url = vacancy.Url; existing.Description = vacancy.Description; }
                            discoveredIds.Add(existing?.Id ?? vacancy.Id);
                            discoveryOrder.TryAdd(existing?.Id ?? vacancy.Id, discoveryOrder.Count);
                            Trace("WorkflowVacancyDiscovered", new { vacancyId = existing?.Id ?? vacancy.Id, platformId = id, vacancy.ExternalId, vacancy.Title, vacancy.Url });
                        }
                        Trace("WorkflowDiscoveryCompleted", new { platformId = id, count = found.DistinctBy(x => x.ExternalId).Count() });
                        run.Discovered += found.DistinctBy(x => x.ExternalId).Count();
                        await db.SaveChangesAsync(ct);
                    }
                    else await new PlatformDiscovery(db, http).ImportAsync(platform, ct);
                    return true;
                }
                catch (ModelProviderException) { throw; }
                catch (Exception ex) when (!ct.IsCancellationRequested &&
                    ex is HttpRequestException or OperationCanceledException or InvalidOperationException or ArgumentException or JsonException or Microsoft.Playwright.PlaywrightException)
                {
                    discoveryFailures.Add(platform.Name);
                    run.Errors++;
                    failureDetails.Add(platform.Name + ": " + ex.Message);
                    Trace("WorkflowDiscoveryFailed", new { platformId = id, reason = ex.Message });
                    await db.SaveChangesAsync(ct);
                    return false;
                }
            }

            var statusSources = await db.Set<Integration>().Where(x => !preparationOnly && monitorWithinSearch && x.Enabled && x.CandidateProfileId == profile.Id &&
                (x.JobPlatformId.HasValue && platformIds.Contains(x.JobPlatformId.Value) && (x.Kind == "hh-status" || x.Kind == "status-json") ||
                 definition.ExecutionMode == "browser" && definition.CheckMail && x.Kind == "gmail"))
                .Select(x => new { x.Id, x.Name }).ToListAsync(ct);
            foreach (var source in statusSources)
            {
                try { await new RemoteIntegrationService(db, new SecretStore(), http, agents).PullAsync(source.Id, ct); }
                catch (InvalidOperationException) when (!ct.IsCancellationRequested) { discoveryFailures.Add(source.Name); }
            }

            var rules = JsonSerializer.Deserialize<SearchRules>(definition.RulesJson, Json) ?? new();
            if (outsideAgeLimit) rules = rules with { MaxAgeDays = null };
            var facts = new[] { profile.Experience, profile.Skills, profile.Education, profile.Languages, profile.Dossier }.Where(x => !string.IsNullOrWhiteSpace(x)).ToArray();
            var completedApplications = 0;
            var priorApplications = await db.Applications.Where(x => x.CandidateProfileId == profile.Id).ToDictionaryAsync(x => x.VacancyId, ct);
            var priorEvaluations = (await db.Set<VacancyEvaluation>().AsNoTracking().Where(x => x.CandidateProfileId == profile.Id)
                .OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.Id).ToListAsync(ct))
                .GroupBy(x => x.VacancyId).ToDictionary(x => x.Key, x => x.First());
            var offset = definition.ExecutionMode == "browser" ? await db.Set<WorkflowRun>().CountAsync(x => x.WorkflowDefinitionId == definitionId && x.Id != run.Id, ct) % platformIds.Length : 0;
            var visitIds = platformIds.Skip(offset).Concat(platformIds.Take(offset)).ToArray();
            var queues = new List<Queue<Vacancy>>();
            // Search every source before spending the preparation budget. Preserve observed result order.
            foreach (var platformId in visitIds)
            {
                if (definition.VacancySource != "saved" && !await DiscoverPlatformAsync(platformId) && definition.VacancySource == "search") continue;
                if (approvedOnly)
                {
                    run.Stage = "SavedVacancies";
                    var ready = await (from vacancy in db.Vacancies
                        join application in db.Applications on vacancy.Id equals application.VacancyId
                        where vacancy.JobPlatformId == platformId && !vacancy.IsArchived && vacancy.Availability != "Closed" &&
                            application.CandidateProfileId == profile.Id && application.State == ApplicationState.ReadyToSubmit && !application.IsTest
                        orderby application.CreatedAt, application.Id
                        select vacancy).ToListAsync(ct);
                    queues.Add(new(ready));
                    Trace("WorkflowApprovedQueueLoaded", new { platformId, count = ready.Count });
                }
                else if (definition.VacancySource != "search")
                {
                    run.Stage = "SavedVacancies";
                    var pending = await db.Vacancies.Where(v => v.JobPlatformId == platformId && !v.IsArchived && v.Availability != "Closed" &&
                        (!targetVacancyId.HasValue || v.Id == targetVacancyId) &&
                        !db.Set<HistoricalApplication>().Any(a => a.CandidateProfileId == profile.Id && a.VacancyId == v.Id) &&
                        (!db.Applications.Any(a => a.CandidateProfileId == profile.Id && a.VacancyId == v.Id) ||
                         db.Applications.Any(a => a.CandidateProfileId == profile.Id && a.VacancyId == v.Id && a.WorkflowDefinitionId == definitionId &&
                            (a.State == ApplicationState.Discovered || a.State == ApplicationState.Evaluating ||
                             a.State == ApplicationState.Drafting || a.State == ApplicationState.Blocked || a.State == ApplicationState.Skipped))))
                        .OrderBy(v => db.Applications.Any(a => a.CandidateProfileId == profile.Id && a.VacancyId == v.Id))
                        .ThenBy(v => v.CreatedAt).ThenBy(v => v.Id).ToListAsync(ct);
                    var selected = pending.Where(v => MatchesQueueFilters(definition, priorApplications.GetValueOrDefault(v.Id), priorEvaluations.GetValueOrDefault(v.Id))).ToArray();
                    queues.Add(new(selected));
                    Trace("WorkflowSavedQueueLoaded", new { platformId, count = selected.Length, excludedByFilters = pending.Count - selected.Length });
                }
                else
                {
                    var vacancies = await db.Vacancies.Where(x => x.JobPlatformId == platformId && !x.IsArchived)
                        .OrderByDescending(x => x.PublishedAt).ThenByDescending(x => x.CreatedAt).ThenBy(x => x.Id).ToListAsync(ct);
                    vacancies = vacancies.Where(v => MatchesQueueFilters(definition, priorApplications.GetValueOrDefault(v.Id), priorEvaluations.GetValueOrDefault(v.Id))).ToList();
                    queues.Add(new(definition.ExecutionMode == "browser" ? vacancies.OrderBy(x => discoveryOrder.GetValueOrDefault(x.Id, int.MaxValue)) : vacancies));
                }
            }
            await db.SaveChangesAsync(ct);
            // Round-robin lets a small check budget cover more than the first selected platform.
            while (queues.Any(x => x.Count > 0))
            foreach (var queue in queues)
            {
                if (!queue.TryDequeue(out var vacancy)) continue;
                ct.ThrowIfCancellationRequested();
                void Skip(string reason) => Trace("WorkflowVacancySkipped", new { vacancyId = vacancy.Id, vacancy.Title, vacancy.Url, reason });
                if (!preparationOnly && await db.Tasks.AnyAsync(x => x.Kind == "PrepareApplication" && x.RunRequested &&
                    x.CandidateProfileId == profile.Id && x.VacancyId == vacancy.Id, ct))
                { Skip("This vacancy has a separate preparation queued."); continue; }
                if (definition.ExecutionMode == "browser" && definition.VacancySource == "search" && !discoveredIds.Contains(vacancy.Id)) { Skip("Not returned by this run's completed browser discovery; cached vacancy is not eligible."); continue; }
                if (!preparationOnly && definition.FreshnessDays.HasValue && vacancy.PublishedAt < DateTime.UtcNow.AddDays(-definition.FreshnessDays.Value)) { run.Skipped++; Skip("Stored publication date exceeds the workflow age limit."); continue; }
                if(await db.Set<HistoricalApplication>().AnyAsync(x=>x.CandidateProfileId==profile.Id&&x.VacancyId==vacancy.Id,ct)) { Skip("Historical application already exists."); continue; }
                var application = await db.Applications.SingleOrDefaultAsync(x => x.CandidateProfileId == profile.Id && x.VacancyId == vacancy.Id, ct);
                if (approvedOnly && application != null) await db.Entry(application).ReloadAsync(ct);
                if (approvedOnly && (application == null || db.Entry(application).State == EntityState.Detached ||
                    application.State != ApplicationState.ReadyToSubmit || application.IsTest))
                { Skip("Application is no longer ready for submission."); continue; }
                if (!approvedOnly && application != null && (application.WorkflowDefinitionId != definitionId ||
                    application.State is not (ApplicationState.Discovered or ApplicationState.Evaluating or ApplicationState.Drafting or ApplicationState.Blocked or ApplicationState.Skipped) &&
                    !(definition.ExecutionMode == "browser" && definition.AutoSubmit && application.State == ApplicationState.ReadyToSubmit))) { Skip("Application belongs to another workflow or its current state blocks processing: " + application.State); continue; }
                if ((approvedOnly ? run.Checked : completedApplications) >= definition.MaxApplicationsPerRun)
                { Skip(approvedOnly ? "Approved application limit reached." : "Prepared application limit reached."); continue; }
                if (run.Checked >= definition.MaxVacancyChecksPerRun) { Skip("Vacancy check limit reached."); continue; }
                run.Checked++;
                if (application == null)
                {
                    application = new JobApplication { IsTest = platforms[vacancy.JobPlatformId].Adapter == "test-form", CandidateProfileId = profile.Id, VacancyId = vacancy.Id, ResumeVersionId = definition.ResumeVersionId, WorkflowDefinitionId = definitionId };
                    db.Add(application); await db.SaveChangesAsync(ct);
                }
                var stateBeforeProcessing = application.State;
                var clarifications = await ApplicationClarifications.ReadAsync(db, application.Id, ct);
                var candidate = profile;
                var vacancyWorkflow = definition;
                if (clarifications.Length > 0)
                {
                    var previousIssues = await db.Audit.AsNoTracking().Where(x => x.EntityId == application.Id && x.Action == "BrowserApplicationBlocked")
                        .OrderByDescending(x => x.CreatedAt).Select(x => x.Details).FirstOrDefaultAsync(ct);
                    (candidate, vacancyWorkflow) = ApplicationClarifications.Context(profile, definition, clarifications, application, previousIssues);
                    db.Audit.Add(new AuditEvent { EntityId = application.Id, EntityType = nameof(JobApplication), Action = "ApplicationClarificationApplied",
                        Details = JsonSerializer.Serialize(new { runId = run.Id, clarificationIds = clarifications.Select(x => x.Id), vacancy.Id }, Json) });
                }
                try
                {
                    if (definition.ExecutionMode == "browser" && application.State == ApplicationState.ReadyToSubmit)
                    {
                        if (duplicateReasons.TryGetValue(application.Id, out var duplicate))
                            throw new HumanReviewRequiredException(duplicate + " Отправки не было.");
                        if (await PrepareExternalAsync(application, vacancy, candidate, vacancyWorkflow))
                        {
                            if (application.State == ApplicationState.Submitted) completedApplications++;
                            continue;
                        }
                        await SubmitAsync(application, vacancy, candidate, vacancyWorkflow);
                        if (application.State == ApplicationState.Submitted) completedApplications++;
                        continue;
                    }
                    run.Stage = "Evaluation";
                    if (application.State is ApplicationState.Discovered or ApplicationState.Blocked or ApplicationState.Skipped) Move(application, ApplicationState.Evaluating);
                    await db.SaveChangesAsync(ct);
                    if (preparationOnly && !outsideAgeLimit && definition.FreshnessDays is int maxAge && vacancy.PublishedAt < DateTime.UtcNow.AddDays(-maxAge))
                        throw new VacancyUnavailableException("stale", $"Вакансия опубликована {vacancy.PublishedAt:dd.MM.yyyy}; лимит актуальности — {maxAge} дн.");
                    EvaluationResult evaluation;
                    if (application.State == ApplicationState.Drafting)
                    {
                        if (definition.ExecutionMode == "browser" && definition.VacancySource != "search")
                            await browser!.ReadVacancyAsync(definition, platforms[vacancy.JobPlatformId], vacancy, ct);
                        var previous = await db.Set<VacancyEvaluation>().Where(x => x.CandidateProfileId == profile.Id && x.VacancyId == vacancy.Id)
                            .OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.Id).FirstAsync(ct);
                        evaluation = JsonSerializer.Deserialize<EvaluationResult>(previous.ResultJson, Json)!;
                    }
                    else
                    {
                        if (definition.ExecutionMode == "browser")
                            await browser!.ReadVacancyAsync(definition, platforms[vacancy.JobPlatformId], vacancy, ct);
                        evaluation = RuleEvaluator.Evaluate(candidate, vacancy, rules, DateTime.UtcNow);
                        if (evaluation.Decision != "Reject" && definition.EvaluationAgentId is Guid evaluator)
                        {
                            var response = await agents.ExecuteAsync(new(evaluator, JsonSerializer.Serialize(new { candidate, candidateSkills = EvaluationEvidence.CandidateSkills(candidate),
                                facts = new[] { candidate.Experience, candidate.Skills, candidate.Education, candidate.Languages, candidate.Dossier }, vacancy, rules, currentUtc = DateTime.UtcNow,
                                freshnessDays = outsideAgeLimit ? null : vacancyWorkflow.FreshnessDays, vacancyWorkflow.Instructions }, Json),
                                VacancyAssessmentPolicy.Evaluation + (clarifications.Length > 0 ? "\n" + ApplicationClarifications.Guidance : "") +
                                (outsideAgeLimit ? "\nThe user explicitly allowed preparation for this vacancy despite its publication age. Do not reject or ask for clarification on age alone. All other requirements and open-status checks still apply." : ""), AgentTemplates.EvaluationSchema), ct);
                            ValidateOutput(response.Output, AgentTemplates.EvaluationSchema);
                            var ai = JsonSerializer.Deserialize<EvaluationResult>(response.Output, Json)!;
                            var checkedEvaluation = EvaluationEvidence.Validate(candidate, ai, response.ExecutionId);
                            // Hard rule conflicts were handled before invoking the evaluator.
                            // Its vacancy-specific questions replace the generic rule-only review checklist.
                            evaluation = checkedEvaluation with
                            {
                                CandidateQuestions = checkedEvaluation.Decision == "NeedsReview"
                                    ? ai.Clarifications.Select(question => question.Trim())
                                        .Where(question => question.EndsWith('?'))
                                        .Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
                                    : []
                            };
                        }
                        var savedEvaluation = new VacancyEvaluation { CandidateProfileId = profile.Id, VacancyId = vacancy.Id, ResultJson = JsonSerializer.Serialize(evaluation, Json) };
                        db.Add(savedEvaluation);
                        Trace("WorkflowVacancyEvaluated", new { vacancyId = vacancy.Id, applicationId = application.Id, evaluationId = savedEvaluation.Id, vacancy.Url, evaluation.Decision, evaluation.Rationale, evaluation.MissingSkills, evaluation.UnverifiedRequirements });
                        if (evaluation.Validation is { } validation)
                            db.Audit.Add(new AuditEvent { EntityId = application.Id, EntityType = nameof(JobApplication), Action = "EvaluationValidated",
                                Details = JsonSerializer.Serialize(new { evaluationId = savedEvaluation.Id, validation.AgentExecutionId, validation.IsValid,
                                    rejectedSkills = validation.Skills.Where(x => x.Status == "Unsupported").Select(x => x.ClaimedSkill).ToArray(), validation.Warnings }, Json) });
                        // Keep the decision and validation evidence durable even when this application is blocked.
                        await db.SaveChangesAsync(ct);
                        if (evaluation.Validation is { IsValid: false } invalid)
                            throw new HumanReviewRequiredException("Оценка сохранена; требуется проверка навыков: " +
                                string.Join("; ", invalid.Warnings)[..Math.Min(1400, string.Join("; ", invalid.Warnings).Length)]);
                        Move(application, evaluation.Decision == "Reject" ? ApplicationState.Rejected : ApplicationState.Drafting);
                        await db.SaveChangesAsync(ct);
                    }
                    if (application.State == ApplicationState.Rejected) { run.Rejected++; continue; }
                    run.Stage = "CoverLetter"; await db.SaveChangesAsync(ct);
                    if (clarifications.Length > 0 && definition.LetterAgentId == null)
                        throw new HumanReviewRequiredException("Для доработки письма с уточнением назначьте агента сопроводительного письма в workflow.");
                    var letter = $"Здравствуйте! Рассматриваю вакансию «{vacancy.Title}».\n\nПодтверждённые сведения из моего профиля:\n" +
                        string.Join("\n", facts) + "\n\nБуду рад обсудить требования и условия.\n" + profile.Name;
                    if (definition.LetterAgentId is Guid writer)
                    {
                        letter = await new CoverLetterService(db, agents).PrepareAsync(writer, candidate, vacancy, evaluation, vacancyWorkflow, ct);
                    }
                    if (string.IsNullOrWhiteSpace(letter) || letter.Length > 30000) throw new InvalidOperationException("Cover letter is empty or too long.");
                    Trace("WorkflowCoverLetterPrepared", new { vacancyId = vacancy.Id, applicationId = application.Id, characters = letter.Length });
                    run.Stage = "Preparation"; await db.SaveChangesAsync(ct);
                    await new ResumeAgentService(db, agents).PrepareAsync(vacancyWorkflow, candidate, vacancy, application, ct);
                    application.CoverLetter = letter; Move(application, ApplicationState.ReadyForReview); run.Prepared++;
                    completedApplications++;
                    Trace("WorkflowApplicationPrepared", new { vacancyId = vacancy.Id, applicationId = application.Id, application.ResumeVersionId, vacancy.Url, requiresReview = evaluation.Decision != "Proceed" || !definition.AutoSubmit });
                    if (evaluation.Decision != "Proceed" || !definition.AutoSubmit)
                        db.Add(new Notification { CandidateProfileId = profile.Id, Kind = "Approval", Title = "Отклик ожидает проверки", Message = vacancy.Title + " — " + vacancy.Employer,
                            Url = vacancy.Url, RelatedId = application.Id });
                    await db.SaveChangesAsync(ct);
                    if (definition.ExecutionMode == "browser" && !outsideAgeLimit && !(preparationOnly && clarifications.Length > 0) && evaluation.Decision == "Proceed" &&
                        await PrepareExternalAsync(application, vacancy, candidate, vacancyWorkflow)) continue;
                    if (definition.ExecutionMode == "browser" && definition.AutoSubmit && evaluation.Decision == "Proceed")
                    {
                        Move(application, ApplicationState.ReadyToSubmit); await db.SaveChangesAsync(ct);
                        await SubmitAsync(application, vacancy, candidate, vacancyWorkflow);
                    }
                }
                catch (ModelProviderException)
                {
                    // Keep the vacancy pending; a provider outage is not a failure of this application.
                    // Never roll back submission states with an uncertain external side effect.
                    if (application.State is ApplicationState.Evaluating or ApplicationState.Drafting)
                        application.State = stateBeforeProcessing;
                    await db.SaveChangesAsync(CancellationToken.None);
                    throw;
                }
                catch (VacancyUnavailableException ex) when (!ct.IsCancellationRequested)
                {
                    var skippedState = ex.Code == "closed" ? ApplicationState.Closed : ApplicationState.Skipped;
                    if (Workflow.CanTransition(application.State, skippedState)) Move(application, skippedState);
                    else if (approvedOnly && application.State == ApplicationState.ReadyToSubmit)
                    {
                        Move(application, ApplicationState.Blocked);
                        db.Audit.Add(new AuditEvent { EntityId = application.Id, EntityType = nameof(JobApplication),
                            Action = "BrowserApplicationBlocked", Details = "Отправки не было. " + ex.Message });
                    }
                    if (ex.Code == "closed") vacancy.Availability = "Closed";
                    run.Skipped++;
                    Trace("WorkflowVacancySkipped", new { vacancyId = vacancy.Id, applicationId = application.Id, vacancy.Title, vacancy.Url, code = ex.Code, reason = ex.Message });
                    db.Add(new Notification { CandidateProfileId = profile.Id, Kind = "Information", Title = "Вакансия пропущена",
                        Message = vacancy.Title + ": " + ex.Message, Url = vacancy.Url, RelatedId = application.Id });
                    await db.SaveChangesAsync(ct);
                }
                catch (Exception ex)
                {
                    if (application.State is ApplicationState.Evaluating or ApplicationState.Drafting or ApplicationState.ReadyToSubmit) Move(application, ApplicationState.Blocked);
                    await db.SaveChangesAsync(CancellationToken.None);
                    if (definition.ExecutionMode != "browser" || ct.IsCancellationRequested) throw;
                    run.NeedsAttention++;
                    var needsReview = ex is HumanReviewRequiredException;
                    if (!needsReview) { run.Errors++; technicalDetails.Add(vacancy.Title + ": " + ex.Message); }
                    failureDetails.Add(vacancy.Title + ": " + ex.Message);
                    Trace("WorkflowApplicationBlocked", new { vacancyId = vacancy.Id, applicationId = application.Id, vacancy.Url, kind = needsReview ? "Review" : "Error", reason = ex.Message });
                    db.Audit.Add(new AuditEvent { EntityId = application.Id, EntityType = nameof(JobApplication), Action = "BrowserApplicationBlocked", Details = ex.Message[..Math.Min(2000, ex.Message.Length)] });
                    var attention = await db.Set<Notification>().FirstOrDefaultAsync(x => x.RelatedId == application.Id && x.Kind == "Attention" && x.ReadAt == null, ct);
                    if (attention == null)
                        db.Add(new Notification { CandidateProfileId = profile.Id, Kind = needsReview ? "Review" : "Error", Title = needsReview ? "Подготовка требует проверки" : "Ошибка подготовки отклика", Message = vacancy.Title + ": " + ex.Message[..Math.Min(1000, ex.Message.Length)], Url = vacancy.Url, RelatedId = application.Id });
                    else { attention.Message = vacancy.Title + ": " + ex.Message[..Math.Min(1000, ex.Message.Length)]; attention.Url = vacancy.Url; }
                    await db.SaveChangesAsync(ct);
                }
            }
            if (monitorWithinSearch && definition.ExecutionMode == "browser" && definition.CheckMail)
            {
                run.Stage = "Mail"; await db.SaveChangesAsync(ct);
                foreach (var source in statusSources.Where(x => !discoveryFailures.Contains(x.Name)))
                {
                    try { await new RemoteIntegrationService(db, new SecretStore(), http, agents).PullAsync(source.Id, ct); }
                    catch (InvalidOperationException) when (!ct.IsCancellationRequested) { discoveryFailures.Add(source.Name); }
                }
            }
            if (discoveryFailures.Count > 0)
            {
                run.Error = "Platform checks failed: " + string.Join(", ", discoveryFailures);
                if (definition.ExecutionMode == "browser" && failureDetails.Count > 0)
                    run.Error += ". " + string.Join(" | ", failureDetails).Substring(0, Math.Min(4000, string.Join(" | ", failureDetails).Length));
                throw new InvalidOperationException(run.Error);
            }
            run.Status = run.NeedsAttention > 0 ? "NeedsAttention" : "Succeeded";
            if (run.NeedsAttention > 0) run.AttentionDetails = string.Join(" | ", failureDetails)[..Math.Min(8000, string.Join(" | ", failureDetails).Length)];
            run.Error = string.Join(" | ", technicalDetails)[..Math.Min(4000, string.Join(" | ", technicalDetails).Length)];
            run.Stage = definition.ExecutionMode == "browser" ? "Completed" : "Review";
            return run;

            async Task<bool> PrepareExternalAsync(JobApplication application, Vacancy vacancy, CandidateProfile candidate, WorkflowDefinition vacancyWorkflow)
            {
                if (!await browser!.PrepareExternalDraftAsync(vacancyWorkflow, platforms[vacancy.JobPlatformId], candidate, vacancy, application, ct)) return false;
                if (application.State == ApplicationState.Submitted)
                {
                    run.Submitted++;
                    await db.SaveChangesAsync(ct);
                    return true;
                }
                // A draft or unsuccessful preflight still requires human review.
                if (application.State == ApplicationState.ReadyToSubmit) Move(application, ApplicationState.ReadyForReview);
                run.NeedsAttention++;
                var report = await ExternalApplicationDraft.ReadAsync(db, application.Id, ct);
                failureDetails.Add(vacancy.Title + ": " + (application.State == ApplicationState.SubmissionUnknown
                    ? "Результат внешней отправки неизвестен. Не отправляйте повторно."
                    : report?.Reason ?? "Внешняя форма ожидает проверки и ручной отправки."));
                await db.SaveChangesAsync(ct);
                return true;
            }
            async Task SubmitAsync(JobApplication application, Vacancy vacancy, CandidateProfile candidate, WorkflowDefinition vacancyWorkflow)
            {
                run.Stage = "Submission"; await db.SaveChangesAsync(ct);
                var attempt = await new BrowserSubmissionService(db, browser!).SubmitAsync(vacancyWorkflow, application, platforms[vacancy.JobPlatformId], candidate, vacancy, ct);
                if (application.State == ApplicationState.Submitted && attempt?.Outcome == "Confirmed") run.Submitted++;
                else
                {
                    run.NeedsAttention++;
                    failureDetails.Add(vacancy.Title + ": " + (attempt?.Outcome == "SubmissionUnknown" ? attempt.Evidence : "Application history blocks a duplicate submission."));
                }
                await db.SaveChangesAsync(ct);
            }
        }
        catch (Exception ex)
        {
            if (ModelProviderException.Find(ex) is { } providerError)
            {
                run.Errors++;
                db.Audit.Add(new AuditEvent { EntityId = run.Id, EntityType = nameof(WorkflowRun), Action = "WorkflowProviderStopped",
                    Details = JsonSerializer.Serialize(new { providerError.Code, providerError.Retryable, providerError.DiagnosticId, reason = providerError.Message }, Json) });
            }
            run.Status = ex is OperationCanceledException ? "Interrupted" : "Failed";
            if (ex is not OperationCanceledException && run.Errors == 0) run.Errors = 1;
            if (string.IsNullOrEmpty(run.Error))
                run.Error = ex is OperationCanceledException ? "Cancelled or timed out." : ex.Message[..Math.Min(4000, ex.Message.Length)];
            throw;
        }
        finally
        {
            try
            {
                if (browserRun != null)
                {
                    await browserRun.DisposeAsync();
                    db.Audit.Add(new AuditEvent { EntityId = run.Id, EntityType = nameof(WorkflowRun), Action = "WorkflowBrowserClosed" });
                }
            }
            catch (Exception closeError)
            {
                run.Errors++;
                if (run.Status == "Succeeded") run.Status = "NeedsAttention";
                run.NeedsAttention++;
                var cleanupMessage = run.Error + " Browser cleanup: " + closeError.Message;
                run.Error = cleanupMessage[..Math.Min(4000, cleanupMessage.Length)];
                db.Audit.Add(new AuditEvent { EntityId = run.Id, EntityType = nameof(WorkflowRun), Action = "WorkflowBrowserCloseFailed", Details = closeError.Message });
            }
            run.FinishedAt = DateTime.UtcNow;
            db.Audit.Add(new AuditEvent { EntityId = run.Id, EntityType = nameof(WorkflowRun), Action = "WorkflowFinished",
                Details = JsonSerializer.Serialize(new { runId = run.Id, workflowId = definitionId, run.Status, run.Stage, run.Discovered, run.Checked, run.Prepared, run.Rejected, run.Submitted, run.Skipped, run.Errors, run.NeedsAttention, run.Error, run.AttentionDetails, run.FinishedAt }, Json) });
            try { await db.SaveChangesAsync(CancellationToken.None); }
            finally
            {
                await db.Set<WorkflowDefinition>().Where(x => x.Id == definitionId && x.LeaseToken == token)
                    .ExecuteUpdateAsync(s => s.SetProperty(x => x.LeaseUntil, (DateTime?)null).SetProperty(x => x.LeaseToken, (string?)null), CancellationToken.None);
            }
        }
    }
    public async Task ValidateAsync(WorkflowDefinition definition, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(definition.Name)) throw new ArgumentException("Workflow name is required.");
        if (definition.ExecutionMode is not ("prepare" or "browser")) throw new ArgumentException("Select prepare or browser workflow mode.");
        if (definition.VacancySource is not ("search" or "saved" or "search-and-saved")) throw new ArgumentException("Select search, saved or search-and-saved vacancy source.");
        if (definition.ApplicationStatusFilter is not ("pending" or "no-application" or "Discovered" or "Evaluating" or "Drafting" or "Blocked" or "Skipped" or WorkflowDefinition.ApprovedStatusFilter))
            throw new ArgumentException("Select an unprocessed application status filter.");
        if (definition.SendsApprovedOnly() && (definition.ExecutionMode != "browser" || definition.VacancySource != "saved" ||
            !definition.AutoSubmit || definition.EvaluationResultFilter != "all" || definition.CheckMail))
            throw new ArgumentException("Для отправки готовых откликов выберите браузерный режим, сохранённые вакансии и автоотправку. Проверка почты и фильтр оценки не нужны.");
        if (definition.SubmitExternalApplications && !definition.SendsApprovedOnly())
            throw new ArgumentException("Внешняя автоотправка доступна только для workflow готовых откликов.");
        if (definition.EvaluationResultFilter is not ("all" or "Unevaluated" or "Proceed" or "NeedsReview" or "Reject"))
            throw new ArgumentException("Select an evaluation result filter.");
        if (definition.VacancySource != "search" && definition.ExecutionMode != "browser")
            throw new ArgumentException("Saved vacancies require browser mode to recheck the original vacancy pages.");
        if (definition.ResumeMode is not ("fixed" or "select" or "tailor")) throw new ArgumentException("Select fixed, select or tailor resume mode.");
        if (definition.FreshnessDays is < 1 or > 365) throw new ArgumentException("Publication age must be 1–365 days.");
        if (definition.ResumeMode != "fixed" && !await db.Agents.AnyAsync(x => x.Id == definition.ResumeAgentId && x.CandidateProfileId == definition.CandidateProfileId && x.Enabled, ct))
            throw new ArgumentException("Select an enabled resume agent belonging to this candidate.");
        if (definition.MaxApplicationsPerRun is < 1 or > 50 || definition.MaxBrowserSteps is < 5 or > 100)
            throw new ArgumentException("Applications per run: 1–50; browser steps per stage: 5–100.");
        if (definition.MaxVacanciesPerPlatform is < 1 or > 200 || definition.MaxVacancyChecksPerRun is < 1 or > 500)
            throw new ArgumentException("Vacancies per platform: 1–200; vacancy checks per run: 1–500.");
        if (definition.ExecutionMode == "browser")
        {
            if (definition.AutoSubmit && definition.FreshnessDays == null) throw new ArgumentException("Automatic browser submission requires a publication-age limit.");
            if (!await db.Set<BrowserProfile>().AnyAsync(x => x.Id == definition.BrowserProfileId, ct))
                throw new ArgumentException("Select the saved browser profile used to log in to the job platforms.");
            if (!await db.Agents.AnyAsync(x => x.Id == definition.BrowserAgentId && x.CandidateProfileId == definition.CandidateProfileId && x.TaskType == "BrowserAutomation", ct))
                throw new ArgumentException("Select a BrowserAutomation agent belonging to this candidate.");
            if (!definition.EvaluationAgentId.HasValue) throw new ArgumentException("Browser workflows require an evaluation agent to decide which vacancies match.");
            if (monitorWithinSearch && definition.CheckMail && !await db.Set<Integration>().AnyAsync(x => x.CandidateProfileId == definition.CandidateProfileId && x.Kind == "gmail" && x.Enabled, ct))
                throw new ArgumentException("Enable a Gmail integration for this candidate or turn off mail monitoring in the workflow.");
        }
        else if (definition.AutoSubmit) throw new ArgumentException("Automatic submission requires browser mode.");
        var rules = JsonSerializer.Deserialize<SearchRules>(definition.RulesJson, Json) ?? throw new ArgumentException("Rules must be an object.");
        if (rules.MinimumSalary < 0 || rules.MaxAgeDays < 1) throw new ArgumentException("Invalid salary or publication age.");
        if (rules.MinimumSalary.HasValue && string.IsNullOrWhiteSpace(rules.Currency)) throw new ArgumentException("Salary currency is required.");
        var platforms = definition.SelectedPlatforms();
        if (platforms.Length is < 1 or > 50 || platforms.Distinct().Count() != platforms.Length)
            throw new ArgumentException("Select 1–50 distinct platforms.");
        if (await db.Platforms.CountAsync(x => platforms.Contains(x.Id), ct) != platforms.Length)
            throw new ArgumentException("A selected platform does not exist.");
        if (definition.ExecutionMode == "browser")
        {
            var urls = await db.Platforms.Where(x => platforms.Contains(x.Id)).Select(x => x.Url).ToListAsync(ct);
            if (urls.Any(value => !Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.UserInfo.Length != 0 ||
                !(uri.Scheme == "https" || uri.Scheme == "http" && uri.IsLoopback)))
                throw new ArgumentException("Browser platforms require HTTPS search URLs (loopback HTTP is allowed for local tests).");
        }
        if (definition.ResumeMode != "fixed" && definition.ResumeVersionId == Guid.Empty)
            definition.ResumeVersionId = await (from v in db.ResumeVersions join r in db.Resumes on v.ResumeId equals r.Id
                where r.CandidateProfileId == definition.CandidateProfileId && v.Origin == "Uploaded" orderby v.CreatedAt descending select v.Id).FirstOrDefaultAsync(ct);
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
    private static bool MatchesQueueFilters(WorkflowDefinition definition, JobApplication? application, VacancyEvaluation? evaluation)
    {
        if (definition.ApplicationStatusFilter != "pending" &&
            definition.ApplicationStatusFilter != (application == null ? "no-application" : application.State.ToString())) return false;
        if (definition.EvaluationResultFilter == "all") return true;
        if (evaluation == null) return definition.EvaluationResultFilter == "Unevaluated";
        try
        {
            using var document = JsonDocument.Parse(evaluation.ResultJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return false;
            return (document.RootElement.TryGetProperty("decision", out var decision) || document.RootElement.TryGetProperty("Decision", out decision)) &&
                decision.ValueKind == JsonValueKind.String && decision.GetString() == definition.EvaluationResultFilter;
        }
        catch (JsonException) { return false; }
    }
    private void Move(JobApplication application, ApplicationState state)
    {
        var previous = application.State; Workflow.Transition(application, state);
        db.Audit.Add(new AuditEvent { EntityId = application.Id, EntityType = nameof(JobApplication), Action = "StateChanged", Details = $"{previous} -> {state}" });
    }
    private async Task ReconcileInterruptedSubmissionsAsync(WorkflowDefinition definition, CancellationToken ct)
    {
        if (definition.ExecutionMode != "browser") return;
        var runOwner = definition.Id.ToString();
        var interrupted = await db.Applications.Where(x => x.State == ApplicationState.Submitting &&
            (db.Audit.Any(e => e.EntityId == x.Id && e.Action == BrowserSubmissionService.SubmissionWorkflow && e.Details == runOwner) ||
             (x.WorkflowDefinitionId == definition.Id &&
              !db.Audit.Any(e => e.EntityId == x.Id && e.Action == BrowserSubmissionService.SubmissionWorkflow)))).ToListAsync(ct);
        foreach (var application in interrupted)
        {
            Move(application, ApplicationState.SubmissionUnknown);
            foreach (var attempt in await db.Set<ApplicationAttempt>().Where(x => x.JobApplicationId == application.Id && x.Outcome == "Pending").ToListAsync(ct))
            { attempt.Outcome = "SubmissionUnknown"; attempt.Evidence = "Previous browser workflow was interrupted. Reconcile on the platform; do not retry automatically."; }
        }
        await db.SaveChangesAsync(ct);
    }
    private static void ValidateOutput(string output, string schema)
    {
        using var document = JsonDocument.Parse(output);
        if (!global::Json.Schema.JsonSchema.FromText(schema).Evaluate(document.RootElement).IsValid) throw new InvalidOperationException("Workflow output schema mismatch.");
    }
}

