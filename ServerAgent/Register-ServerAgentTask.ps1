#Requires -Version 5.1
#Requires -RunAsAdministrator
[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$CollectorHost,
    [ValidateRange(1,65535)][int]$CollectorPort = 6515,
    [string]$AgentId,
    [ValidateRange(1,3600)][int]$PollSeconds = 5
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$CollectorHost = $CollectorHost.Trim()
if ([Uri]::CheckHostName($CollectorHost) -eq [UriHostNameType]::Unknown) { throw 'Use a collector DNS name or IP, without http://, path or port.' }
$taskName = 'Watcher-Server-Agent'
$directory = Join-Path $env:ProgramData 'WatcherServerAgent'
$configPath = Join-Path $directory 'agent.json'
$source = Join-Path $PSScriptRoot 'Watcher-ServerAgent.ps1'
if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { throw 'Keep Watcher-ServerAgent.ps1 beside this registration helper.' }
if (Test-Path -LiteralPath $configPath) {
    $existing = Get-Content -LiteralPath $configPath -Encoding UTF8 -Raw | ConvertFrom-Json
    if ([string]::IsNullOrWhiteSpace($AgentId)) { $AgentId = [string]$existing.agent_id }
}
if ([string]::IsNullOrWhiteSpace($AgentId)) {
    $AgentId = $env:COMPUTERNAME
    $domain = [Net.NetworkInformation.IPGlobalProperties]::GetIPGlobalProperties().DomainName
    if ($domain -and -not $AgentId.EndsWith('.' + $domain,[StringComparison]::OrdinalIgnoreCase)) { $AgentId += '.' + $domain }
}
if ($AgentId -notmatch '^[A-Za-z0-9_.-]{1,120}$' -or $AgentId -in @('.','..')) { throw 'Invalid AgentId; use a hostname with letters, digits, dots, underscores or hyphens.' }
$previous = Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
if ($previous) {
    Stop-ScheduledTask -TaskName $taskName
    $deadline = [DateTime]::UtcNow.AddSeconds(30)
    while ((Get-ScheduledTask -TaskName $taskName).State -eq 'Running') {
        if ([DateTime]::UtcNow -ge $deadline) { throw 'Old task did not stop; retry after it exits.' }
        Start-Sleep -Milliseconds 250
    }
}
[IO.Directory]::CreateDirectory($directory) | Out-Null
# Only Administrators/SYSTEM may edit a script that will run as SYSTEM.
& icacls.exe $directory /inheritance:r /grant:r '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' /T | Out-Host
if ($LASTEXITCODE -ne 0) { throw 'Could not protect the agent directory.' }
$script = Join-Path $directory 'Watcher-ServerAgent.ps1'
if ([IO.Path]::GetFullPath($source) -ne [IO.Path]::GetFullPath($script)) { Copy-Item -LiteralPath $source -Destination $script -Force }
$spool = Join-Path $directory 'spool'
[IO.Directory]::CreateDirectory($spool) | Out-Null
$config = [ordered]@{
    agent_id=$AgentId; collector_host=$CollectorHost; collector_port=$CollectorPort
    spool_dir=$spool; log_file=(Join-Path $directory 'agent.log')
    poll_seconds=$PollSeconds; heartbeat_seconds=60; max_spool_events=50000; send_batch_size=1000
}
if (Test-Path -LiteralPath $configPath) { Copy-Item -LiteralPath $configPath -Destination ($configPath + '.bak') -Force }
$temporary = $configPath + '.tmp'
[IO.File]::WriteAllText($temporary, (ConvertTo-Json -InputObject $config), [Text.UTF8Encoding]::new($false))
Move-Item -LiteralPath $temporary -Destination $configPath -Force
$powerShell = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
$arguments = '-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "' + $script + '" -ConfigPath "' + $configPath + '"'
$action = New-ScheduledTaskAction -Execute $powerShell -Argument $arguments -WorkingDirectory $directory
$trigger = New-ScheduledTaskTrigger -AtStartup
$trigger.Delay = 'PT30S'
$principal = New-ScheduledTaskPrincipal -UserId 'SYSTEM' -LogonType ServiceAccount -RunLevel Highest
$settings = New-ScheduledTaskSettingsSet -MultipleInstances IgnoreNew -ExecutionTimeLimit ([TimeSpan]::Zero) -RestartCount 999 -RestartInterval (New-TimeSpan -Minutes 1) -StartWhenAvailable -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries
Register-ScheduledTask -TaskName $taskName -Action $action -Trigger $trigger -Principal $principal -Settings $settings -Description 'Watcher TCP metadata forwarding; starts after reboot and buffers until the collector acknowledges events.' -Force | Out-Null
Start-ScheduledTask -TaskName $taskName
Write-Host "Registered and started $taskName as SYSTEM. Starts 30 seconds after each reboot."
Write-Host "Collector: ${CollectorHost}:$CollectorPort | Host identity: $AgentId"
Write-Host "Configuration and logs: $directory | Existing queued events are preserved."
Write-Host "Check: Get-Content '$directory\agent.log' -Tail 30"
