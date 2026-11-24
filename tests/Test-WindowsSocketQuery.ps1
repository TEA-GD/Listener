param([string]$AgentSource)
$ErrorActionPreference='Stop'
$source=Get-Content -LiteralPath $AgentSource -Raw
$query=[regex]::Match($source,'private const string WindowsQuery = """(.*?)""";',[Text.RegularExpressions.RegexOptions]::Singleline).Groups[1].Value
if (-not $query) { throw 'Windows query not found' }
function Get-CimInstance { [pscustomobject]@{ProcessId=123;Name='udp-test.exe';ExecutablePath='C:\udp-test.exe'} }
function Get-NetTCPConnection { @() }
function Get-NetUDPEndpoint { [pscustomobject]@{LocalAddress='0.0.0.0';LocalPort=5353;OwningProcess=123} }
$rows=@((& ([scriptblock]::Create($query.Replace('__COLLECT_UDP__','$true')))) | ConvertFrom-Json)
if ($rows.Count -ne 1 -or $rows[0].protocol -ne 'UDP' -or $rows[0].local_port -ne 5353 -or $rows[0].remote_ip -ne '') { throw 'Incorrect Windows UDP snapshot' }
$rows=@((& ([scriptblock]::Create($query.Replace('__COLLECT_UDP__','$false')))) | ConvertFrom-Json)
if ($rows.Count -ne 0) { throw 'TCP-only query included UDP' }
Write-Host 'PASS: embedded Windows query with empty TCP snapshot and UDP enabled/disabled'

function Get-NetTCPConnection {
 param($State)
 if($State -eq 'Listen'){[pscustomobject]@{LocalAddress='0.0.0.0';LocalPort=443;OwningProcess=123}}
 else {
  [pscustomobject]@{LocalAddress='192.0.2.5';LocalPort=443;RemoteAddress='203.0.113.8';RemotePort=50000;OwningProcess=123}
  [pscustomobject]@{LocalAddress='192.0.2.5';LocalPort=50001;RemoteAddress='203.0.113.9';RemotePort=443;OwningProcess=123}
 }
}
$rows=@((& ([scriptblock]::Create($query.Replace('__COLLECT_UDP__','$false')))) | ConvertFrom-Json)
if($rows.Count -ne 2 -or $rows[0].connection_direction -ne 'incoming' -or $rows[1].connection_direction -ne 'outgoing'){throw 'Incorrect Windows listener direction estimate'}
function Get-NetTCPConnection {
 param($State)
 if($State -eq 'Listen'){throw 'Listener access denied'}
 [pscustomobject]@{LocalAddress='192.0.2.5';LocalPort=50001;RemoteAddress='203.0.113.9';RemotePort=443;OwningProcess=123}
}
$rows=@((& ([scriptblock]::Create($query.Replace('__COLLECT_UDP__','$false')))) | ConvertFrom-Json)
if($rows[0].connection_direction -ne 'unknown'){throw 'Unavailable listeners were guessed'}
Write-Host 'PASS: embedded Windows query incoming/outgoing estimates and unavailable-listener fallback'
