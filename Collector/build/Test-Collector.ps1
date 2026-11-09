$port = 6515
$base = Join-Path $env:ProgramData 'WatcherCollector'
Write-Host '=== Watcher Collector Diagnostics (PLAIN TCP; NO TLS) ===' -ForegroundColor Cyan
Get-Service WatcherCollector -ErrorAction SilentlyContinue | Format-Table Name,Status,StartType
Write-Host 'Listening socket:'
Get-NetTCPConnection -LocalPort $port -State Listen -ErrorAction SilentlyContinue | Format-Table LocalAddress,LocalPort,State,OwningProcess
Write-Host 'Files:'
@('collector.json','watcher-events.db','collector.log') | ForEach-Object {
  $path = Join-Path $base $_
  [PSCustomObject]@{ File=$path; Exists=Test-Path $path }
} | Format-Table -AutoSize
$log = Join-Path $base 'collector.log'
if(Test-Path $log){ Write-Host 'Latest collector messages:'; Get-Content $log -Tail 20 }
Write-Host 'Expected CSV directory: C:\Watcher\Feeds\Connections'
