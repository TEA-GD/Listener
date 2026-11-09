param([ValidateSet('Release','Debug')][string]$Configuration = 'Release')
$ErrorActionPreference='Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$collector = Join-Path $root 'src\WatcherCollector\WatcherCollector.csproj'
$installer = Join-Path $root 'src\WatcherCollectorInstaller\WatcherCollectorInstaller.csproj'
$payload = Join-Path $root 'src\WatcherCollectorInstaller\payload'
$output = Join-Path $root 'output\windows-installer'
foreach ($path in @($payload, $output)) { New-Item -Path $path -ItemType Directory -Force | Out-Null }
Write-Host 'Publishing self-contained WatcherCollector.exe ...' -ForegroundColor Cyan
& dotnet publish $collector -c $Configuration -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none -o $payload
if ($LASTEXITCODE -ne 0) { throw 'Collector publish failed' }
if (-not (Test-Path (Join-Path $payload 'WatcherCollector.exe'))) { throw 'Collector EXE not generated' }
Write-Host 'Publishing GUI installer with embedded collector ...' -ForegroundColor Cyan
& dotnet publish $installer -c $Configuration -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none -o $output
if ($LASTEXITCODE -ne 0) { throw 'Installer publish failed' }
if (-not (Test-Path (Join-Path $output 'WatcherCollectorInstaller.exe'))) { throw 'Installer EXE not generated' }
Write-Host "COMPLETE: $output\WatcherCollectorInstaller.exe" -ForegroundColor Green
