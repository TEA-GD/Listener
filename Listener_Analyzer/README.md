# Listener_Analyzer — device connection profiles and shared-file alerts

A separate .NET 10 console project. It polls the existing collector SQLite
`events` table read-only and builds persistent profiles in its **own** SQLite
state database. It works with Windows, Linux, servers/DCs or any other device
whose connection events already reach the collector. It does not discover or
monitor devices that send no events.

The chosen delivery method is a shared alert file, not Telegram or another
messenger. Nothing is sent until you run the analyzer with your configured
output location. There are no bot tokens or account passwords in this module.

## What it learns

Each monitored host has first/last observation, OS and observation counts.
Profiles also track:

- Every destination IP and the UTC days on which it was observed.
- Destination IP + destination port + protocol + process name + executable path
  combinations, their first/last observations, counts and observed days.
- Raw source context in each alert: source/destination IPs and ports, PID,
  process/path, OS, original event ID and all other collected fields.

Ephemeral source ports and PIDs are retained in alert evidence but do not form
baseline keys; otherwise ordinary new sockets would constantly look unusual.
Hosts are identified by `MONITORED_HOST`, so every agent needs a unique agent ID.
Domains/full browser URLs are not newly collected by this module.

## Alert rules

The default learning span is **7 days of stored event-time history per device**.
The initial database snapshot is learned without historical alerts. New source
rows after that snapshot can alert once the device has enough history.

| Reason | Meaning |
| --- | --- |
| `new_destination` | No prior destination observation exists in the learned profile. |
| `returned_after_gap` | Destination was last observed at least 30 elapsed days earlier. |
| `rare_destination` | Destination was observed on at most 2 distinct UTC days in the preceding 30 calendar dates. |
| `new_port_or_process_combination` | Destination is known, but this port/protocol/process/path combination is new. |
| `rare_port_or_process_combination` | An existing combination appeared on at most 2 prior UTC days in that window. |
| `returned_port_or_process_after_gap` | An existing combination was last observed at least 30 elapsed days earlier. |

This catches, for example, a server that contacts an IP on only one or two days
per month, or a rare process contacting an otherwise familiar IP. Rules are
configurable and their reasons/evidence are included in each alert.

Only `TCP_CONNECTED` and `ACTIVE_AT_START` trigger alerts. Disconnections also
teach the profile that a destination/combination was observed. Heartbeats update
device presence but never generate connection alerts. Multiple observations do
not mean separate website visits: TCP metadata cannot establish request counts.

A 24-hour cooldown applies per device and full connection combination. Multiple
rule matches produce one alert containing multiple reasons. New ports/processes
can produce separate alerts for the same IP. Old queued records and out-of-order
records update profiles without issuing stale alerts; default maximum alert age
is 24 hours, and timestamps over 5 minutes into the future do not alert.

This is explainable, rule-based profiling, not a threat verdict. Short monitoring
history, clock errors, outages, long-lived sockets, dynamic/CDN IPs and software
updates affect results. The profile is based on observed records, not proof of
all traffic. Profiles do not currently use an allowlist or automatic retention.

## Install/run on Windows

Recommended: run on the collector PC so SQLite stays on its local disk.

1. Build with `Build-Windows.bat` (requires .NET 10 SDK/NuGet access), or publish
   `Listener_Analyzer.sln` in Visual Studio.
2. Copy `output/windows/` to a stable local application directory. The published
   executable is self-contained. Keep all published files together.
3. Copy `config.example.json` to a separate writable deployment directory as
   `config.json`, then edit the paths and thresholds.
4. Run the executable using an account with read access to the collector database,
   write access to its local state directory, and read/write access to the alert
   share. The collector's ProgramData ACL normally requires an Administrator or
   an explicit read grant to that account.

Example configuration for a domain share:

```json
{
  "database_path": "C:\\ProgramData\\WatcherCollector\\watcher-events.db",
  "state_directory": "C:\\ProgramData\\WatcherAnalyzer",
  "alerts_file": "\\\\fileserver.example.local\\WatcherAlerts\\connections.jsonl",
  "poll_seconds": 10,
  "baseline_days": 7,
  "rarity_window_days": 30,
  "rare_max_active_days": 2,
  "long_gap_days": 30,
  "cooldown_hours": 24,
  "maximum_alert_age_hours": 24,
  "batch_size": 500
}
```

Create the Windows share yourself and grant the analyzer identity permission to
create/read/write the alert file. Give consuming domain users read access to the
share and NTFS directory. This program does not create shares, grant domain
permissions or store credentials. The alert file contains internal network and
process metadata; choose its readers accordingly.

Start manually:

```powershell
& 'C:\Watcher\Analyzer\Listener_Analyzer.exe' --config 'C:\Watcher\AnalyzerConfig\config.json'
```

Or use `Start-Windows.bat` in the source module folder after creating its
`config.json`. Paths inside the configuration are resolved relative to that
configuration file. Ctrl+C stops the foreground analyzer. The first startup
backfills history; new devices may still be warming up even when backfill ends.

### Start after reboot

Use Windows Task Scheduler > Create Task:

- Use a dedicated account with the required database/state/share permissions;
  select Run whether user is logged on or not and a trigger At startup.
- Program: full path to `Listener_Analyzer.exe`.
- Arguments: `--config "C:\Watcher\AnalyzerConfig\config.json"`.
- Set restart on failure, disable the execution time limit, and select Do not
  start a new instance when already running.
- Protect the program and configuration paths from modification by untrusted
  users. Keep state on the local disk and use one analyzer per state directory.

If running as SYSTEM, access to a remote domain share normally uses the machine
account, e.g. `DOMAIN\COLLECTOR-PC$`. Grant that identity the needed permissions,
or use the intended domain service account. Mapped drive letters from your
interactive session may not exist in the scheduled task: use a UNC path.

## Read alerts from a domain PC

Open the shared file in a reader, or monitor messages in PowerShell:

```powershell
$seen = [Collections.Generic.HashSet[string]]::new()
Get-Content '\\fileserver.example.local\WatcherAlerts\connections.jsonl' -Wait |
    ForEach-Object {
        try {
            $alert = $_ | ConvertFrom-Json
            if ($seen.Add($alert.alert_id)) { $alert.message }
        } catch { Write-Warning 'Incomplete alert line; inspect the shared log.' }
    }
```

Each line is one JSON alert with stable `alert_id`, `event_id`, device, destination,
process, reasons and original event fields. This is shared-file delivery, not a
Windows toast/pop-up or messenger push notification. Consumers decide how to
present the alert. Delivery is **at least once**: a crash after file flush but
before marking it sent may repeat an `alert_id`; deduplicate by that ID. An
interrupted append may leave an invalid partial line; retries start on a new
line, and consumers should skip malformed lines while retaining valid records.

Use a dedicated file for this analyzer. Writes take an exclusive writer handle;
readers must allow writes. File/share failures leave alerts in the local outbox
and are retried. Successfully sent alert payloads also remain in the local state
DB for audit. Plan retention/backups for state and alert files; no rotation or
pruning is currently automatic.

## Linux or source execution

The same project can run on a .NET-10-supported Linux machine with a local
collector-format SQLite file and a writable mounted share:

```bash
bash Listener_Analyzer/build-linux.sh
Listener_Analyzer/output/linux/Listener_Analyzer --config /etc/listener-analyzer/config.json
```

Use Linux paths for database/state/output. A systemd service can run this command
under an account granted those permissions. For self-contained single-file builds,
ensure the runtime's native-library extraction directory is writable (for a
restricted service, set `DOTNET_BUNDLE_EXTRACT_BASE_DIR` to a writable local
subdirectory). Windows startup scripts do not run on Linux.

Keep the live SQLite DB local to its writer. Do not open an actively written
SQLite/WAL database over SMB. To run elsewhere, use a coherent SQLite backup or
replica, preserving the expected append history; copying only a live `.db`
without its WAL is not a reliable replica. Shared alerts, unlike SQLite state,
are ordinary file output and may use SMB. Never share the analyzer's state DB
between multiple running machines.

## Reports, state and recovery

```powershell
.\Listener_Analyzer.exe --config .\config.json --once --export-profiles .\profiles.json
```

This processes all currently available records, attempts delivery and exports
profiles as JSON (devices, combinations, daily history and rule thresholds).
Exit codes: 0 success, 1 setup/source error, 2 processing completed but alert output
is still pending. To test sooner on synthetic data, lower `baseline_days`; 0
still needs a prior device observation. Deleting state causes full relearning
and suppresses existing rows as a new initial snapshot.

`state_directory/profiles.db` stores learned profiles, cursor, cooldowns and the
durable outbox. Source row processing, cursor advancement and outbox insertion
commit together. A process lock prevents two instances using the same directory.
The collector database is never modified. A changed source path or missing/changed
cursor anchor stops processing, rather than silently ignoring a restored or
rewritten database. Preserve the old state for audit and use a fresh state
directory to bootstrap a deliberately replaced database. Back up the state DB
with its WAL/SHM consistently before changing or removing it.

The console logs ingestion progress and share failures. Review `profiles.json`
first/last observations when no alerts appear: insufficient baseline, cooldown,
age/order rules, or no unusual pattern can legitimately produce zero alerts.

## Validation

```bash
dotnet build Listener_Analyzer/Listener_Analyzer.sln -c Release
python tests/test_analyzer.py
```

Tests use temporary synthetic source databases, the real analyzer executable and
local file output. They check historical warm-up, Windows/Linux device isolation,
new/rare/returned combinations, usual-traffic suppression, cooldown/restart
behavior, delayed records, source replacement protection, unavailable-share-path
retry, profile export and unchanged source DB bytes. Real SMB permissions,
Windows startup and sustained production-volume performance remain deployment
checks. No alerts are sent to real recipients during development tests.
