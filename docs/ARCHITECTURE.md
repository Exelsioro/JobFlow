# Architecture and boundaries

Domain holds entities, ownership IDs and states. Application holds contracts, rules, templates and transition policy. Infrastructure implements EF SQLite, durable workflow, CLI/browser/MCP, imports and task processing. API serves localhost operations and Angular. Worker runs separately against the same JOBFLOW_DATA_DIR.

## Workflow and consistency

WorkflowDefinition binds a profile, a selection of platforms, a resume version, rules and optional evaluator/writer. A conditional lease prevents overlapping runs. Discovery uses a synthetic Playwright source, imported vacancies or read-only HeadHunter and Greenhouse APIs. Up to ten applications per run pass through persisted evaluation and drafting stages. Existing reviewed/terminal applications and imported historical submissions are skipped.

Unique keys protect platform/external vacancy ID, profile/vacancy application, resume/version, attempt idempotency and source feedback identity. Application revision, workflow revision and agent version prevent stale changes. AI output is schema-validated; claimed matching skills and writer facts must come from supplied evidence. Review remains mandatory.

TestSubmissionService commits Pending and Submitting before invoking the isolated provider. Confirmation produces TestConfirmed/Submitted with IsTest=true; failure produces SubmissionUnknown. The same key returns the existing attempt, never repeats the side effect. A crash can leave Pending; explicit mark-unknown records uncertainty. Real platform submission is disabled.

## Worker

TaskProcessor claims a 20-minute lease atomically. Configured timeout is 5–900 seconds, below the lease duration; no lease renewal is needed for supported bounded runs. A separate context polls cancellation every 300 ms. Completion and release commit together. Failed jobs retry with 1/2/4/8/16-minute backoff up to configured MaxRetries. Expired Running jobs become Interrupted and are reclaimed. Workflow application state permits safe resumption.

Dispatch: HealthCheck, BrowserDryRun, Workflow, Integration. Pause disables future scheduling; Stop also requests cancellation. Periodic intervals coalesce. Timezone is validated metadata; schedule arithmetic is UTC intervals, not cron. Worker process heartbeat/service packaging is not implemented.

## AI and MCP

Codex CLI uses ArgumentList and stdin, output schema and process-tree timeout. Model IDs are user supplied. The host performs MCP calls before AI according to static tool bindings. Official SDK handles stdio/HTTP. Connections must be enabled; calls require explicit name allowlist and readOnlyHint. Tool output is untrusted input, capped in size. No MCP server or model-selected write tool execution is implemented.

## Imports and feedback

JournalService stages CSV rows, validates mapping and confirms them transactionally. Vacancy conflicts follow keep-local/update-vacancies. HistoricalApplication stores source status separately from locally executed attempts and blocks resubmission. Unconfirmed journal imports block approval/submission.

RemoteIntegrationService uses fixed Google API hosts, no redirect following, 90-second timeout and bounded responses. Sheets becomes a staged import. A separate export path previews a bounded snapshot and compares the local and remote values before a RAW update to a dedicated tab. Google OAuth uses a loopback callback with PKCE and stores refresh tokens in the protected secret store. Gmail reads metadata/snippet, JSON feedback uses source event IDs. Feedback is explicitly linked to an application of the same profile; this records EmployerInteraction and EmployerResponded. No outbound mail or calendar writes occur.

## Local security and storage

API binds 127.0.0.1:5271, validates Host and requires X-JobFlow-Client for mutations without permissive CORS. This is not authentication against local processes; remote hosting requires authentication/TLS.

Secrets are separate encrypted files via Data Protection; Windows keys use DPAPI. Unix keys rely on filesystem permissions. Database, CVs and browser profiles rely on OS permissions; full data encryption and retention policies are not implemented. Configuration snapshots may contain user-authored prompt text, so never put tokens in prompts. Only trusted MCP executables should be configured.

Migrations apply on startup. Back up the stopped data directory before upgrading: SQLite table rebuilds may not be fully transactional. Google live credentials, Linux deployment, PostgreSQL and cross-machine restore remain unverified.
