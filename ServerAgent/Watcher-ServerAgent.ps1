#Requires -Version 5.1
[CmdletBinding()]
param(
    [string]$ConfigPath = (Join-Path $env:ProgramData 'WatcherServerAgent\agent.json'),
    [switch]$Once
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Read-AgentConfig([string]$Path) {
    $c = Get-Content -LiteralPath $Path -Encoding UTF8 -Raw | ConvertFrom-Json
    foreach ($name in @('agent_id','collector_host','collector_port','spool_dir','log_file','poll_seconds','heartbeat_seconds','max_spool_events','send_batch_size')) {
        if ($null -eq $c.PSObject.Properties[$name]) { throw "Missing configuration field: $name" }
    }
    if ($c.agent_id -notmatch '^[A-Za-z0-9_.-]{1,120}$' -or $c.agent_id -in @('.','..')) { throw 'Invalid agent_id' }
    $c.collector_host = ([string]$c.collector_host).Trim()
    if ([Uri]::CheckHostName($c.collector_host) -eq [UriHostNameType]::Unknown) { throw 'Invalid collector hostname; use a DNS name or IP without a URL or port' }
    foreach ($spec in @(@('collector_port',1,65535),@('poll_seconds',1,3600),@('heartbeat_seconds',1,86400),@('max_spool_events',1,1000000),@('send_batch_size',1,10000))) {
        $number = 0
        if (-not [int]::TryParse([string]$c.($spec[0]), [ref]$number) -or $number -lt $spec[1] -or $number -gt $spec[2]) { throw "Invalid $($spec[0])" }
        $c.($spec[0]) = $number
    }
    foreach ($name in @('spool_dir','log_file')) {
        if ([string]::IsNullOrWhiteSpace($c.$name) -or -not [IO.Path]::IsPathRooted($c.$name)) { throw "$name must be an absolute path" }
    }
    return $c
}

function Write-AgentLog($Config, [string]$Message) {
    $line = [DateTimeOffset]::UtcNow.ToString('o') + ' ' + $Message
    Write-Host $line
    try {
        if ((Test-Path -LiteralPath $Config.log_file) -and (Get-Item -LiteralPath $Config.log_file).Length -ge 5MB) {
            Move-Item -LiteralPath $Config.log_file -Destination ($Config.log_file + '.1') -Force
        }
        [IO.File]::AppendAllText($Config.log_file, $line + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))
    } catch { Write-Warning "Could not write agent log: $($_.Exception.Message)" }
}

function Get-ConnectionSnapshot {
    $processes = @{}
    Get-CimInstance Win32_Process -ErrorAction Stop | ForEach-Object { $processes[[int]$_.ProcessId] = $_ }
    $connections = @{}
    $sockets = @()
    try { $sockets = @(Get-NetTCPConnection -State Established -ErrorAction Stop) }
    catch {
        # Windows reports an empty CIM query as ObjectNotFound on some versions.
        if ($_.FullyQualifiedErrorId -notlike 'CmdletizationQuery_NotFound*') { throw }
    }
    $sockets | ForEach-Object {
        $row = $_
        if ($row.RemoteAddress -in @('127.0.0.1','::1','') -or [string]::IsNullOrEmpty($row.LocalAddress)) { return }
        $process = $processes[[int]$row.OwningProcess]
        $name = 'Unknown'; $path = ''
        if ($null -ne $process) { $name = [string]$process.Name; $path = [string]$process.ExecutablePath }
        $key = @($row.LocalAddress,$row.LocalPort,$row.RemoteAddress,$row.RemotePort,$row.OwningProcess) -join '|'
        $connections[$key] = [pscustomobject]@{
            LocalIP=[string]$row.LocalAddress; LocalPort=[int]$row.LocalPort
            RemoteIP=[string]$row.RemoteAddress; RemotePort=[int]$row.RemotePort
            ProcessId=[int]$row.OwningProcess; ProcessName=$name; Software=$path
        }
    }
    return $connections
}

function New-AgentEvent($Config, [string]$Snapshot, [string]$Type, $Connection) {
    $now = [DateTimeOffset]::UtcNow
    $row = [ordered]@{
        EVENT_ID=[guid]::NewGuid().ToString('D'); EVENT_TYPE=$Type
        TIMESTAMP_UTC=$now.ToString('yyyy-MM-ddTHH:mm:ss.fffzzz'); DATE_AND_TIME=[DateTime]::Now.ToString('yyyy-MM-dd HH:mm:ss.fff')
        SOURCE_IP=''; SOURCE_DOMAIN_NAME=[string]$Config.agent_id; PROCESS_NAME=''; PROCESS_ID=''
        RECIEVER_IP=''; RECIEVER_DOMAINNAME=''; PORT_USED=''; SOFTWARE_THAT_USED=''; SOURCE_PORT=''
        PROTOCOL=''; STATE=''; OPERATING_SYSTEM='Windows'; MONITORED_HOST=[string]$Config.agent_id; SNAPSHOT_ID=$Snapshot
    }
    if ($null -ne $Connection) {
        $row.SOURCE_IP=$Connection.LocalIP; $row.PROCESS_NAME=$Connection.ProcessName; $row.PROCESS_ID=[string]$Connection.ProcessId
        $row.RECIEVER_IP=$Connection.RemoteIP; $row.PORT_USED=[string]$Connection.RemotePort
        $row.SOFTWARE_THAT_USED=$Connection.Software; $row.SOURCE_PORT=[string]$Connection.LocalPort
        $row.PROTOCOL='TCP'; $row.STATE='ESTABLISHED'
        if ($Type -eq 'TCP_DISCONNECTED') { $row.STATE='CLOSED_OR_NOT_OBSERVED' }
    }
    return $row
}

function Add-SpoolBatch($Config, [object[]]$Events) {
    $pending = @(Get-ChildItem -LiteralPath $Config.spool_dir -Filter '*.json' -File).Count
    if ($pending + $Events.Count -gt $Config.max_spool_events) { throw 'Spool capacity exceeded; pending events are preserved' }
    foreach ($row in $Events) {
        $name = [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffffffZ') + '_' + $row.EVENT_ID
        $temporary = Join-Path $Config.spool_dir ($name + '.tmp')
        $destination = Join-Path $Config.spool_dir ($name + '.json')
        $body = [Text.Encoding]::UTF8.GetBytes((ConvertTo-Json -InputObject $row -Depth 5 -Compress))
        if ($body.Length -gt 30000) { throw 'Event too large for the collector protocol' }
        $stream = [IO.File]::Open($temporary, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
        try { $stream.Write($body,0,$body.Length); $stream.Flush($true) } finally { $stream.Dispose() }
        [IO.File]::Move($temporary,$destination)
    }
}

function Read-CollectorAck([IO.Stream]$Stream) {
    $buffer = [Collections.Generic.List[byte]]::new()
    while ($true) {
        $b = $Stream.ReadByte()
        if ($b -lt 0) { throw 'Collector disconnected before ACK' }
        if ($b -eq 10) { return [Text.Encoding]::ASCII.GetString($buffer.ToArray()).TrimEnd([char]13) }
        if ($buffer.Count -ge 128) { throw 'Collector ACK too long' }
        $buffer.Add([byte]$b)
    }
}

function Send-Spool($Config) {
    $files = @(Get-ChildItem -LiteralPath $Config.spool_dir -Filter '*.json' -File | Sort-Object Name | Select-Object -First $Config.send_batch_size)
    if ($files.Count -eq 0) { return 0 }
    $client = [Net.Sockets.TcpClient]::new()
    try {
        $connect = $client.ConnectAsync($Config.collector_host, $Config.collector_port)
        if (-not $connect.Wait(8000)) { throw 'Collector connect timeout' }
        $connect.GetAwaiter().GetResult()
        $stream = $client.GetStream(); $stream.ReadTimeout=10000; $stream.WriteTimeout=10000
        $sent = 0
        foreach ($file in $files) {
            $body = [IO.File]::ReadAllText($file.FullName) | ConvertFrom-Json
            $id = [string]$body.EVENT_ID
            $request = ConvertTo-Json -InputObject @{event=$body} -Depth 5 -Compress
            $bytes = [Text.Encoding]::UTF8.GetBytes($request + "`n")
            if ($bytes.Length -gt 32768) { throw "Oversized event retained: $($file.Name)" }
            $stream.Write($bytes,0,$bytes.Length); $stream.Flush()
            $ack = Read-CollectorAck $stream
            if ($ack -cne ('ACK ' + $id)) { throw "Collector rejected event: $ack" }
            [IO.File]::Delete($file.FullName)
            $sent++
        }
        return $sent
    } finally { $client.Dispose() }
}

function Invoke-ServerAgent([string]$Path, [bool]$RunOnce) {
    $config = Read-AgentConfig $Path
    [IO.Directory]::CreateDirectory($config.spool_dir) | Out-Null
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($config.log_file)) | Out-Null
    # Hold an exclusive lock for this spool, including when started manually alongside the task.
    $lockPath = Join-Path $config.spool_dir 'agent.lock'
    $lock = [IO.File]::Open($lockPath,[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
    try {
        Write-AgentLog $config "STARTED $($config.agent_id) -> $($config.collector_host):$($config.collector_port)"
        $previous = $null; $lastHeartbeat = [DateTimeOffset]::MinValue
        do {
            $clock = [Diagnostics.Stopwatch]::StartNew(); $failed = $false
            try {
                $current = Get-ConnectionSnapshot
                $batch = [Collections.Generic.List[object]]::new()
                $snapshot = [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffZ')
                foreach ($key in $current.Keys) {
                    if ($null -eq $previous) { $batch.Add((New-AgentEvent $config $snapshot 'ACTIVE_AT_START' $current[$key])) }
                    elseif (-not $previous.ContainsKey($key)) { $batch.Add((New-AgentEvent $config $snapshot 'TCP_CONNECTED' $current[$key])) }
                }
                if ($null -ne $previous) {
                    foreach ($key in $previous.Keys) {
                        if (-not $current.ContainsKey($key)) { $batch.Add((New-AgentEvent $config $snapshot 'TCP_DISCONNECTED' $previous[$key])) }
                    }
                }
                $heartbeatDue = ([DateTimeOffset]::UtcNow - $lastHeartbeat).TotalSeconds -ge $config.heartbeat_seconds
                if ($heartbeatDue) { $batch.Add((New-AgentEvent $config $snapshot 'HEARTBEAT' $null)) }
                Add-SpoolBatch $config $batch.ToArray()
                $previous = $current
                if ($heartbeatDue) { $lastHeartbeat = [DateTimeOffset]::UtcNow }
                Write-AgentLog $config "Snapshot sockets=$($current.Count) queued=$($batch.Count)"
            } catch { $failed=$true; Write-AgentLog $config "Collection/spool error: $($_.Exception.Message)" }
            try {
                $sent = Send-Spool $config
                if ($sent -gt 0) { Write-AgentLog $config "Acknowledged: $sent" }
            } catch { $failed=$true; Write-AgentLog $config "Delivery error; events retained: $($_.Exception.Message)" }
            if ($RunOnce) { if ($failed) { return 1 }; return 0 }
            $delay = [Math]::Max(0, $config.poll_seconds*1000 - $clock.ElapsedMilliseconds)
            if ($delay -gt 0) { Start-Sleep -Milliseconds ([int]$delay) }
        } while ($true)
    } finally { $lock.Dispose() }
}

if ($MyInvocation.InvocationName -ne '.') {
    try { exit (Invoke-ServerAgent $ConfigPath $Once.IsPresent) }
    catch { Write-Error $_ -ErrorAction Continue; exit 1 }
}
