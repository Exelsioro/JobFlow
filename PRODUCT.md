# JobFlow

## Platform
web

## Stack
User-specified ASP.NET Core, Angular, EF Core SQLite, .NET Worker, Playwright .NET, Codex CLI.

## Purpose

Search and monitoring are independent background services with separate schedules and agents. Resume selection and adaptation use Codex CLI with operation-specific instructions and the supplied factual dossier. Generated PDFs are preserved separately with source/vacancy/agent provenance. A basic notification center reports completed checks, approvals and employer replies. Calendar schedules use the configured local time zone.
Universal local job-search workspace with multiple candidate profiles. A saved browser profile holds the user's platform logins; a browser agent searches, evaluates vacancies, drafts and submits matching applications, and tracks replies through Gmail. Manual browser login/control is available through the web UI across the private LAN. All personal data is user supplied. Existing preparation workflows keep manual review; browser auto-submission is explicitly configured per workflow. Empty installation, localhost and private LAN access without authentication, external services optional.
