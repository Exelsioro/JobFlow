param([string]$DataDirectory = '')
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
foreach ($settingName in @('JOBFLOW_DATA_DIR','DOTNET_ROOT','NUGET_PACKAGES','NPM_CONFIG_CACHE','PLAYWRIGHT_BROWSERS_PATH','JOBFLOW_CODEX_PATH','JOBFLOW_GOOGLE_CLIENT_ID','JOBFLOW_GOOGLE_CLIENT_SECRET')) {
    if ([string]::IsNullOrWhiteSpace([Environment]::GetEnvironmentVariable($settingName, 'Process'))) {
        $savedValue = [Environment]::GetEnvironmentVariable($settingName, 'User')
        if (-not [string]::IsNullOrWhiteSpace($savedValue)) { [Environment]::SetEnvironmentVariable($settingName, $savedValue, 'Process') }
    }
}
$userPath = [Environment]::GetEnvironmentVariable('Path', 'User')
foreach ($entry in ($userPath -split ';' | Where-Object { $_ })) {
    if (($env:Path -split ';') -notcontains $entry) { $env:Path += ';' + $entry }
}
if ([string]::IsNullOrWhiteSpace($DataDirectory)) {
    $DataDirectory = if ($env:JOBFLOW_DATA_DIR) { $env:JOBFLOW_DATA_DIR } else { Join-Path $env:LOCALAPPDATA 'JobFlow' }
}
$env:JOBFLOW_DATA_DIR = [IO.Path]::GetFullPath($DataDirectory)
$env:Services__UseWorkerBrowser = 'true'
$dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
if (!$dotnet -and $env:DOTNET_ROOT -and (Test-Path -LiteralPath (Join-Path $env:DOTNET_ROOT 'dotnet.exe'))) {
    $dotnet = Get-Item -LiteralPath (Join-Path $env:DOTNET_ROOT 'dotnet.exe')
}
if (!$dotnet) { throw 'Install .NET 10 SDK or set DOTNET_ROOT before starting JobFlow.' }
$dotnetPath = if ($dotnet.Source) { $dotnet.Source } else { $dotnet.FullName }
$runtimes = & $dotnetPath --list-runtimes
if ($LASTEXITCODE -ne 0 -or !($runtimes -match '^Microsoft.NETCore.App 10\.') -or !($runtimes -match '^Microsoft.AspNetCore.App 10\.')) {
    throw '.NET 10 and ASP.NET Core 10 runtimes are required.'
}
foreach ($component in @('Api', 'Worker')) {
    if (!(Test-Path -LiteralPath (Join-Path $repo "src/JobFlow.$component/bin/Debug/net10.0/JobFlow.$component.dll"))) { throw "Build JobFlow.$component before starting it." }
}
$logDirectory = Join-Path $env:JOBFLOW_DATA_DIR 'logs'
New-Item -ItemType Directory -Path $logDirectory -Force | Out-Null
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
foreach ($role in @('search','monitor')) {
    $port = if ($role -eq 'search') { 5272 } else { 5273 }
    if (-not (Get-NetTCPConnection -LocalPort $port -State Listen -ErrorAction SilentlyContinue)) {
        $workerDirectory = Join-Path $repo 'src/JobFlow.Worker'
        Start-Process -FilePath $dotnetPath -ArgumentList @('bin/Debug/net10.0/JobFlow.Worker.dll','--ServiceRole',$role) -WorkingDirectory $workerDirectory -WindowStyle Hidden -RedirectStandardOutput (Join-Path $logDirectory "$role-$stamp.log") -RedirectStandardError (Join-Path $logDirectory "$role-$stamp.error.log") | Out-Null
    }
}
if (-not (Get-NetTCPConnection -LocalPort 5271 -State Listen -ErrorAction SilentlyContinue)) {
    Start-Process -FilePath $dotnetPath -ArgumentList @('bin/Debug/net10.0/JobFlow.Api.dll') -WorkingDirectory (Join-Path $repo 'src/JobFlow.Api') -WindowStyle Hidden -RedirectStandardOutput (Join-Path $logDirectory "api-$stamp.log") -RedirectStandardError (Join-Path $logDirectory "api-$stamp.error.log") | Out-Null
}
$deadline = [DateTime]::UtcNow.AddSeconds(120)
Add-Type -AssemblyName System.Net.Http
$handler = New-Object Net.Http.HttpClientHandler
$handler.UseProxy = $false
$healthClient = New-Object Net.Http.HttpClient($handler)
$healthClient.Timeout = [TimeSpan]::FromSeconds(3)
$healthClient.DefaultRequestHeaders.Add('X-JobFlow-Client', 'local-ui')
try {
    do {
        try {
            $status = $healthClient.GetStringAsync('http://127.0.0.1:5271/api/status').GetAwaiter().GetResult() | ConvertFrom-Json
            $search = $healthClient.GetStringAsync('http://127.0.0.1:5272/health').GetAwaiter().GetResult() | ConvertFrom-Json
            $monitor = $healthClient.GetStringAsync('http://127.0.0.1:5273/health').GetAwaiter().GetResult() | ConvertFrom-Json
            if ($null -ne $status.profiles -and $search.role -eq 'search' -and $monitor.role -eq 'monitor') {
                Write-Output 'JobFlow API, Search and Monitor are ready. Open http://127.0.0.1:5271 or the server LAN address.'
                return
            }
        } catch { }
        Start-Sleep -Seconds 2
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "JobFlow did not become ready. Check logs in $logDirectory."
} finally { $healthClient.Dispose() }
