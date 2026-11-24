# Independent connection analyzer

`Listener_ConnectionAnalyzer` is a separate .NET 10 application for Windows/Linux.
It reads the Collector `events` view **read-only** and maintains its own SQLite
`connection-analysis.db`. The GUI's **It's time to analyze** button opens its results.
It is separate from `Listener_Analyzer`, which retains its existing behavioral alerts.
Stopping the GUI does not stop this module. Stopping the module leaves its last results
readable; the analysis page shows the last scan time and learning progress.

## Views and first-seen rules

- Incoming and Outgoing show unique connection identities per device: peer IP,
  protocol, service port, process name and executable path. Incoming uses the local
  listening port; outgoing uses the peer port. Temporary client ports and PIDs are
  excluded from identity. The stored local IP is the latest observed local address.
- TCP direction is **estimated**, using a matching local listening endpoint and its
  owner where available. A listener match means estimated incoming; a successful
  listener query with no match means estimated outgoing. Estimates are retained
  for an observed socket's lifetime. This is not SYN/accept tracing or proof of
  initiation; proxies, port reuse and listener races can affect the result.
- Historical records without direction, failed listener queries, and UDP peers
  are Unknown. Unknown identities omit service port rather than treating remote
  temporary client ports as new services. Unconnected UDP bindings have no peer
  and are excluded; they do not establish communication.
- **Connections first seen on this day** checks the earliest event time across
  directions for that device and identity. Earlier Unknown observations of the
  same peer/protocol/process conservatively prevent a known connection from being
  called new, because its historical service port cannot be established.
- **Peers first seen on this day** checks whether that IP appeared on the device
  in *any* direction before that day, regardless of ports, protocol or process.
  This is the strict view for “never seen incoming or outgoing before today.”
- Day boundaries use the selected time zone (browser zone by default), including
  DST changes. A date selector also allows reviewing an earlier first-seen day.
- Snapshots and disconnects prove prior observation too. Counts are observations,
  not sessions/visits. Original timestamps determine history; delayed uploads can
  move a first-seen date backwards. First-seen results are provisional during
  backfill and always limited to the available retained source history.

The module incrementally scans source sequence IDs in batches. The profile update
and cursor commit together, so restart does not recount rows. A source-path/anchor
check refuses an unexpected replacement/rewritten source instead of silently
merging unrelated history. Preserve the old state and choose a new analysis DB to
rebuild after an intentional source replacement. Do not edit derived tables manually.

## Database

The independent DB contains `analysis_connections` and `analysis_meta` (schema,
source identity, cursor/anchor, scanned watermark and last processed UTC time).
Indexes support device/direction, peer and identity lookups. No mail data or packet
content is copied. The source DB is never migrated by this module. Keep both DBs
on local disk; do not expose a live SQLite DB over a network filesystem.

## Upgrade order

1. Back up the Collector consistently, then update it. It adds
   `CONNECTION_DIRECTION`/`DIRECTION_BASIS` to all event partitions and recreates
   the unified view transactionally, preserving existing IDs/history. These fields
   also appear in newly exported CSV files. Existing data remains unclassified.
2. Update compiled Windows/Linux Agents, or re-register the updated PowerShell
   ServerAgent, to send new direction estimates. Linux Agent package: **1.0.5**.
   Existing spools/configuration are retained. Older agents still work as Unknown.
3. Build/start this module and update the GUI. The main build script now includes
   the connection analyzer. Older Collectors can be analyzed, but cannot retain
   direction fields; upgrade to populate Incoming/Outgoing for new observations.

## Windows

Build with `Build-Windows.bat` in this directory (.NET 10 SDK on the build machine).
Copy the whole `output/windows` folder to its own stable application directory.
Copy `config.example.json` as `config.json` beside the deployed executable and edit:

```json
{
  "database_path": "C:\\ProgramData\\WatcherCollector\\watcher-events.db",
  "analysis_database": "C:\\ProgramData\\WatcherConnectionAnalyzer\\connection-analysis.db",
  "poll_seconds": 5,
  "batch_size": 1000
}
```

Run independently, with permission to read the Collector DB and create/write the
analysis directory. The Collector's default ACL normally requires an Administrator
or an explicit read grant. With the source tree, `Start-Windows.bat` is provided;
with the deployed publish folder, run:

```powershell
.\Listener_ConnectionAnalyzer.exe --config .\config.json
# One pass through all currently available records, then exit:
.\Listener_ConnectionAnalyzer.exe --config .\config.json --once
```

Keep the continuous process running (or deploy it under your existing task/service
management). Only one instance may use a particular analysis DB. The GUI's default
analysis path matches the example. For another location:

```powershell
.\Listener_DB_GUI\Start-Windows.ps1 -AnalysisDatabasePath 'D:\Listener\connection-analysis.db'
```

`Launch-Windows.ps1` accepts the same optional parameter. The GUI account needs read
access to the analysis DB, its parent directory and SQLite WAL/SHM files. The GUI
reads results; it does not start the analyzer or write to its tables.

## Linux

Build on a compatible Linux amd64 machine with .NET 10 SDK:

```bash
bash Listener_ConnectionAnalyzer/build-linux.sh
```

Run from a deployment/config directory using absolute paths in `config.json`:

```json
{
  "database_path": "/var/lib/watcher-collector/watcher-events.db",
  "analysis_database": "/var/lib/watcher-connection-analyzer/connection-analysis.db",
  "poll_seconds": 5,
  "batch_size": 1000
}
```

The supplied systemd unit assumes the `watcher-collector` account from the Linux
Collector guide. Change the account/paths if your deployment differs:

```bash
sudo install -d -m 755 /opt/listener-connection-analyzer
sudo cp -a Listener_ConnectionAnalyzer/output/linux/. /opt/listener-connection-analyzer/
sudo chown -R root:root /opt/listener-connection-analyzer
sudo install -d -o root -g watcher-collector -m 750 /etc/watcher-connection-analyzer
# Save the Linux JSON above as /etc/watcher-connection-analyzer/config.json.
sudo chown root:watcher-collector /etc/watcher-connection-analyzer/config.json
sudo chmod 640 /etc/watcher-connection-analyzer/config.json
sudo install -m 644 Listener_ConnectionAnalyzer/listener-connection-analyzer.service /etc/systemd/system/
sudo systemctl daemon-reload
sudo systemctl enable --now listener-connection-analyzer
sudo journalctl -u listener-connection-analyzer -n 30 --no-pager
```

For the GUI, set `WATCHER_ANALYSIS_DB_PATH` to that analysis DB path in its process
or service environment, alongside its existing `WATCHER_DB_PATH`. Grant its account
read access to the directory/database/WAL/SHM as appropriate. An unavailable analysis
DB produces a setup message on the analysis page; the rest of the dashboard works.

Stop/remove the systemd unit to uninstall the module. Configuration and the analysis
DB remain until explicitly removed; the Collector database is unaffected.

## Validation

```bash
dotnet build Listener_ConnectionAnalyzer -c Release
dotnet build Listener_DB_GUI -c Release
python tests/test_connection_analysis.py
dotnet run --project tests/DatabaseIntegration -c Release
dotnet run --project tests/AgentIntegration -c Release
```

The integration suite checks read-only source access, restart/cursor protection,
late historical replay, cross-direction and per-device novelty, time zones,
Unknown fallback, continuous processing and browser filters/escaping. Agent checks
include real Linux local-listener/client sockets and mocked Windows cmdlet queries.
Windows Task Scheduler and live Windows collection require a target Windows check.
