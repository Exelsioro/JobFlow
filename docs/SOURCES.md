# Version and integration sources

Checked 2026-11-15; stable installed versions are pinned in project files and package-lock.json.

- .NET 10 LTS and support policy: https://dotnet.microsoft.com/en-us/platform/support/policy
- Angular 22 / Node 26 compatibility: https://angular.dev/reference/versions
- Codex non-interactive mode and structured output: https://developers.openai.com/codex/noninteractive/
- Installed CLI `codex exec --help` verified flags: exec, --output-schema, --output-last-message, --ephemeral, --ignore-user-config, --sandbox read-only, --model, stdin prompt.
- Installed CLI `codex features list` verified disabled tool feature names used by provider.
- Playwright .NET browser installation: https://playwright.dev/dotnet/docs/browsers
- JSON Schema validation uses JsonSchema.Net 9.7.0 and its installed API XML documentation.

No model IDs are hard-coded. The user's CLI/model availability determines the model.
