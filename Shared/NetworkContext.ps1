# Metadata only: no command lines, credentials, packet payloads or message content.
Set-StrictMode -Off # Optional CIM/profile properties may be unavailable; do not inherit the caller's strict mode.
$ErrorActionPreference='Stop'
[Console]::OutputEncoding=New-Object Text.UTF8Encoding($false)
$errorsFound=@();$networks=@();$processes=@()
try {
 $profiles=@(Get-NetConnectionProfile -ErrorAction SilentlyContinue)
 $wifiNames=@{};$wifiGuid=''
 try { foreach($line in @(& netsh.exe wlan show interfaces 2>$null)) {
  if($line -match '^\s*GUID\s*:\s*\{?([a-fA-F0-9-]+)'){$wifiGuid=$Matches[1].ToLowerInvariant()}
  elseif($wifiGuid -and $line -match '^\s*SSID\s*:\s*(.+)$'){$wifiNames[$wifiGuid]=$Matches[1].Trim()}
 }}catch{}

 $networks=@(Get-NetAdapter -ErrorAction Stop | Where-Object Status -eq 'Up' | ForEach-Object {
  $a=$_;$profile=$profiles | Where-Object InterfaceIndex -eq $a.ifIndex | Select-Object -First 1
  $kind='other';if([int]$a.InterfaceType -eq 71){$kind='wifi'}elseif([int]$a.InterfaceType -eq 6){$kind='ethernet'}
  if($a.InterfaceDescription -match 'vpn|wireguard|wintun|tap-|tun|amnezia'){$kind='vpn'}
  $networkName=[string]$profile.Name
  $guid=([string]$a.InterfaceGuid).Trim('{}').ToLowerInvariant()
  if($kind -eq 'wifi' -and $wifiNames.ContainsKey($guid)){$networkName=$wifiNames[$guid]}
  $ip=Get-NetIPConfiguration -InterfaceIndex $a.ifIndex -ErrorAction SilentlyContinue
  @{adapter_id=[string]$a.InterfaceGuid;adapter_name=[string]$a.Name;network_type=$kind;network_name=$networkName;connection_type=[string]$a.InterfaceDescription;state='Up';addresses=(@($ip.IPv4Address.IPAddress)+@($ip.IPv6Address.IPAddress) -join ',');gateway=(@($ip.IPv4DefaultGateway.NextHop) -join ',')}
 })
}catch{$errorsFound+='network sampling failed'}
try {
 $processes=@(Get-CimInstance Win32_Process -ErrorAction Stop | ForEach-Object {
  @{entry_type='process';pid=[int]$_.ProcessId;name=[string]$_.Name;path=[string]$_.ExecutablePath;started_utc=$(if($_.CreationDate){$_.CreationDate.ToUniversalTime().ToString('o')}else{''})}
 })
}catch{$errorsFound+='process sampling failed'}
try {
 $processes+=@(Get-CimInstance Win32_Service -Filter "State='Running'" -ErrorAction Stop | ForEach-Object {
  @{entry_type='service';pid=[int]$_.ProcessId;name=[string]$_.Name;path='';started_utc=''}
 })
}catch{$errorsFound+='service sampling failed'}
if($networks.Count -gt 128 -or $processes.Count -gt 4096){$errorsFound+='sample capped'}
$networks=@($networks | Select-Object -First 128);$processes=@($processes | Select-Object -First 4096)
foreach($row in @($networks)+@($processes)){foreach($key in @($row.Keys)){if($row[$key] -is [string] -and $row[$key].Length -gt 256){$row[$key]=$row[$key].Substring(0,256);$errorsFound+='metadata truncated'}}}
@{networks=@($networks);processes=@($processes);status=$(if($errorsFound.Count){'partial'}else{'ok'});note=(($errorsFound | Select-Object -Unique) -join '; ')} | ConvertTo-Json -Depth 5 -Compress
