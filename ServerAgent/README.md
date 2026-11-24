# Lightweight Windows server / domain controller agent

A standalone PowerShell 5.1 script with a Task Scheduler registration helper.
No MSI, GUI installer, .NET SDK/runtime installation, Python or service is needed
on the server. Uses built-in Windows PowerShell, CIM, Get-NetTCPConnection and Get-NetUDPEndpoint.
Target: Windows Server 2016 or newer with Windows PowerShell 5.1.

This module collects **TCP connection/process metadata and UDP endpoint metadata**, as requested.
It does not collect Security/System logs, AD logons, directory objects, credentials
or packet contents. It sends the same event format as the existing agent, so
collector SQLite columns, CSV exports and Listener_DB_GUI work without changes.

## Add it as a startup task

Copy this `ServerAgent` folder to the server. Open **Windows PowerShell as
Administrator** in that folder and run:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Register-ServerAgentTask.ps1 -CollectorHost collector.example.local -CollectorPort 6515
```

Use your collector's actual DNS name, without `http://` or `:port`. The port
must match the collector. For a specific PC identity or slower polling:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Register-ServerAgentTask.ps1 -CollectorHost collector.example.local -AgentId DC-01.example.local -PollSeconds 15
```

The helper copies the agent script/config to `%ProgramData%\WatcherServerAgent`,
restricts that directory to Administrators/SYSTEM, registers **Watcher-Server-Agent**
as SYSTEM with highest privileges, and starts it immediately. The startup
trigger runs it 30 seconds after each reboot. It runs continuously without a
time limit, rejects duplicate task instances, and restarts after a process
failure. No domain account password is stored. Domain Group Policy/script
signing requirements still apply; the process-level execution-policy argument
does not override enforced Group Policy.

Keep the protected ProgramData folder: the original downloaded folder can be
removed after registration. Re-running the helper updates this task and script,
backs up the previous configuration, and preserves the spool. An existing
custom AgentId is retained when omitted. CollectorHost remains required.

Do not run the GUI-installed Watcher agent and this module on the same machine
unless duplicate monitoring is intentional. This helper only manages its own
`Watcher-Server-Agent` task; it does not remove `Watcher-Connection-Agent`.

## Check delivery

On the server:

```powershell
Get-ScheduledTask -TaskName Watcher-Server-Agent
Get-ScheduledTaskInfo -TaskName Watcher-Server-Agent
Get-Content "$env:ProgramData\WatcherServerAgent\agent.log" -Tail 30
Test-NetConnection collector.example.local -Port 6515
Get-ChildItem "$env:ProgramData\WatcherServerAgent\spool" -Filter *.json | Measure-Object
```

Expect the task to be **Running** and logs to contain **Acknowledged** messages.
A continuously running task can report LastTaskResult 267009 (0x41301); that is
not an application failure. The PC appears in Listener_DB_GUI after the
collector accepts its first event. A blank collector IP allowlist permits any
reachable sender. The collector service must itself be running and listening.
An increasing spool means events are being generated but not acknowledged;
check DNS, collector status/port and firewall. The dashboard does not repair a
stopped collector service.

For one foreground cycle, first stop the scheduled task to avoid the spool lock:

```powershell
Stop-ScheduledTask -TaskName Watcher-Server-Agent
& "$env:ProgramData\WatcherServerAgent\Watcher-ServerAgent.ps1" -ConfigPath "$env:ProgramData\WatcherServerAgent\agent.json" -Once
$LASTEXITCODE
Start-ScheduledTask -TaskName Watcher-Server-Agent
```

`-Once` exits 1 on collection or delivery errors, 0 on success. If task shutdown
is still in progress, wait until its state is no longer Running before testing.
Normal continuous mode logs transient failures and keeps retrying.

## Behavior and files

- Polls established TCP connections every 5 seconds by default; combines IPs,
  ports and owning PID with process name/path. Excludes loopback peers 127.0.0.1
  and ::1.
- Sends `ACTIVE_AT_START`, `TCP_CONNECTED`, `TCP_DISCONNECTED`, and a heartbeat
  every 60 seconds. Polling may miss short-lived connections. A disconnection
  means the connection disappeared from the snapshot, not an observed FIN/RST.
- Writes UTF-8 JSON spool files with disk flush before attempting delivery.
  Sends newline-delimited JSON to the collector and removes each file only on
  a matching ACK. The collector deduplicates retried event IDs.
- Keeps up to 50,000 pending events and sends up to 1,000 per cycle. When full,
  preserves pending files, refuses new batches, logs the error and still tries
  delivery. Full spools cannot capture new history indefinitely.
- Spool writes are atomic per file, not for an entire batch. Partial disk write
  failures can cause logical duplicates on a later snapshot retry.
- Uses one exclusive spool lock to prevent concurrent manual/task instances.
  Logs rotate at 5 MB with one previous log retained.

```text
%ProgramData%\WatcherServerAgent\
    Watcher-ServerAgent.ps1
    agent.json
    agent.json.bak        (after updates)
    agent.log / agent.log.1
    spool\*.json
    spool\agent.lock
```

Configuration includes `collector_host`, `collector_port`, `agent_id`, absolute
`spool_dir`/`log_file`, `poll_seconds`, `heartbeat_seconds`, `max_spool_events`,
and `send_batch_size`. Restart the task after manual edits. Configure outbound
TCP to the collector. Like the existing protocol, transport is plaintext and
unauthenticated: use a trusted management network or protected VPN.

## Remove the task

Run as Administrator:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Unregister-ServerAgentTask.ps1
```

Stops and removes only this task. Script, config, logs and unsent events are
preserved in ProgramData. Removing those files later is a separate manual action.

## Validation

```bash
dotnet build tests/ServerAgentCollector -c Release
PWSH_PATH=/path/to/pwsh python tests/test_server_agent.py
```

Tests use PowerShell 7 with mocked Windows TCP/CIM commands and the real C#
collector on temporary local data. They check script parsing, event fields,
loopback exclusion, spool limits, offline buffering, DNS delivery, matching ACKs,
SQLite/CSV storage, replay deduplication and continuous connection transitions.
These tests do not execute Windows Task Scheduler or reboot a Windows server.
Validate startup and actual process visibility on a target Windows server before
rolling out to more servers/DCs.

## UDP endpoints

UDP is enabled by default (`collect_udp: true`, optional for older configs).
Windows records local IP/port and owning process, with unknown remote fields
left empty. New UDP_ENDPOINT_AT_START/OPENED/CLOSED events require the updated
collector. See [UDP.md](../Agent/UDP.md) before upgrading. Existing explicit
collect_udp choices survive registration updates. Set false and restart the
task for TCP-only collection. Delivery remains TCP with matching ACKs.

Queued events are transmitted with their original JSON values. PowerShell date parsing is used only while inspecting the event ID; delivery does not reserialize timestamps, so UTC is preserved even when the machine uses a non-UTC time zone.

The ServerAgent also reports complete TCP/UDP snapshots on each configured heartbeat. Upgrade the Collector first to accept `TCP_SNAPSHOT` and `UDP_SNAPSHOT`. The dashboard reconstructs the latest reported sockets from a complete snapshot plus later open/close events. A zero-socket completion clears old state after restart. Custom heartbeat intervals longer than two minutes can make the dashboard's freshness indicator show stale between snapshots.

## Device category

Registration asks for the device category when no saved choice exists. For unattended deployment, add `-DeviceCategory windows_servers` (also accepts `linux_servers`, `user_pcs`, `project_stands`). Updates preserve the saved category unless explicitly overridden. Runtime configurations without the field continue as `legacy` for compatibility. Queued events keep their original category. Update the Collector first; see [device storage](../DEVICE-STORAGE.md).

## Replaying offline data

The ServerAgent retains unacknowledged spool files and retries when the Collector is reachable, preserving original UTC timestamps. Slow replay batches now yield between acknowledged events after roughly five seconds; the next collection iteration continues before another batch. In-flight connection/read timeouts still apply. The spool is bounded by `max_spool_events`; full capacity stops new collection rather than deleting unsent evidence. Delayed heartbeats appear orange after receipt in the updated Collector/GUI; see [timeline semantics](../Listener_DB_GUI/README.md#orange-working-periods-after-offline-replay).

## Network/process context

Registration copies `Shared/NetworkContext.ps1` beside the installed agent. Each heartbeat samples active adapters and running processes/services and sends context events through the existing spool. Update the Collector first, then re-register this agent from the complete repository layout. See [network context fields and limitations](../Listener_DB_GUI/README.md#device-network-communications-and-running-processes).

## Pipelined VPN delivery

The updated ServerAgent negotiates pipeline support on its first ordinary event, then sends groups of up to 64 events/128 KiB before reading per-event durable ACKs. Legacy Collectors receive sequential traffic automatically; old timestamps and unacknowledged files are preserved. Update the Collector first, then re-register the updated ServerAgent while retaining its spool. Existing five-second replay yielding and configurable `send_batch_size` limits remain. See [VPN backlog behavior](../Agent/README.md#vpn-backlog-pipelined-delivery).

## Estimated incoming/outgoing direction

Re-register the current script after updating the Collector to retain TCP listener-based
`CONNECTION_DIRECTION`/`DIRECTION_BASIS`. These are estimates, kept for an observed
socket's lifetime. Missing listener visibility, older reports and UDP remain Unknown.
See [independent connection analyzer setup and semantics](../Listener_ConnectionAnalyzer/README.md).
