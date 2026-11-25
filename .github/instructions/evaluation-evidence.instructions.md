---
description: Candidate skill evidence and durable vacancy evaluation validation
applyTo: '**/{EvaluationEvidence,JobSearchWorkflow,Workflow}*.cs'
---

# Evaluation evidence

- An AI match is a skill name, not a free-form experience claim. Supply candidateSkills at every evaluation call, including stored agents, and normalize only controlled equivalent names (SQL Server / MS SQL Server); never infer versions (.NET 8 / .NET 10), proficiency or years. A mention in Dossier, especially a negative or uncertain mention, is not automatic proof of experience.
- Persist the evaluation, original agent decision/matches, execution ID and per-skill validation before blocking an application. Otherwise the exact rejected claim disappears, because AgentExecutor does not retain general output. Expose the concrete issue through the existing rationale/clarifications without storing the full prompt or duplicating the dossier.
- Unsupported skills block preparation/submission. Proceed combined with missing skills or unverified requirements must require review. Test durable failure and corrected retry, accepted aliases, rejected versions/qualifications, and zero submission attempts for unsupported evaluation.
- When an application's blocking reason changes, deduplicating an unread Attention notification by RelatedId/Kind alone can leave an obsolete cause visible. Keep the current reason synchronized with the latest workflow failure and test repeated failures with different causes.
