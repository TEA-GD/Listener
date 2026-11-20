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
    if ($null -eq $c.PSObject.Properties['collect_udp']) { $c | Add-Member -NotePropertyName collect_udp -NotePropertyValue $true }
    if ($null -eq $c.PSObject.Properties['device_category']) { $c | Add-Member -NotePropertyName device_category -NotePropertyValue 'legacy' }
    if ($c.device_category -cnotin @('legacy','linux_servers','windows_servers','user_pcs','project_stands')) { throw 'Invalid device_category' }
    if ($c.collect_udp -isnot [bool]) { throw 'collect_udp must be true or false' }
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

function Get-ConnectionSnapshot([bool]$CollectUdp=$true) {
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
        $key = @('TCP',$row.LocalAddress,$row.LocalPort,$row.RemoteAddress,$row.RemotePort,$row.OwningProcess) -join '|'
        $connections[$key] = [pscustomobject]@{
            LocalIP=[string]$row.LocalAddress; LocalPort=[int]$row.LocalPort
            RemoteIP=[string]$row.RemoteAddress; RemotePort=[int]$row.RemotePort
            ProcessId=[int]$row.OwningProcess; ProcessName=$name; Software=$path; Protocol='TCP'; SocketState='ESTABLISHED'
        }
    }
    if ($CollectUdp) {
        $endpoints = @()
        try { $endpoints = @(Get-NetUDPEndpoint -ErrorAction Stop) }
        catch { if ($_.FullyQualifiedErrorId -notlike 'CmdletizationQuery_NotFound*') { throw } }
        foreach ($endpoint in $endpoints) {
            $process = $processes[[int]$endpoint.OwningProcess]
            $name='Unknown'; $path=''
            if ($null -ne $process) { $name=[string]$process.Name; $path=[string]$process.ExecutablePath }
            $key=@('UDP',$endpoint.LocalAddress,$endpoint.LocalPort,$endpoint.OwningProcess) -join '|'
            $connections[$key]=[pscustomobject]@{
                LocalIP=[string]$endpoint.LocalAddress; LocalPort=[int]$endpoint.LocalPort
                RemoteIP=''; RemotePort=0; ProcessId=[int]$endpoint.OwningProcess
                ProcessName=$name; Software=$path; Protocol='UDP'; SocketState='BOUND'
            }
        }
    }
    return $connections
}

function New-AgentEvent($Config, [string]$Snapshot, [string]$Type, $Connection) {
    $now = [DateTimeOffset]::UtcNow
    $row = [ordered]@{
        DEVICE_CATEGORY=[string]$Config.device_category
        EVENT_ID=[guid]::NewGuid().ToString('D'); EVENT_TYPE=$Type
        TIMESTAMP_UTC=$now.ToString('yyyy-MM-ddTHH:mm:ss.fffzzz'); DATE_AND_TIME=[DateTime]::Now.ToString('yyyy-MM-dd HH:mm:ss.fff')
        SOURCE_IP=''; SOURCE_DOMAIN_NAME=[string]$Config.agent_id; PROCESS_NAME=''; PROCESS_ID=''
        RECIEVER_IP=''; RECIEVER_DOMAINNAME=''; PORT_USED=''; SOFTWARE_THAT_USED=''; SOURCE_PORT=''
        PROTOCOL=''; STATE=''; OPERATING_SYSTEM='Windows'; MONITORED_HOST=[string]$Config.agent_id; SNAPSHOT_ID=$Snapshot
    }
    if ($null -ne $Connection) {
        $row.SOURCE_IP=$Connection.LocalIP; $row.PROCESS_NAME=$Connection.ProcessName; $row.PROCESS_ID=[string]$Connection.ProcessId
        $row.RECIEVER_IP=$Connection.RemoteIP
        if ($Connection.RemotePort -gt 0) { $row.PORT_USED=[string]$Connection.RemotePort }
        $row.SOFTWARE_THAT_USED=$Connection.Software; $row.SOURCE_PORT=[string]$Connection.LocalPort
        $row.PROTOCOL=$Connection.Protocol; $row.STATE=$Connection.SocketState
        if ($Type -in @('TCP_DISCONNECTED','UDP_ENDPOINT_CLOSED')) { $row.STATE='CLOSED_OR_NOT_OBSERVED' }
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
    $sendBudget = [Diagnostics.Stopwatch]::StartNew()
    $client = [Net.Sockets.TcpClient]::new()
    try {
        $connect = $client.ConnectAsync($Config.collector_host, $Config.collector_port)
        if (-not $connect.Wait(8000)) { throw 'Collector connect timeout' }
        $connect.GetAwaiter().GetResult() | Out-Null
        $stream = $client.GetStream(); $stream.ReadTimeout=10000; $stream.WriteTimeout=10000
        $client.NoDelay=$true
        $sent=0; $index=0; $window=1
        while($index -lt $files.Count){
            if($sent -gt 0 -and $sendBudget.Elapsed.TotalSeconds -ge 5){break}
            $group=@();$size=0
            while($index -lt $files.Count -and $group.Count -lt $window){
                $file=$files[$index];$raw=[IO.File]::ReadAllText($file.FullName);$body=$raw | ConvertFrom-Json;$id=[string]$body.EVENT_ID
                # Preserve the original JSON and timestamps; never reserialize parsed dates.
                $request='{"event":'+$raw.Replace("`r",'').Replace("`n",'')
                if($index -eq 0){$request+=',"request_pipeline":true'}
                $request+='}';$bytes=[Text.Encoding]::UTF8.GetBytes($request+"`n")
                if($bytes.Length -gt 32768){throw "Oversized event retained: $($file.Name)"}
                if($group.Count -gt 0 -and $size+$bytes.Length -gt 131072){break}
                $group+=@{File=$file;Id=$id;Bytes=$bytes};$size+=$bytes.Length;$index++
            }
            foreach($item in $group){$stream.Write($item.Bytes,0,$item.Bytes.Length)};$stream.Flush()
            foreach($item in $group){
                $ack=Read-CollectorAck $stream
                if($sent -eq 0 -and $ack -ceq ('ACK '+$item.Id+' PIPELINE64')){$window=64}
                elseif($ack -cne ('ACK '+$item.Id)){throw "Collector rejected event: $ack"}
                [IO.File]::Delete($item.File.FullName);$sent++
            }
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
                $current = Get-ConnectionSnapshot $config.collect_udp
                $batch = [Collections.Generic.List[object]]::new()
                $snapshot = [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffZ')
                foreach ($key in $current.Keys) {
                    if ($null -eq $previous) { $type='ACTIVE_AT_START'; if ($current[$key].Protocol -eq 'UDP') { $type='UDP_ENDPOINT_AT_START' }; $batch.Add((New-AgentEvent $config $snapshot $type $current[$key])) }
                    elseif (-not $previous.ContainsKey($key)) { $type='TCP_CONNECTED'; if ($current[$key].Protocol -eq 'UDP') { $type='UDP_ENDPOINT_OPENED' }; $batch.Add((New-AgentEvent $config $snapshot $type $current[$key])) }
                }
                if ($null -ne $previous) {
                    foreach ($key in $previous.Keys) {
                        if (-not $current.ContainsKey($key)) { $type='TCP_DISCONNECTED'; if ($previous[$key].Protocol -eq 'UDP') { $type='UDP_ENDPOINT_CLOSED' }; $batch.Add((New-AgentEvent $config $snapshot $type $previous[$key])) }
                    }
                }
                $heartbeatDue = ([DateTimeOffset]::UtcNow - $lastHeartbeat).TotalSeconds -ge $config.heartbeat_seconds
                if ($heartbeatDue) {
                    foreach ($socket in $current.Values) {
                        $kind='TCP_SNAPSHOT'; if ($socket.Protocol -eq 'UDP') { $kind='UDP_SNAPSHOT' }
                        $batch.Add((New-AgentEvent $config $snapshot $kind $socket))
                    }
                    try {
                        $contextScript=Join-Path $PSScriptRoot 'NetworkContext.ps1'
                        if (-not(Test-Path $contextScript)) { $contextScript=Join-Path (Split-Path $PSScriptRoot -Parent) 'Shared/NetworkContext.ps1' }
                        $context=(& $contextScript | ConvertFrom-Json)
                        foreach($network in $context.networks){ $r=New-AgentEvent $config $snapshot 'NETWORK_CONTEXT' $null; $r.CONTEXT_JSON=ConvertTo-Json $network -Compress; $batch.Add($r) }
                        foreach($process in @($context.processes | Select-Object -First 4096)){ $r=New-AgentEvent $config $snapshot 'PROCESS_CONTEXT' $null; $r.CONTEXT_JSON=ConvertTo-Json $process -Compress; $batch.Add($r) }
                        $r=New-AgentEvent $config $snapshot 'CONTEXT_COMPLETE' $null
                        $r.CONTEXT_JSON=ConvertTo-Json @{networks=@($context.networks).Count;processes=[Math]::Min(4096,@($context.processes).Count);status=$context.status;note=$context.note} -Compress; $batch.Add($r)
                    } catch { Write-AgentLog $config ('Network context unavailable: '+$_.Exception.Message) }
                    $complete=New-AgentEvent $config $snapshot 'HEARTBEAT' $null
                    $complete.STATE='SNAPSHOT_COMPLETE:'+$current.Count
                    $batch.Add($complete)
                }
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
