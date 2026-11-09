# Run elevated. Preserves spool + credentials for forensic/reinstall purposes.
$ErrorActionPreference = 'Stop'
schtasks.exe /End /TN 'Watcher-Connection-Agent' 2>$null
schtasks.exe /Delete /TN 'Watcher-Connection-Agent' /F
Write-Host 'Startup task removed. Stop any remaining WatcherAgent.exe process before deleting binaries.'
Write-Host 'Then delete C:\Program Files\Watcher\Agent when the process exits.'
Write-Host 'Credentials/data intentionally preserved: C:\ProgramData\WatcherAgent'
