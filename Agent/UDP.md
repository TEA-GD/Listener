# TCP + UDP socket monitoring

UDP monitoring is enabled by default in updated Windows/Linux agents and the
PowerShell ServerAgent. It uses OS socket snapshots, like the existing TCP
monitor, rather than packet capture. There is no additional driver or packet
payload collection.

## Available information

| Agent/platform | UDP source | Recorded information |
| --- | --- | --- |
| Windows compiled agent | Get-NetUDPEndpoint | Bound local IP/port, PID, process name/path. Remote IP/port remain empty. |
| Windows PowerShell ServerAgent | Get-NetUDPEndpoint | Same bound-endpoint information. |
| Linux compiled agent | ss -H -n -u -a -p | Local endpoint and process metadata; configured remote IP/port when the socket exposes a peer. Wildcard peers stay empty. |

A UDP socket can send to many remote addresses without calling connect(). These
snapshots cannot enumerate those destinations. In particular, the Windows UDP
endpoint API provides no remote address even for application-configured peers.
A Linux configured peer is socket configuration, not proof a datagram was sent.
Bound endpoints can be idle; no per-packet counts, DNS names or payloads are
inferred. Polling can miss short-lived sockets.

Bindings such as `0.0.0.0`, `::` or `*` mean any local interface. They are not
remote addresses. Local loopback-only UDP bindings can also be reported. Known
loopback remote peers remain filtered consistently with TCP peer filtering.

## Events and columns

- `UDP_ENDPOINT_AT_START`: endpoint exists in the first snapshot.
- `UDP_ENDPOINT_OPENED`: endpoint appears in a later snapshot.
- `UDP_ENDPOINT_CLOSED`: endpoint disappears or is no longer observed.

The existing database/CSV columns are reused:

```json
{
  "EVENT_TYPE": "UDP_ENDPOINT_AT_START",
  "PROTOCOL": "UDP",
  "SOURCE_IP": "0.0.0.0",
  "SOURCE_PORT": "5353",
  "RECIEVER_IP": "",
  "PORT_USED": "",
  "PROCESS_NAME": "service.exe",
  "STATE": "BOUND"
}
```

Known Linux peers have `STATE=PEER_CONFIGURED` and actual remote fields. Closed
endpoints use `CLOSED_OR_NOT_OBSERVED`. TCP keeps its existing event types and
state meanings. Snapshot keys include protocol to prevent TCP/UDP collisions.
UDP closing/opening is an observation of table changes, not a network handshake.

## Upgrade order

1. Update the source: `git pull --ff-only`.
2. Rebuild and install/update the **collector first**, using the Windows build
   scripts and existing installer. It must recognize the new UDP event types.
3. Rebuild/install the Windows agent. The installer now has **Monitor UDP
   endpoints** checked by default; it reloads saved settings when updating.
4. For the server script, copy the updated ServerAgent folder and rerun
   Register-ServerAgentTask.ps1 with the same collector hostname. Existing
   identity, spool and an explicitly configured collect_udp setting are retained.
5. On Debian 12/13 amd64, build and upgrade the package:

```bash
bash Agent/build/build-linux.sh
sudo apt install ./Agent/output/watcher-agent_1.0.5_amd64.deb
```

6. Rebuild/restart Listener_DB_GUI and Listener_Analyzer for their UDP views and
   profile support. Existing TCP data and analyzer state do not need deletion;
   the analyzer adds its new UDP profile tables automatically.

Agents still send all metadata over the existing **TCP JSON/ACK transport** on
port 6515. Do not open UDP port 6515 for this update. The collected protocol and
the event-delivery transport are different settings.

An old collector rejects new UDP event types. The agent preserves those files,
which can block later queued delivery until the collector is updated. Do not
clear the spool to work around this; upgrade the collector first.

## Disable UDP collection

Set `"collect_udp": false` in the agent's configuration, or uncheck the Windows
installer option and Install / Update. Restart the agent after manual edits.

- Windows agent: `%ProgramData%\WatcherAgent\agent.json`
- ServerAgent: `%ProgramData%\WatcherServerAgent\agent.json`
- Linux: `/etc/watcher-agent/agent.json`

Older configurations without the field default to true in the new agent.
The PowerShell field must be a JSON boolean, not a quoted string. Disabling
collection does not delete UDP events already queued or stored.

## Dashboard and analysis

The main event view adds **Protocol** and **Local port** columns, a TCP/UDP filter,
UDP event-type choices, UDP endpoint observation counts and local-port statistics.
Search also matches local ports. Empty destination fields mean unknown, not a
remote endpoint at 0.0.0.0. Connection/event counts are not live socket counts.

The New & rare connections board includes configured UDP peers when available
and has its own protocol filter. Peerless UDP bindings do not become destination
IPs or false new-destination alerts. This board cannot show remote destinations
that the OS socket snapshots do not provide.

The analyzer learns local UDP bindings in `udp_endpoints` and
`udp_endpoint_days` (also in exported profiles), plus destination combinations
for UDP records with known peers. Known-peer alerts explicitly say that a UDP
socket was configured for that peer. Local-binding alerts require the optional
`"alert_udp_endpoints": true` analyzer setting; they default off to avoid noise
from ephemeral client ports. Local-binding alert reasons are `new_udp_endpoint`,
`rare_udp_endpoint` and `returned_udp_endpoint_after_gap`. Such alerts explicitly
state that the remote peer is unavailable and keep destination fields empty.
Normal baseline/cooldown/age/order rules apply.

## Validation

Tests cover Windows-query/ServerAgent mocks, IPv4/IPv6/wildcard parsing, protocol
key separation, real bound/configured Linux UDP sockets, collector validation,
deduplication/CSV, UDP dashboard filtering and analyzer local-versus-peer rules.
Windows builds are compiled on Linux; actual Windows cmdlets, installation and
reboot behavior remain target-machine checks.
