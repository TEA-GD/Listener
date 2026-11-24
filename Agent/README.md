Watcher V6 Windows Agent installer includes Install / Update and Uninstall Agent buttons. See ../README.md for instructions.

TCP and UDP socket monitoring: see [UDP.md](UDP.md) for available fields, limitations and upgrade order.

For the device overview, the agent now queues a full TCP/UDP snapshot on startup and on every heartbeat (normally once a minute). A completion heartbeat declares the expected row count; partial deliveries are not displayed as complete. Upgrade the Collector before installing this agent, because `TCP_SNAPSHOT` and `UDP_SNAPSHOT` are new event types. Existing open/close monitoring and ACK-based retries are retained. Snapshot records increase stored event/CSV volume.

## Updating without uninstalling

Download the current repository, rebuild `Agent/build/Build-Windows.bat` with .NET 10 SDK, and run the newly built `Agent/output/windows-installer/WatcherInstaller.exe` elevated. Select **Install / Update**. Reusing an older installer executable will reuse the old update behavior.

The installer repairs ownership and permissions on its ProgramData directory and existing `agent.json`, removes the config's read-only attribute, and grants access only to SYSTEM and Administrators. It recovers access before loading existing settings, so a permissions error does not silently replace the saved configuration with defaults. Updates preserve unknown config settings and any custom `spool_dir`; pending events are not deleted. Only the installed WatcherAgent process is stopped, and complete staged files replace the binary and configuration atomically. If an update fails after stopping the agent, the installer attempts to restart the existing scheduled task. A failed staging/replacement leaves that file's previous contents intact.

Do not uninstall or select data deletion to work around `agent.json` permissions. The updated installer handles the normal legacy ACL/read-only cases in place. A genuinely corrupt configuration, linked install path, security product block or unrecoverable permissions failure is reported rather than overwriting unreadable settings.

Regression checks:

```powershell
dotnet run --project tests/InstallerUpdate -c Release
```

Run from the repository root in an elevated Windows terminal to exercise the actual explicit-deny NTFS ACL and sharing-lock checks. Portable preservation/replacement checks also run on Linux; Linux cannot validate the Windows ACL repair itself.

## Launch and stop controls

The Agent installer now has **Launch Agent** and **Stop Agent** buttons alongside Install / Update and Uninstall Agent, matching the Collector's start/stop workflow.

- **Launch Agent** starts the existing `Watcher-Connection-Agent` scheduled task with the installed executable and saved `agent.json`. It does not apply unsaved setup-field edits. An already-running installed agent is left running, and the log reports whether the process could be confirmed. Starting the process does not confirm Collector delivery.
- **Stop Agent** ends the startup task's current run and stops only a WatcherAgent process whose executable path matches the installed agent. It keeps the task registered, preserves configuration and unsent events, and verifies that the process stopped. The agent can start again with Launch Agent or at the next Windows startup.

Commands run asynchronously, with other installer actions disabled until completion. Use the rebuilt installer elevated; an agent must already be installed before Launch Agent can work. These controls do not uninstall the agent or change monitoring settings.

Portable lifecycle regression checks: `dotnet run --project tests/AgentControls -c Release`. Actual Task Scheduler/process control and button layout require verification on a Windows test PC.

## Device category

Setup now requires a **Device type** selection: Linux servers, Windows servers, User PCs, or Project Stands. Updates load the saved choice; older configurations without a choice require selection once. Future events are tagged with that category before entering the spool. Updates preserve queued events with their original category. Update the Collector first. See [device storage and migration](../DEVICE-STORAGE.md).

## Notebooks: run on battery power

The rebuilt Windows installer explicitly sets `DisallowStartIfOnBatteries=false`, `StopIfGoingOnBatteries=false` and `ExecutionTimeLimit=PT0S`. The agent can start while unplugged, remains running when AC power is removed, and has no 72-hour task limit. It runs as elevated SYSTEM at startup without a user login, permits on-demand launch, ignores duplicate instances, and retries failures every minute (up to 255 retries). It does not wait for idle or network availability; queued events retain the existing offline buffering behavior.

Older installer EXEs created the task with Windows defaults, which could leave it **Queued** on battery. Download the updated sources, run `Agent/build/Build-Windows.bat`, then run `Agent/output/windows-installer/WatcherInstaller.exe` as Administrator and choose **Install / Update**. This replaces the task settings for an existing installation while retaining saved configuration/custom spool settings and unsent events. **Launch Agent** alone starts the saved task and does not repair its settings; use **Install / Update** once to apply this fix. Uninstalling is unnecessary.

Verify on a Windows test notebook after updating:

```powershell
(Get-ScheduledTask -TaskName 'Watcher-Connection-Agent').Settings |
    Format-List DisallowStartIfOnBatteries, StopIfGoingOnBatteries, ExecutionTimeLimit
Get-ScheduledTask -TaskName 'Watcher-Connection-Agent' | Select-Object TaskName, State
```

Expected settings are `False`, `False`, `PT0S`. Start the task on battery, unplug/reconnect AC, close the setup window, and confirm it stays `Running` and delivers/buffers events. Windows sleep, hibernation and shutdown still suspend monitoring; these settings do not keep the notebook awake.

Portable task-definition checks: `dotnet run --project tests/AgentTaskDefinition -c Release`. The generated XML was also checked against the [Microsoft Task Scheduler schema](https://learn.microsoft.com/en-us/windows/win32/taskschd/task-scheduler-schema). Actual Task Scheduler execution and power transitions require Windows.

## Offline delivery and timeline

Unacknowledged events stay in the local spool across Collector/network outages and agent restarts. Reconnection retries the original payloads; only matching durable Collector ACKs delete files. The compiled Agent uses an independent upload worker on Windows and Linux: sampling continues during slow sends, and the worker starts another bounded pass immediately while backlog remains. The default spool limit is 50,000 events; a full spool preserves pending files and refuses new collection. The updated Collector/GUI can mark delayed working periods orange after these heartbeats arrive; see [orange timeline semantics and upgrade order](../Listener_DB_GUI/README.md#orange-working-periods-after-offline-replay).

## Minute network/process context

Each heartbeat also samples active network adapters and running processes/services, correlating them by device and snapshot ID. Deploy the updated Collector before this agent. Windows embeds the shared metadata-only PowerShell sampler; Linux uses interface/process metadata and optional NetworkManager/systemd labels. See [fields, limits, privacy and GUI deployment](../Listener_DB_GUI/README.md#device-network-communications-and-running-processes). Context shares the existing finite spool and ACK delivery, increasing disk/database volume.

## VPN backlog: pipelined delivery

Update the Collector first, then rebuild/update the agent using **Install / Update**. Keep the existing spool and configuration. Updating only the agent is safe, but an older Collector keeps the sequential delivery speed.

The first ordinary event requests pipeline support. An updated Collector acknowledges that committed event with `ACK <event-id> PIPELINE64`; subsequent groups can contain up to 64 events or 128 KiB of encoded data. All events retain their original timestamps and IDs. Each file is deleted only after its own matching durable ACK. Rejection/disconnection leaves unacknowledged files for replay; duplicates remain idempotent. Existing Collectors simply return the original ACK, which makes the new agent use sequential delivery. Old agents never request the extension and continue receiving unchanged ACKs.

The sender limits each pass to 1,000 files. Its independent background worker immediately starts the next pass while files remain, without waiting for the collection interval or yielding after five seconds. Collection and minute network/process snapshots run separately. Network latency and Collector commit speed still limit throughput.

After upgrading, check the pending count several times over a few minutes. Expect brief increases when minute snapshots are queued, with an overall downward trend while catching up. Actual throughput still depends on VPN bandwidth, Collector disk/commit speed and event production. Do not delete queued files or reinstall to clear the backlog. The 50,000-event spool limit remains in effect.

## Local attachment mail bridge

The compiled Agent now optionally accepts attachment-only Thunderbird metadata on loopback HTTP, then forwards it through the same durable spool/Collector ACK path. Configure `mail_token` (at least 32 characters) and optionally `mail_port` (default 58766); an empty token disables the endpoint. Keep the spool when updating. See [single add-on setup and destructive legacy-mail migration](../Thunderbird_Attachments/README.md). No message subjects, bodies or attachment contents are accepted. The separate PowerShell ServerAgent does not provide this endpoint.

## Independent upload worker (Windows and Linux)

Continuous mode now runs one background uploader alongside the existing sampling loop.
It starts with any saved backlog, sends complete published `.json` files in filename order,
and immediately starts another pass after each 1,000-file pass. Collection and the local
mail bridge wake it when new files are ready; an idle one-second rescan also catches
published files. Connection, read and write failures retain unacknowledged files and
retry after five seconds. Slow network I/O no longer delays socket polling. Platform
sampling commands and disk pressure can still delay sampling.

The protocol is unchanged: optional `PIPELINE64`, at most 64 events/128 KiB per group,
original IDs/timestamps, and deletion only after each matching durable ACK. Validation
and per-event Collector commits remain enabled. There is no priority bypass for recent
events: FIFO replay can still delay current status until the backlog catches up.

Only one compiled Agent can hold a given spool's `agent.lock`. A manual second launch
with the same spool fails instead of competing with the installed task/service. The
lock file may remain after exit; the OS releases its handle, so do not delete it to
work around a running Agent. Queue publication and mail writes remain serialized;
uploading never holds that write lock while waiting on the network. The 50,000-event
capacity and refusal to discard pending events are unchanged.

Ctrl+C and Linux SIGTERM cancel pending network I/O and stop the worker. Forced Windows
task termination or process crashes leave unacknowledged files for replay. `--once`
remains a foreground diagnostic: collect once, attempt one pass of up to 1,000 files with the existing five-second
soft budget between groups, then exit; an in-flight group still uses network timeouts.
It does not run a continuous uploader.

Deploy by rebuilding/updating the Windows installer with **Install / Update**, or by
installing the rebuilt Linux **1.0.4** package. Existing configuration and spool are
retained; no new settings, Collector update or database migration is needed for this
worker. The existing updated Collector is still needed for all current event types.
The separate PowerShell ServerAgent retains its existing sender loop.

Validation:

```bash
dotnet run --project tests/AgentIntegration -c Release
# After publishing the Linux Agent:
python tests/test_upload_worker.py
```

Tests cover continuous passes beyond 1,000 events, publishing while an ACK is stalled,
legacy/pipelined receivers, cancellation, replay with unchanged payloads, ignored `.tmp`
files and actual process-level sampling/queue locking on Linux. Windows Task Scheduler
and power transitions still require checks on a Windows test device.
