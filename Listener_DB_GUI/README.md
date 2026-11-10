# Listener_DB_GUI

A separate .NET 10 web dashboard for the Watcher collector's SQLite database.
Open the PC list, choose a computer and explore its recorded activity.
The collector and agents continue to operate independently of this project.

## What it shows

- PCs grouped by `MONITORED_HOST`, with event counts and last collector receipt.
- Per-PC event, process, destination and TCP-connected event counts.
- Top 10 event types, processes, destination IPs and destination ports.
- UTC date filters, event-type filters and search by process, IP, destination port or event ID.
- Paginated events (50 per page) and a detail dialog containing all 19 event fields plus receipt time.
- Manual refresh, empty states and database/configuration error messages.

Counts describe recorded events in the selected filters, not currently active
sockets. The PC list contains historical PCs, not a live online/offline inventory.
Agent-supplied host identities are not authenticated. No demo data is shown in
the application; an empty database gives an empty PC list.

## Windows: build and open

1. Download the latest repository source. Install the .NET 10 SDK and enable the
   NuGet source `https://api.nuget.org/v3/index.json` for building.
2. Run `Listener_DB_GUI/Build-Windows.bat`, or open
   `Listener_DB_GUI.sln` in Visual Studio and publish the web project.
3. Run `Listener_DB_GUI/Start-Windows.bat` on the collector PC. The collector
   protects ProgramData for Administrators/SYSTEM, so run the launcher as
   Administrator if access is denied.
4. Keep the console open and open `http://127.0.0.1:5080` in a browser.
5. Click a computer on the left, then use filters or View on an event.

The launcher uses the published executable if available, otherwise runs the
source project with the SDK. The build output is in `output/windows/` and is
self-contained. When copying it, keep the **whole output folder**, including
`wwwroot`, together; copying only the EXE omits the web page files.

The default database is `%ProgramData%\WatcherCollector\watcher-events.db`.
For a custom collector database path or dashboard port:

```powershell
.\Start-Windows.ps1 -DatabasePath 'D:\Watcher\watcher-events.db' -Port 5081
```

This dashboard port is HTTP; it is separate from the collector's TCP ingestion
port 6515. The launcher intentionally listens only on the local PC.

## Run from source (Windows/Linux)

From the repository root:

```powershell
$env:WATCHER_DB_PATH = 'C:\ProgramData\WatcherCollector\watcher-events.db'
dotnet run --project Listener_DB_GUI -c Release
```

Linux equivalent:

```bash
WATCHER_DB_PATH=/path/to/watcher-events.db dotnet run --project Listener_DB_GUI -c Release
```

The database can also be specified with ASP.NET configuration `--DatabasePath`.
`WATCHER_DB_PATH` takes precedence. The default URL is `http://127.0.0.1:5080`.
No Node.js or Python is needed to run the dashboard.

## Database compatibility and access

The GUI uses `Mode=ReadOnly` and `PRAGMA query_only=ON`. It never creates a
missing database, migrates schemas, writes events or deletes records. It requires
the collector schema with individual event columns. For an older `payload_json`
database, back it up and start the updated collector once to migrate it.

Use the live database on the collector PC. With SQLite WAL, the viewer account
also needs access to the database directory and the existing WAL/SHM sidecars.
Do not copy just the main database while the collector is writing: use a proper
SQLite backup, or stop the collector before copying the database and sidecars.
The viewer opens short read connections while the collector continues writing.
The query results of a refresh may reflect slightly different instants as new
events arrive.

The dashboard has **no login** and defaults to loopback. It displays internal
network/process metadata; do not expose it directly to an untrusted network.
For access from other PCs, put it behind an authenticated HTTPS reverse proxy
with appropriate firewall controls rather than opening the dashboard publicly.

## Validation

```bash
dotnet build Listener_DB_GUI/Listener_DB_GUI.sln -c Release
python tests/test_dashboard.py
```

Tests require Python and Playwright (`pip install playwright`, then
`python -m playwright install chromium`). An existing system `chromium` is used
when available; `PLAYWRIGHT_CHROMIUM_EXECUTABLE` can select another compatible
browser binary. Tests create temporary synthetic databases, run the real HTTP
server, verify filters/counts/pagination, error handling and unchanged database
contents, and navigate the UI in Chromium. They never use production data.

## New & rare connections board

Select a PC, then use **New & rare connections**. Choose a UTC day and a minimum
absence threshold (default 30 days; configurable from 1 to 36,500). The initial
day follows the selected PC's latest stored event. The board is independent of
the event-history filters above it.

Each row is a unique destination IP for that PC/day, across ports and processes:

- **New in history**: no earlier observation of this IP is stored for this PC.
- **Returned after gap**: the first positive observation that day occurred at
  least the configured number of elapsed 24-hour days after its last earlier
  observation. The displayed gap is rounded; classification uses the unrounded
  timestamps. Exactly 30 days qualifies for a 30-day threshold.

Candidates come from `TCP_CONNECTED` and `ACTIVE_AT_START` events. Earlier
`TCP_DISCONNECTED` events also count as evidence of prior observation, so a
recently observed disconnect prevents a false long-gap classification. Heartbeat
and disconnect-only destinations do not create candidates. Multiple observations
on the selected day do not erase the first observation's classification. Future
events and another PC's history do not affect it. Arrival order is ignored;
comparison uses event timestamps in UTC.

The board shows first/previous observations, gap, ports, processes and positive
observation counts, with 50 destinations per page. **Show events** sets the
history's exact destination-IP filter and UTC day, including any disconnects.

Results describe **retained observed history**, not proof of previously absent
network traffic, malicious behavior or continuous inactivity. New deployments,
data deletion, agent downtime, long-lived connections between snapshots, and
incorrect endpoint clocks can affect classifications. No persistent alert state
or database writes are created; results are recomputed on refresh.
