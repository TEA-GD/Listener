$ErrorActionPreference = 'Stop'
$Root = $PSScriptRoot
Write-Host 'Building Watcher Collector (plain TCP, no TLS)...' -ForegroundColor Cyan
& (Join-Path $Root 'Collector\build\build-windows.ps1')
if ($LASTEXITCODE -ne 0) { throw 'Collector build failed.' }
Write-Host 'Building Watcher Agent (plain TCP, no TLS)...' -ForegroundColor Cyan
& (Join-Path $Root 'Agent\build\build-windows.ps1')
if ($LASTEXITCODE -ne 0) { throw 'Agent build failed.' }

foreach ($project in @('Listener_Analyzer','Listener_DB_GUI','Listener_MailReceiver','Listener_MailReceiver_Metadata')) {
    Write-Host "Building $project..." -ForegroundColor Cyan
    & (Join-Path $Root "$project\build-windows.ps1")
    if ($LASTEXITCODE -ne 0) { throw "$project build failed." }
}
foreach ($addon in @('Thunderbird_Addon','Thunderbird_Addon_Metadata')) {
    & python (Join-Path $Root "$addon\build.py")
    if ($LASTEXITCODE -ne 0) { throw "$addon packaging failed. Install Python 3 and retry." }
}
Write-Host 'Done. Collector/Agent installers, Analyzer, dashboard, both receivers and both Thunderbird packages are built.' -ForegroundColor Green
