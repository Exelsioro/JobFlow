$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8
$runtime = 'D:/JobFlowRuntime'
$dotnetRoot = Join-Path $runtime 'dotnet'
$nodeRoot = Join-Path $runtime 'node'
$archives = Join-Path $runtime 'downloads'
$sdkArchive = Join-Path $archives 'dotnet-sdk-10.0.404-win-x64.zip'
$nodeArchive = Join-Path $archives 'node-v24.22.1-win-x64.zip'
foreach ($archive in @($sdkArchive, $nodeArchive)) { if (!(Test-Path -LiteralPath $archive)) { throw "Verified archive not found: $archive" } }
$runtimePrefix = [IO.Path]::GetFullPath($runtime).TrimEnd('\') + '\'
$active = @(Get-CimInstance Win32_Process -Filter "name='dotnet.exe'" | Where-Object { $_.ExecutablePath -and $_.ExecutablePath.StartsWith($runtimePrefix, [StringComparison]::OrdinalIgnoreCase) })
if ($active.Count) { throw 'Stop JobFlow before changing its runtime.' }
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
New-Item -ItemType Directory -Path $dotnetRoot -Force | Out-Null
& tar.exe -xf $sdkArchive -C $dotnetRoot
if ($LASTEXITCODE -ne 0) { throw '.NET extraction failed.' }
& tar.exe -xf $nodeArchive -C $runtime
if ($LASTEXITCODE -ne 0) { throw 'Node extraction failed.' }
$unpackedNode = [IO.Path]::GetFullPath((Join-Path $runtime 'node-v24.22.1-win-x64'))
$prefix = [IO.Path]::GetFullPath($runtime).TrimEnd('\') + '\'
$targetNode = [IO.Path]::GetFullPath($nodeRoot)
if (!$unpackedNode.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase) -or !$targetNode.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'Node installation escaped the runtime directory.' }
if (Test-Path -LiteralPath $nodeRoot) { throw 'Node target already exists; review it before replacing.' }
Move-Item -LiteralPath $unpackedNode -Destination $nodeRoot
$entries = @($dotnetRoot, $nodeRoot, 'C:/Users/Exelsior/AppData/Local/Programs/Git/cmd')
$userPath = [Environment]::GetEnvironmentVariable('Path', 'User')
$savedPath = @($userPath -split ';' | Where-Object { $_ })
foreach ($entry in $entries) { if ($savedPath -notcontains $entry) { $savedPath += $entry } }
[Environment]::SetEnvironmentVariable('Path', ($savedPath -join ';'), 'User')
foreach ($entry in $entries) { if (($env:Path -split ';') -notcontains $entry) { $env:Path = $entry + ';' + $env:Path } }
foreach ($name in @('DOTNET_ROOT','NUGET_PACKAGES','JOBFLOW_DATA_DIR','PLAYWRIGHT_BROWSERS_PATH')) {
    $value = [Environment]::GetEnvironmentVariable($name, 'User')
    if ($value) { [Environment]::SetEnvironmentVariable($name, $value, 'Process') }
}
[Environment]::SetEnvironmentVariable('NPM_CONFIG_CACHE', "$runtime/npm-cache", 'User')
$env:NPM_CONFIG_CACHE = "$runtime/npm-cache"
& (Join-Path $dotnetRoot 'dotnet.exe') --info
if ($LASTEXITCODE -ne 0) { throw '.NET does not run on this Windows installation.' }
& (Join-Path $nodeRoot 'node.exe') --version
if ($LASTEXITCODE -ne 0) { throw 'Node does not run on this Windows installation.' }
& (Join-Path $nodeRoot 'npm.cmd') install --global npm@12.1.0 --prefix $nodeRoot --no-fund --no-audit
if ($LASTEXITCODE -ne 0) { throw 'npm setup failed.' }
& (Join-Path $nodeRoot 'npm.cmd') --version
if ($LASTEXITCODE -ne 0) { throw 'npm is unavailable.' }
Write-Output '.NET, Node, npm and user PATH are ready on the current Windows installation.'
