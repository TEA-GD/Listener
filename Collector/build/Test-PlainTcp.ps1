# Test the V5 collector from a computer whose IP is included in allowed_client_cidrs.
# Sends a clearly identified HEARTBEAT event and expects ACK. No TLS, keys, or tokens.
param(
    [string]$HostName = 'watcher-collector.domain.local',
    [int]$Port = 6515
)
$ErrorActionPreference = 'Stop'
$id = [guid]::NewGuid().ToString('D')
$stamp = [DateTimeOffset]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ss.fffzzz')
$message = @{
    event = @{
        EVENT_ID = $id
        EVENT_TYPE = 'HEARTBEAT'
        MONITORED_HOST = 'WATCHER_TEST'
        TIMESTAMP_UTC = $stamp
        DATE_AND_TIME = (Get-Date -Format 'yyyy-MM-dd HH:mm:ss.fff')
        OPERATING_SYSTEM = 'Windows'
        PROTOCOL = ''
        STATE = ''
        SNAPSHOT_ID = "TEST-$id"
    }
} | ConvertTo-Json -Depth 5 -Compress
$client = [System.Net.Sockets.TcpClient]::new()
try {
    $async = $client.BeginConnect($HostName, $Port, $null, $null)
    if (-not $async.AsyncWaitHandle.WaitOne(8000)) { throw "Connection timed out" }
    $client.EndConnect($async)
    $client.ReceiveTimeout = 10000
    $client.SendTimeout = 10000
    $stream = $client.GetStream()
    $writer = [System.IO.StreamWriter]::new($stream, [System.Text.UTF8Encoding]::new($false), 1024, $true)
    $writer.NewLine = "`n"
    $writer.WriteLine($message)
    $writer.Flush()
    $reader = [System.IO.StreamReader]::new($stream)
    $response = $reader.ReadLine()
    Write-Host "Collector response: $response"
    if ($response -ne "ACK $id") { throw "Unexpected response: $response" }
    Write-Host "SUCCESS: V5 collector accepted test HEARTBEAT. CSV export may take up to 10s." -ForegroundColor Green
}
finally { $client.Dispose() }
