param([string]$AgentScript,[string]$ConfigPath,[ValidateSet('Collect','Send','Checks','Continuous')][string]$Mode='Checks')
$ErrorActionPreference='Stop'
. $AgentScript -ConfigPath $ConfigPath
function Get-CimInstance { param($ClassName) [pscustomobject]@{ProcessId=4884;Name='server-process.exe';ExecutablePath='C:\Services\server-process.exe'} }
$script:sample = 0
function Get-NetTCPConnection {
    param($State)
    $script:sample++
    if ($Mode -eq 'Continuous' -and $script:sample -ge 3) { return }
    $remotePort=443
    if ($Mode -eq 'Continuous' -and $script:sample -eq 2) { $remotePort=8443 }
    [pscustomobject]@{LocalAddress='192.0.2.10';LocalPort=57224;RemoteAddress='203.0.113.20';RemotePort=$remotePort;OwningProcess=4884}
    [pscustomobject]@{LocalAddress='127.0.0.1';LocalPort=57225;RemoteAddress='127.0.0.1';RemotePort=443;OwningProcess=4884}
}
if ($Mode -eq 'Collect') { exit (Invoke-ServerAgent $ConfigPath $true) }
if ($Mode -eq 'Continuous') { exit (Invoke-ServerAgent $ConfigPath $false) }
$config = Read-AgentConfig $ConfigPath
if ($Mode -eq 'Send') { $sent=Send-Spool $config; Write-Host "SENT=$sent"; exit 0 }
foreach ($file in Get-ChildItem (Split-Path $AgentScript) -Filter '*.ps1') {
    $tokens=$null; $parseErrors=$null
    [Management.Automation.Language.Parser]::ParseFile($file.FullName,[ref]$tokens,[ref]$parseErrors) | Out-Null
    if ($parseErrors.Count -gt 0) { throw ($parseErrors | Out-String) }
}
$snapshot=Get-ConnectionSnapshot
if ($snapshot.Count -ne 1) { throw 'Snapshot did not exclude loopback' }
$connection=@($snapshot.Values)[0]
foreach ($type in @('ACTIVE_AT_START','TCP_CONNECTED','TCP_DISCONNECTED','HEARTBEAT')) {
    $socket=$connection
    if ($type -eq 'HEARTBEAT') { $socket=$null }
    $row=New-AgentEvent $config 'test-snapshot' $type $socket
    if ($row.EVENT_TYPE -ne $type -or $row.MONITORED_HOST -ne $config.agent_id) { throw 'Incorrect event mapping' }
    if ($type -eq 'TCP_DISCONNECTED' -and $row.STATE -ne 'CLOSED_OR_NOT_OBSERVED') { throw 'Incorrect closed state' }
    if ($type -eq 'HEARTBEAT' -and $row.SOURCE_IP -ne '') { throw 'Heartbeat must not carry connection fields' }
}
[IO.Directory]::CreateDirectory($config.spool_dir) | Out-Null
$event=New-AgentEvent $config 'test' 'HEARTBEAT' $null
Add-SpoolBatch $config @($event)
$config.max_spool_events=1
$blocked=$false
try { Add-SpoolBatch $config @($event) } catch { $blocked=$true }
if (-not $blocked -or @(Get-ChildItem $config.spool_dir -Filter '*.json').Count -ne 1) { throw 'Spool capacity must preserve pending events' }
$config.collector_host='https://invalid.example'
$temp=Join-Path (Split-Path $ConfigPath) 'bad.json'
[IO.File]::WriteAllText($temp,($config|ConvertTo-Json))
$blocked=$false
try { Read-AgentConfig $temp | Out-Null } catch { $blocked=$true }
if (-not $blocked) { throw 'URL must not be accepted as hostname' }
Write-Host 'PASS: PowerShell parsing, TCP mapping, loopback exclusion, event types, spool capacity and config validation'
