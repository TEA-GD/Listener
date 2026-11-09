param(
    [string]$DatabasePath = (Join-Path $env:ProgramData 'WatcherCollector\watcher-events.db'),
    [ValidateRange(1,65535)][int]$Port = 5080
)
$ErrorActionPreference = 'Stop'
if (-not (Test-Path -LiteralPath $DatabasePath -PathType Leaf)) { throw "Database not found: $DatabasePath" }
$previousPath = $env:WATCHER_DB_PATH
try {
    $env:WATCHER_DB_PATH = (Resolve-Path -LiteralPath $DatabasePath).Path
    $url = "http://127.0.0.1:$Port"
    Write-Host "Open $url in your browser. Keep this window open; Ctrl+C stops the dashboard."
    $exe = Join-Path $PSScriptRoot 'output\windows\Listener_DB_GUI.exe'
    if (Test-Path -LiteralPath $exe) { & $exe --urls $url }
    else { & dotnet run --project (Join-Path $PSScriptRoot 'Listener_DB_GUI.csproj') -c Release -- --urls $url }
    if ($LASTEXITCODE -ne 0) { throw 'Dashboard stopped with an error. See the output above.' }
}
finally { $env:WATCHER_DB_PATH = $previousPath }
