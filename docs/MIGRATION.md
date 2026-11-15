# Moving JobFlow to another machine

## Layout

Keep source and build output separate from writable runtime data. Set JOBFLOW_DATA_DIR identically for API and Worker. Keep backup storage outside that directory. Configuration uses appsettings plus environment overrides; no credentials belong in source.

## Backup

1. Stop API and Worker cleanly; close all Playwright sessions.
2. Copy the entire data directory including SQLite sidecar files if present, resumes and browser-profile folders.
3. Restrict backup permissions to the owner and use encrypted storage if backups leave the machine.
4. Record the Git commit and runtime versions alongside the backup.
5. Test restoration against a separate data directory before replacing the active installation.

Online SQLite backup automation is not implemented. Do not copy only an active database file while ignoring WAL state.

## Restore

1. Install .NET 10, a supported Node.js version and Playwright OS dependencies.
2. Build the same commit with scripts/build.ps1 or scripts/build.sh.
3. Restore the data directory and set JOBFLOW_DATA_DIR for both processes.
4. Start API; migrations apply automatically. Back up before upgrading to a newer commit.
5. Start Worker and inspect interrupted runs.
6. Reauthenticate Codex CLI and websites manually. Browser cookies may not be portable across operating systems; do not assume Windows cookies can be decrypted on Linux.
7. Confirm real submission is still disabled and test a HealthCheck and isolated browser form.

Headless fixtures support Linux in source. Manual browser login requires a desktop/display on the server. Linux and service-manager deployments have not yet been exercised. Add systemd/Windows Service packaging in a later iteration; the business scheduler remains inside JobFlow.

## PostgreSQL

Add the Npgsql EF provider in Infrastructure and provider-specific migrations. Review index lengths, datetime mapping and lease/concurrency behavior against PostgreSQL. Copy data using an explicit migration tool with validation. Do not reuse SQLite migration scripts blindly.
