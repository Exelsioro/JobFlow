$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8
$recovery = 'D:/JobFlowRecovery/20261120'
$data = 'D:/JobFlowData'
$runtime = 'D:/JobFlowRuntime'
$oldData = 'C:/Windows.old/Users/Exelsior/AppData/Local/JobFlow'
$oldBrowsers = 'C:/Windows.old/Users/Exelsior/AppData/Local/ms-playwright'
if (Test-Path -LiteralPath "$data/jobflow.db") { throw 'The target already contains a database. Do not rerun this one-time recovery over working data.' }
foreach ($directory in @($recovery, $data, $runtime)) { New-Item -ItemType Directory -Path $directory -Force | Out-Null }
$sid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
foreach ($privateDirectory in @($recovery, $data)) {
    & icacls.exe $privateDirectory /inheritance:r /grant:r "*${sid}:(OI)(CI)F" '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Could not protect the data directory.' }
}
function Copy-Tree([string]$Source, [string]$Destination, [string]$LogName) {
    & robocopy.exe $Source $Destination /E /COPY:DAT /DCOPY:DAT /XJ /R:1 /W:1 /NP "/LOG:$recovery/$LogName" | Out-Null
    if ($LASTEXITCODE -ge 8) { throw "Copy failed: $LogName" }
}
Copy-Tree $oldData "$recovery/original-data" 'backup.log'
Write-Output 'Original data saved outside Windows.old.'
Copy-Tree "$recovery/original-data" $data 'restore.log'
Copy-Tree $oldBrowsers "$runtime/ms-playwright" 'browsers.log'
$oldNuget = 'C:/Windows.old/Users/Exelsior/.nuget/packages'
if (Test-Path -LiteralPath $oldNuget) { Copy-Tree $oldNuget "$runtime/nuget-packages" 'nuget.log' }
$oldOAuth = 'C:/Windows.old/Users/Exelsior/Downloads/Telegram Desktop/client_secret_324827790978_itsauu5dnealqnj1us2fff35h8m4qcbh_apps.json'
Copy-Item -LiteralPath $oldOAuth -Destination "$recovery/google-client.json" -Force
# Preserve old encrypted material, then let the new Windows account create fresh keys and sessions.
foreach ($name in @('secrets', 'browser-profiles')) {
    $source = [IO.Path]::GetFullPath((Join-Path $data $name))
    $target = [IO.Path]::GetFullPath((Join-Path $data "backups/pre-reinstall-$name"))
    $prefix = [IO.Path]::GetFullPath($data).TrimEnd('\') + '\'
    if (!$source.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase) -or !$target.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'Archive target escaped the data directory.' }
    if ((Test-Path -LiteralPath $source) -and !(Test-Path -LiteralPath $target)) {
        Move-Item -LiteralPath $source -Destination $target
    }
}
foreach ($name in @('secrets', 'browser-profiles')) { New-Item -ItemType Directory -Path (Join-Path $data $name) -Force | Out-Null }
$settings = @{
    JOBFLOW_DATA_DIR = $data
    PLAYWRIGHT_BROWSERS_PATH = "$runtime/ms-playwright"
    DOTNET_ROOT = "$runtime/dotnet"
    NUGET_PACKAGES = "$runtime/nuget-packages"
}
$client = Get-Content -LiteralPath "$recovery/google-client.json" -Raw -Encoding UTF8 | ConvertFrom-Json
$oauth = if ($client.installed) { $client.installed } else { $client.web }
if (!$oauth.client_id) { throw 'Google client ID is absent.' }
$settings.JOBFLOW_GOOGLE_CLIENT_ID = $oauth.client_id
if ($oauth.client_secret) { $settings.JOBFLOW_GOOGLE_CLIENT_SECRET = $oauth.client_secret }
foreach ($name in $settings.Keys) {
    [Environment]::SetEnvironmentVariable($name, $settings[$name], 'User')
    [Environment]::SetEnvironmentVariable($name, $settings[$name], 'Process')
}
$git = 'C:/Users/Exelsior/AppData/Local/Programs/Git/cmd/git.exe'
$trusted = @(& $git config --global --get-all safe.directory)
if ($trusted -notcontains 'D:/Projects/JobFlow') { & $git config --global --add safe.directory D:/Projects/JobFlow }
if ($LASTEXITCODE -ne 0) { throw 'Could not configure trust for this repository.' }
Write-Output 'Data, browser binaries, NuGet cache, Google client settings and Git trust restored. Windows was not changed.'
