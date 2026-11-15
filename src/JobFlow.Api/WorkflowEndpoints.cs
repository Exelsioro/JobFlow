using JobFlow.Application;
using JobFlow.Domain;
using JobFlow.Infrastructure;
using Microsoft.EntityFrameworkCore;
namespace JobFlow.Api;
public static class WorkflowEndpoints
{
    public static void MapWorkflowEndpoints(this WebApplication app)
    {
        app.MapGet("/api/application-attempts",async(JobFlowDb db)=>await db.Set<ApplicationAttempt>().AsNoTracking().OrderByDescending(x=>x.CreatedAt).ToListAsync());
        app.MapPost("/api/applications/{id:guid}/test-submit",async(Guid id,TestSubmissionInput input,TestSubmissionService service,CancellationToken ct)=>Results.Ok(await service.SubmitAsync(id,input.IdempotencyKey,input.Revision,ct)));
        app.MapPost("/api/applications/{id:guid}/mark-unknown",async(Guid id,TestSubmissionService service,CancellationToken ct)=>{await service.MarkUnknownAsync(id,ct);return Results.Ok(new{status="SubmissionUnknown"});});
        app.MapPost("/api/applications/{id:guid}/record-manual-submission", async (Guid id, ManualSubmissionInput input, JobFlowDb db, CancellationToken ct) =>
        {
            var application = await db.Applications.SingleAsync(x => x.Id == id, ct);
            if (application.Revision != input.Revision) throw new DbUpdateConcurrencyException("Application changed.");
            if (application.IsTest || application.State != ApplicationState.ReadyToSubmit)
                throw new InvalidOperationException("Only approved real-platform drafts can be recorded as manually sent.");
            application.State = ApplicationState.Submitted;
            application.Revision++;
            db.Audit.Add(new AuditEvent { EntityId = id, EntityType = nameof(JobApplication), Action = "ManualSubmissionRecorded" });
            await db.SaveChangesAsync(ct);
            return Results.Ok(application);
        });
        app.MapGet("/api/workflows", async (JobFlowDb db) => await db.Set<WorkflowDefinition>().AsNoTracking().ToListAsync());
        app.MapGet("/api/workflow-runs", async (JobFlowDb db) => await db.Set<WorkflowRun>().AsNoTracking().OrderByDescending(x => x.CreatedAt).Take(200).ToListAsync());
        app.MapGet("/api/agent-templates", () => AgentTemplates.All);
        app.MapPost("/api/workflows", async (WorkflowDefinition input, JobFlowDb db, JobSearchWorkflow workflow, CancellationToken ct) =>
        {
            await workflow.ValidateAsync(input, ct);
            input.JobPlatformId = input.SelectedPlatforms()[0];
            input.Id = Guid.NewGuid(); input.Revision = 1; input.LeaseToken = null; input.LeaseUntil = null;
            db.Add(input);
            db.Add(new ScheduledTask { CandidateProfileId = input.CandidateProfileId, Name = input.Name, Kind = "Workflow",
                WorkflowDefinitionId = input.Id, Enabled = false, IntervalMinutes = 60, NextRunAt = DateTime.UtcNow.AddHours(1) });
            await db.SaveChangesAsync(ct); return Results.Ok(input);
        });
        app.MapPut("/api/workflows/{id:guid}", async (Guid id, WorkflowDefinition input, JobFlowDb db, JobSearchWorkflow workflow, CancellationToken ct) =>
        {
            var row = await db.Set<WorkflowDefinition>().SingleAsync(x => x.Id == id, ct);
            if (row.Revision != input.Revision || row.LeaseUntil > DateTime.UtcNow) return Results.Conflict(new { error = "Workflow changed or is running." });
            if (row.CandidateProfileId != input.CandidateProfileId)
                throw new ArgumentException("Create a new workflow to change its profile.");
            await workflow.ValidateAsync(input, ct);
            input.JobPlatformId = input.SelectedPlatforms()[0];
            input.Id = id; input.CreatedAt = row.CreatedAt; input.Revision++; input.LeaseToken = null; input.LeaseUntil = null;
            db.Entry(row).CurrentValues.SetValues(input); await db.SaveChangesAsync(ct); return Results.Ok(row);
        });
        app.MapPost("/api/workflows/{id:guid}/run", async (Guid id, JobFlowDb db, CancellationToken ct) =>
        {
            var definition = await db.Set<WorkflowDefinition>().SingleAsync(x => x.Id == id, ct);
            if (!definition.Enabled) throw new ArgumentException("Enable the workflow first.");
            var task = await db.Tasks.SingleAsync(x => x.WorkflowDefinitionId == id, ct);
            if (task.LeaseUntil > DateTime.UtcNow) return Results.Conflict(new { error = "Workflow is already running." });
            task.RunRequested = true; task.NextRunAt=DateTime.UtcNow; task.FailureCount=0; await db.SaveChangesAsync(ct); return Results.Ok(new { queued = true, taskId = task.Id });
        });
        app.MapPut("/api/applications/{id:guid}/draft", async (Guid id, DraftInput input, IJobSearchWorkflow workflow, CancellationToken ct) =>
        { await workflow.SaveDraftAsync(id, input.Letter, input.Revision, ct); return Results.Ok(new { saved = true }); });
    }
    public record TestSubmissionInput(string IdempotencyKey,int Revision);
    public record ManualSubmissionInput(int Revision);
    public record DraftInput(string Letter, int Revision);
}

