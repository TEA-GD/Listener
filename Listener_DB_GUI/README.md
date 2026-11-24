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
3. Run `Listener_DB_GUI/Launch-Windows.bat` (or `Start-Windows.bat` without arguments) on the GUI/collector PC. Choose **Local only** or **Domain network**, the database path, and port. Run as Administrator if the protected database requires it.
4. Click **Start GUI**. Keep the new server console open. Use the displayed local URL, or share the server URL in Domain mode.
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

The dashboard defaults to loopback and has **no Windows/AD sign-in**. Domain mode makes it reachable over the network and can configure a Windows Domain-profile firewall rule; that profile does not check each visitor's AD account. For account-based access, use an HTTPS reverse proxy with Windows Authentication. Mail endpoints retain their separate viewing-key requirement; use HTTPS for remote mail viewing.

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


## UDP events

Protocol and Local port columns, protocol/event-type filters and local-port
statistics support UDP endpoint observations. Known configured peers can appear
on the New & rare connections board; peerless bindings cannot. UDP endpoint
counts are event counts, not live sockets or packet counts. See
[UDP behavior and upgrade order](../Agent/UDP.md).


## Device overview: rate, addresses and current reported connections

Click a device to see a 60-minute chart of **new TCP connection events per minute**, its observed local IPs, average/peak opens per minute, and a paged list of reported TCP connections and UDP endpoints with process/PID, local address/port and remote peer address/port. The table and chart refresh every ten seconds while the tab is visible, independently of historical event filters. Hover/focus a chart bar for its counts, or expand the minute-by-minute table. All chart times are UTC; the current minute is partial.

The rate counts `TCP_CONNECTED` events, not packet throughput or simultaneous active sockets. Startup observations and periodic snapshots are excluded so agent restarts do not inflate the rate. Connections shorter than the agent polling interval can be missed. Missing reports can produce zero recorded opens; zero does not prove the device was online. UDP opens are available in the expanded counts table.

**Upgrade Collector first, then Agent/ServerAgent, then rebuild/restart the GUI.** Agents send `TCP_SNAPSHOT`/`UDP_SNAPSHOT` records on each heartbeat (normally 60 seconds), followed by a `HEARTBEAT` containing `STATE=SNAPSHOT_COMPLETE:<count>`. The GUI uses a snapshot only when every expected row has arrived, then replays later open/close events in event-time order. Empty complete snapshots clear old state, including after a restart. Older agents show a request to upgrade rather than a misleading live list. The new snapshot event types require the updated Collector; older Collectors reject them and queued events remain pending.

Reports older than two minutes are marked stale; large future timestamps are marked as clock differences. Incomplete new snapshots are marked as arriving. This is the agent's last reported view, not an instantaneous probe of the PC. Refresh cadence, polling, delivery delays and any custom heartbeat interval affect freshness. Local IPs are taken from reported active sockets; wildcard/loopback addresses are excluded. The Collector peer IP is shown separately because NAT or loopback can make it differ from a device's local IPs. No reverse-DNS lookup or invented UDP peer is used.

Snapshots add roughly one record per observed socket each minute plus a completion heartbeat. They are retained in the existing event table and CSV exports; ensure retention/storage fits your environment. Existing statistics and anomaly analysis continue to distinguish connection-open events from these periodic observations.

## Last minute tab: a frozen connection frame

Use the **Last minute** tab at the top, select a device, and click **Capture latest minute**. The first selection captures automatically. The table covers the 60 seconds ending at the shown capture time, including long-running connections still in the latest reconstructed report, plus connections observed opening, closing, or in snapshots during that minute. It is not just a list of new connection events. Each reported socket identity (protocol, local/peer addresses and ports, process ID) appears once.

The frame shows process/PID, local and peer addresses/ports, last evidence time, and whether a connection was present at the latest report, closed during the minute, absent from the latest snapshot, or has unknown current state. UDP endpoints can have no known peer. Legacy agents without complete snapshots show observations with unknown current state; stale or partial reports are labelled rather than presented as verified live state. No recent reports does not prove the device had no connections. Very short-lived sockets can be missed by agent polling.

**The view stays frozen until Capture latest minute is clicked again.** Paging and switching back from Overview retain the same capture. Incoming records, including late/backdated records, do not shift the frame: page requests reuse its UTC capture time and database row watermark. Selecting a different device creates a new frame. The global Refresh data button updates the device list/history without replacing an existing frame. This is an inspection view, not a permanent stored image or archive; deleting/replacing database history can affect old captures.

Rebuild/restart `Listener_DB_GUI` to install this tab. Agents already sending complete snapshots need no additional protocol update. Older agents can show recent observations, but full current-state reporting requires the snapshot-capable Agent/ServerAgent and Collector described above. No mail/database schema change is required for this GUI feature.

## Local or domain-network launcher

Run **`Launch-Windows.bat`** to open the Windows mode selector. `Start-Windows.bat` without arguments opens the same dialog. The Windows publish script also copies the launcher files into `output/windows/`; copy that **whole folder**, including `wwwroot`, when deploying a built GUI.

| Mode | Server listens on | URL for users |
| --- | --- | --- |
| Local only (default) | `http://127.0.0.1:5080` | Open on the same PC only |
| Domain network | `http://0.0.0.0:5080` (all IPv4 interfaces) | `http://GUI-HOST.your-domain:5080` |

The launcher derives the share URL from the server hostname and domain suffix. Use the port you choose in the dialog; 5080 is the default. The hostname must resolve to the GUI server from client PCs. Users need only a browser and network access to the server; the database and optional key file stay on the server.

In Domain mode, **Configure Windows Firewall** creates/updates only `Listener-GUI-Domain-TCP-<port>`: inbound TCP on the selected port, Domain profile, any reachable client address. It does not open Private/Public profiles or edit AD/GPO objects. The dialog requests administrator elevation when this option is selected. If your administrator already manages the rule through GPO, clear the option and use the existing rule. Domain firewall rules require an active DomainAuthenticated network profile, and GPO policy can override local rules.

**Domain network means network reachability, not AD-user authentication.** Anyone who can reach the permitted GUI port can view the connection dashboard. To require AD accounts, place it behind your authenticated HTTPS reverse proxy. Mail alerts still require the separately configured mail viewing key. Select a key file in the dialog if mail viewing is needed; no key is embedded in published output or browser URLs.

The launcher opens a separate server console and, optionally, the local browser once the server responds. Keep that console open; Ctrl+C stops the GUI. Stop an existing GUI before changing modes on the same port. Local mode binds only to loopback even if a previous Domain firewall rule remains. To remove the persistent rule when it is no longer needed, run elevated:

```powershell
Remove-NetFirewallRule -Name 'Listener-GUI-Domain-TCP-5080'
```

For scripted startup, bypass the dialog by calling PowerShell directly, or pass arguments to `Start-Windows.bat`:

```powershell
# This PC only; no firewall changes:
.\Start-Windows.ps1 -AccessMode Local -Port 5080

# Reachable from domain-network PCs; run elevated to create the rule:
.\Start-Windows.ps1 -AccessMode Domain -Port 5080 -ConfigureFirewall

# Custom database and optional mail viewing key:
.\Start-Windows.ps1 -AccessMode Domain -DatabasePath 'D:\Watcher\watcher-events.db' -MailViewKeyFile 'C:\ProgramData\WatcherCollector\mail-view.key'
```

On a client PC, test the server URL/port if access fails:

```powershell
Test-NetConnection GUI-HOST.your-domain -Port 5080
```

Only the GUI host needs the server executable/database. Starting Domain mode does not redeploy agents or change their Collector address. Both local and network startup reuse the same read-only dashboard and existing features.

## Online status and daily availability timeline

Select a device in the main list. Its **Overview IP box** uses **blue for online**, **red for offline**, and neutral grey when no usable heartbeat history exists. It shows the current online duration, or offline duration and the last online period. If no current socket IP is available, a concrete local IP from previous reports is labelled **last recorded**; Collector-observed/NAT addresses remain separate.

Click **Show online graph** in that box to open `online.html?host=...`. The page shows:

- Current status and current/most recent online period.
- Online and offline/no-report duration for the selected dates.
- One timeline row per local calendar day, with daily online totals and hourly ticks.
- Blue online periods, red gaps without recent heartbeats, and grey unobserved/future time.
- Hover/focus details and an expandable table of exact online periods.

The default range is the last seven days, with up to 90 days per view. The default time zone is the browser's; choose an IANA zone such as `Europe/Moscow`, `Europe/Berlin` or `UTC`. Day lengths, daily clipping and offsets account for daylight-saving changes. The page and main device box refresh every ten seconds while visible.

### What online means

Availability is inferred from `HEARTBEAT` **event timestamps**, not the time old queued events arrive at the Collector. Each heartbeat covers a configurable grace interval (default **120 seconds**). Overlapping/touching intervals form one online period; gaps longer than the grace interval split periods. An ongoing period ends at the current time for duration calculations. An expired period ends at its final heartbeat plus the grace interval.

This estimates **monitoring availability**, not OS boot uptime or confirmed power state. An agent, network or Collector outage can all produce a red no-report gap, and brief outages inside the grace interval may not be visible. Before the first retained heartbeat the history is unknown. Delayed records can fill historical periods without making a device online now; future-dated heartbeats are ignored. The same reported host identity groups the history, so use unique agent IDs.

`OnlineTimeoutSeconds` configures the grace interval for the GUI (30–3600 seconds). Set it above any custom agent heartbeat interval. For example, from the published GUI folder:

```powershell
.\Listener_DB_GUI.exe --OnlineTimeoutSeconds 300
```

Changing it recalculates the estimate from retained heartbeats. No availability tables, new agent protocol or database writes are introduced; rebuild/restart the GUI to use the feature. The history API is `/api/availability?host=PC-01&from=2026-11-13&to=2026-11-19&tz=Europe%2FMoscow`.

## Faster device views

Update the Collector and GUI together to use maintained device summaries, indexed heartbeat history and cached online sessions. Initial history shows the latest recorded UTC day; clear the dates or use Reset for all retained history. The new/returning destination report runs on **Check connections**. See [deployment steps, benchmarks and limitations](PERFORMANCE.md).

## Connection names

History, current connections and Last minute captures now include **Source name** and **Destination name** columns alongside the IPs. The new/returning destination report includes **Source device** and **Destination name**. Source/local identifies the monitored device and peer/destination identifies its remote endpoint; these labels do not establish which side initiated a connection.

Names already reported in `SOURCE_DOMAIN_NAME` and `RECIEVER_DOMAINNAME` take priority. Missing source names fall back to the monitored device ID. Missing destination names are looked up through the GUI server's configured DNS resolver, separately from the data requests. AD device names therefore depend on correct internal reverse-DNS/PTR records and the server using the appropriate DNS servers. A PTR name may describe a provider/server rather than the actual website; some IPs have no PTR record and stay `—`.

Lookup uses four workers, a 256-item queue, one-second lookup timeouts and a cache limited to 4,096 addresses (10 minutes for names, one minute for unavailable results). The browser batches visible table rows, caches results, and bounds retries. Wildcard/multicast addresses are not queried; loopback displays `localhost`. To disable DNS queries, set the GUI process environment variable `ResolveConnectionNames=false`; reported names still display.

Reverse-DNS names are current annotations, including on historical/frozen captures; they do not modify the event, database, CSV exports or recorded capture timestamps. Names render as plain text. Rebuild/redeploy the GUI and reload the browser; Collector and agents need no changes for this feature.

Validation: build the GUI, run `dotnet run --project tests/ConnectionNames -c Release`, then `python -m unittest discover -s tests -p 'test_connection_names.py'`. The worker tests use a fake DNS resolver, and browser tests use deterministic name responses.

## Device folders

The left menu has expandable **Win Server**, **Linux Server**, **User PCs**, and **Project Stand** folders, each with a device count. Click a folder to open/close it, then choose a device to view its existing overview, history or Last minute capture. Search finds devices across all folders and expands matching groups. Folder expansion is retained during page refreshes through the Refresh data button; a full browser reload resets it.

Each device appears once. Its folder comes from its newest categorized event by agent event time; equal timestamps use the global event sequence. Uncategorized/legacy reports do not override a known category. Historical records in other categories remain part of the same device history. Devices with no categorized records are shown in **Unclassified**, which is expanded initially. Empty standard folders stay available with count zero.

This is a GUI-only update. The category selection made in the agent installer already determines event storage; devices sending uncategorized events need that selection configured through Install / Update before they can move out of Unclassified. The GUI remains read-only and does not reassign database records or infer server roles from the OS. Old databases without category tables remain supported under Unclassified.

Category lookup uses per-device/time indexes in the physical category tables and preserves the existing maintained device-list summaries. To deploy, rebuild/restart the GUI and reload the browser. No Collector schema migration is required for the folders.

## Orange working periods after offline replay

Agents already store unsent JSON events locally and retry them when the Collector becomes reachable. Original event timestamps and IDs survive replay. A file is removed only after the Collector durably commits it and acknowledges the matching event ID; repeated delivery is deduplicated.

The online graph now distinguishes:

- **Blue:** estimated working time with timely heartbeat reports.
- **Orange:** estimated working time reconstructed from buffered/delayed heartbeats.
- **Red:** no recent heartbeat evidence. This does not prove the device was powered off.
- **Grey:** before the first retained report, or future time.

Orange is retrospective: while a device cannot reach the Collector, the server cannot know whether it is still working. After replay, the chart fills the historical working periods and colors delayed ones orange. A heartbeat is delayed when `received_utc - TIMESTAMP_UTC` exceeds **120 seconds** by default. Set GUI configuration/environment `BufferedDeliverySeconds` (30–3600) to adjust the threshold. This is evidence of delayed delivery, not proof of a specific internet, VPN, corporate-network or Collector failure. Agent/server clocks should be synchronized; clock skew or Collector congestion can also create apparent delay. Delivery delays within the threshold do not turn orange.

Working intervals still use the configured heartbeat grace period. Each late heartbeat covers its timestamp until the next heartbeat or grace expiry, whichever comes first. A timely following heartbeat switches the color back to blue. Sleep/shutdown/collection gaps without heartbeat evidence remain red. Replayed old heartbeats do not make a device online now. Duplicate stamps use the earliest recorded receipt time, so a replay of an on-time heartbeat does not make it orange.

**Device working** totals include blue plus orange; the new **Working · reports delivered late** card shows the orange subset. Daily captions, hover/focus details, and the exact-period table expose the same distinction. Existing `total_online_seconds` remains total estimated working time for API compatibility; `total_buffered_seconds` and `total_connected_seconds` add the split.

Deploy the updated **Collector first**, then the **GUI**, and reload the browser. The Collector backfills `gui_heartbeat_delivery` and schema marker 2 from retained event and receipt times in one transaction, preserving event tables, global IDs, categories and CSVs. Old records can be recolored if both timestamps are valid. Unparseable/missing receipt times do not establish delayed delivery. A new GUI also works with older Collector databases through raw-event queries, but updating the Collector provides indexed receipt history and avoids repeated raw-history scans.

The updated compiled Agent and PowerShell ServerAgent also yield between acknowledged events when a slow replay batch has consumed about five seconds, allowing the collection loop to run again. Existing connect/read timeouts still apply to the in-flight event. Older agents' retained data is compatible with the orange graph; updating agents provides this replay responsiveness improvement.

Buffering is finite: the compiled agent currently allows 50,000 pending events, and ServerAgent uses its configured `max_spool_events`. At capacity, new collection fails rather than discarding existing queued evidence; a missing period cannot be reconstructed without retained heartbeats. Keep the spool on persistent storage and size deployments for expected outage volume. The GUI cannot observe periods whose queued files were deleted or never collected.

## Device network communications and running processes

Below the daily online timeline, open **Show networks & processes**. Choose a UTC day, then **Inspect sample** to see active adapters, their network names and the processes/services observed in the same minute. Lists are paginated (50 samples and 200 process entries per page), with VPN candidates first. The context day filter is UTC independently of the timeline's selected time zone; all sample/detail timestamps are labelled UTC.

Update the **Collector first**, then the **agents**, then rebuild/restart the **GUI**. New context event types require the updated Collector before agents send them. Existing agents and old connection history remain compatible, but cannot retroactively supply network/process snapshots. Both compiled Windows/Linux agents and the PowerShell ServerAgent sample context on their heartbeat (normally every 60 seconds). ServerAgent registration also copies `Shared/NetworkContext.ps1`; keep the repository layout when registering/updating.

Database tables:

- `device_network_communications`: device (`host`), snapshot/time and receipt time, adapter ID/name, network type (`wifi`, `ethernet`, `vpn`, `other`), network name, connection/adapter description, state, addresses and gateway.
- `device_process_snapshots`: device, snapshot/time and receipt time, process/service type, PID, name, executable path and process start time where available, plus a name-based VPN candidate flag.
- `device_context_snapshots`: expected counts and coverage notes. A snapshot is shown only when its marker and all expected rows have arrived. Offline replay uses the existing spool and durable ACKs, and duplicate event IDs do not duplicate context.

Windows uses active adapters and network profiles; Wi-Fi SSID is used when `netsh` exposes a matching interface GUID, otherwise the profile label is retained. Ethernet has no SSID; profile labels may be generic. Linux uses NetworkManager connection labels when available, otherwise adapter details with an empty network name. BSSID/access-point MAC and saved Wi-Fi passwords are not collected. Active VPN adapters may be identified from adapter type/name; other virtual adapters remain visible with their descriptions.

Running processes and services are sampled, not every launch/termination event: brief programs between samples can be missed. Start time/path are best effort; service rows may lack start time/PID. OpenVPN, Amnezia, WireGuard and other VPN-like names are correlation hints, not proof a VPN tunnel was connected, used for a given socket, or routed a particular application. A running service may be idle. No command lines, environment variables, credentials, packet payloads or message contents are collected.

Permission failures are reported as partial samples; samples cap at 128 adapters and 4,096 process/service entries, and metadata fields are limited to 256 characters. Context increases storage and spool volume substantially; the existing 50,000-event compiled-agent buffer remains finite and may fill sooner during an outage. No historical data is deleted by this update.

## Daily timeline navigation and network strips

The online-history page initially shows the last **three local calendar days**. Choose From/To dates and **Days at a time** (3, 7, 14, 30 or 90), then click **Show timeline**. Previous/Next days pages through the selected range. Totals continue to cover the entire chosen range; each page shows only its own daily rows and network strips. Time-zone changes and 23/25-hour DST days apply to both tracks.

A smaller network strip appears below each day's working-time graph. It uses the active adapters in stored minute samples: Wi-Fi, Ethernet, VPN, other or multiple adapters. Hover/focus a segment for network/profile names and times; labels below the strip summarize the networks observed that day. Empty space is unknown, not a claim of disconnection. A sample extends only until the next network sample, two minutes, or the current time, whichever comes first. Equal consecutive observations merge; empty samples stop the previous connection estimate. Network rows must match their marker's expected network count; process rows can arrive independently without withholding network evidence. The strip does not prove that a particular adapter carried traffic or supported every heartbeat.

The device card also has **It's time to analyze** beside **Show online graph**. It opens `analyze.html?host=...`, an intentionally empty page reserved for future analysis.

Rebuild/restart only the GUI and reload the browser. No new Collector/agent update or schema change is required. Network strips need samples collected by the previous network-context update; older history stays grey where no such evidence exists.

## Attachment mail information

The Mail alerts page now reads only `mail_information`, showing observed receipt date, device, sender, recipients, attachment count and names/formats/sizes. No message body or variant selector remains. The existing separate GUI viewing-key configuration is retained. See [the new add-on and migration guide](../Thunderbird_Attachments/README.md).

## It's time to analyze

The existing device button now opens Incoming, Outgoing and Unknown connections from
the separate `Listener_ConnectionAnalyzer` database. Filter all recorded identities,
connections first seen on a day, or peer IPs never observed before that day in any
direction. Time zone selection applies to first-seen dates. TCP directions are labeled
estimated; old/ambiguous reports remain Unknown. The GUI does not run the analyzer.

Start the independent module and point `WATCHER_ANALYSIS_DB_PATH` (or
`AnalysisDatabasePath`) to its DB. Windows start/launch scripts accept
`-AnalysisDatabasePath`. See [setup, identity rules and upgrade order](../Listener_ConnectionAnalyzer/README.md).
