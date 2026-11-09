# Windows PowerShell: requires .NET 10 SDK. Output is a self-contained single-file EXE.
$ErrorActionPreference = 'Stop'
$Root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$InstallerProject = Join-Path $Root 'src\WatcherInstaller\WatcherInstaller.csproj'
$InstallerOut = Join-Path $Root 'output\windows-installer'
Write-Host 'Building and embedding the Windows agent, then publishing the installer...'
dotnet publish $InstallerProject -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:DebugType=none -p:IncludeNativeLibrariesForSelfExtract=true -o $InstallerOut
if ($LASTEXITCODE -ne 0) { throw 'Installer build failed' }
Write-Host "Ready: $InstallerOut\WatcherInstaller.exe" -ForegroundColor Green
