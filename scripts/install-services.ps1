#Requires -RunAsAdministrator
param(
    [Parameter(Mandatory)][System.Management.Automation.PSCredential]$Credential,
    [string]$DataDirectory = (Join-Path $env:LOCALAPPDATA 'JobFlow'),
    [string]$CodexDirectory = (Join-Path $env:USERPROFILE '.codex'),
    [string]$CodexPath = (Get-Command codex -ErrorAction Stop).Source
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$published = Join-Path $repo 'data/services/worker'
dotnet publish (Join-Path $repo 'src/JobFlow.Worker') -c Release -r win-x64 --self-contained true -o $published
if ($LASTEXITCODE -ne 0) { throw 'Worker publish failed.' }
$executable = Join-Path $published 'JobFlow.Worker.exe'
$dataPath = [IO.Path]::GetFullPath($DataDirectory)
$codexRoot = [IO.Path]::GetFullPath($CodexDirectory)
foreach ($role in @('search','monitor')) {
    $name = if ($role -eq 'search') { 'JobFlow.Search' } else { 'JobFlow.Monitor' }
    if (Get-Service -Name $name -ErrorAction SilentlyContinue) { throw "Service $name already exists. Stop it and update its existing deployment explicitly." }
    $binary = '"{0}" --ServiceRole {1} --contentRoot "{2}"' -f $executable,$role,$published
    New-Service -Name $name -DisplayName $name -BinaryPathName $binary -Credential $Credential -StartupType Automatic -Description "JobFlow independent $role service"
    $serviceKey = "HKLM:/SYSTEM/CurrentControlSet/Services/$name"
    New-ItemProperty -Path $serviceKey -Name Environment -PropertyType MultiString -Value @("JOBFLOW_DATA_DIR=$dataPath","CODEX_HOME=$codexRoot","JOBFLOW_CODEX_PATH=$CodexPath",'Browser__Headless=true') -Force | Out-Null
    sc.exe failure $name reset= 86400 actions= restart/10000/restart/30000/restart/60000 | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Could not configure recovery for $name" }
}
Write-Output 'Installed JobFlow.Search and JobFlow.Monitor. Run these services under the Windows account authorized in Codex and owning the JobFlow data/DPAPI keys.'
Write-Output 'The account needs Log on as a service. Grant the API account Start/Stop permission for these two services if API control is required without elevation.'
Write-Output 'Stop development copies before starting the installed services; each role has an exclusive process lock.'
