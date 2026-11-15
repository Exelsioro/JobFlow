# Architecture and boundaries

## Current implementation

Domain contains entities, ownership IDs and application states. Application contains the transition policy, conservative evaluator and integration contracts. Infrastructure contains EF Core, migrations, CLI/browser adapters and the persistent task processor. API exposes local HTTP operations. Angular uses the same-origin API. Worker is a separate executable.

SQLite is authoritative. CSV export is an optional projection. No external account or seeded candidate is required. User is created lazily when the first candidate profile is saved, without personal seed data.

Persistence uses separate tables for all named concepts, including future integration/account/feedback concepts. **A table or interface is not an implemented feature.** See PROGRESS.md.

## Invariants

- Unique platform + external vacancy ID.
- Unique candidate profile + vacancy application.
- Unique resume + version.
- Unique application attempt idempotency key and employer source event key (future adapters must use them).
- A new application references a resume version owned by the same candidate profile.
- Workflow transitions use an explicit allowed-edge table. Unknown submission cannot be retried.
- Application Revision and AgentProfile Version are optimistic concurrency tokens.
- Manual state changes cannot assert Submitting, Submitted, SubmissionUnknown or EmployerResponded.
- ReadyToSubmit requires a cover letter and no unconfirmed external-journal import.
- No executable adapter sends real applications.

## Scheduler

Worker polls SQLite. A conditional SQL update acquires a two-minute lease with a unique token. Normal jobs have a 45-second deadline. Completion and lease release commit together. Expired leases are reclaimed; prior Running history becomes Interrupted. Missed periodic intervals coalesce into one run rather than a burst.

Manual RunRequested is separate from Enabled. Running a paused task does not enable its repeating schedule. Pause removes queued requests and disables future periodic starts; it does not cancel an already executing task. Per-job stop, configurable retries, cron, lease renewal and arbitrary long-running workflows remain unimplemented.

Only HealthCheck and BrowserDryRun are dispatched. No background search/AI/email workflow is implied.

## AI

Codex uses ProcessStartInfo.ArgumentList and stdin instead of shell interpolation. Exit success is necessary but insufficient: output file must exist, parse as JSON and pass the configured schema. CLI timeout kills the process tree. Failed executions retain a sanitized diagnostic and configuration snapshot. Outputs are returned to the caller without persistence by default.

Model IDs are user supplied. Other provider kinds are rejected instead of pretending to work. Provider enablement is explicit authorization for external model calls, not permission to submit applications.

## Local security

API binds 127.0.0.1:5271, validates Host and requires a custom header on mutating API calls. There is no permissive CORS policy. This guards against ordinary cross-site form requests; it is not authentication against local processes.

Do not expose this build to a LAN/reverse proxy. Authentication and transport security must be added before remote access. Data/cookies are protected by the OS user account, not encrypted by JobFlow. No generalized secret store or OAuth vault is implemented. Browser profiles are dedicated directories; Chrome profiles are never copied.

## Known architecture debt

HTTP handlers still contain some orchestration and persistence details; move them into Application use cases before adding an MCP server or additional transport. There is no complete adapter registry, transactional outbox, external journal conflict engine or workflow orchestration engine. Application ownership currently assumes one local owner. PostgreSQL migration needs a provider-specific migration assembly and concurrency verification; Domain/Application have no SQLite dependencies.
