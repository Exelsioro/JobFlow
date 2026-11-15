$ErrorActionPreference='Stop'
Set-Location (Split-Path $PSScriptRoot -Parent)
dotnet restore
if ($LASTEXITCODE) { throw 'dotnet restore failed' }
dotnet build --no-restore
if ($LASTEXITCODE) { throw 'dotnet build failed' }
Push-Location src/JobFlow.Web
try {
    npm.cmd ci
    if ($LASTEXITCODE) { throw 'npm ci failed' }
    npm.cmd run build
    if ($LASTEXITCODE) { throw 'Angular build failed' }
} finally { Pop-Location }
New-Item -ItemType Directory src/JobFlow.Api/wwwroot -Force | Out-Null
Copy-Item src/JobFlow.Web/dist/jobflow-web/browser/* src/JobFlow.Api/wwwroot -Recurse -Force
Write-Host 'Built. Run API and Worker in separate terminals; see README.md.'
