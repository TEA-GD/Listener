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
