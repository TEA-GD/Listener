# Watcher V6 — PLAIN TCP collector + Windows/Linux agents (install/uninstall GUI)

**No authentication tokens. No TLS certificates. No private/public keys. No Python needed for the installed C# programs.**

This is a change to the **network protocol**, not just a fix to a certificate-loading flag. You MUST replace **both** the collector and every participating agent. An old TLS agent cannot talk to a plaintext collector, or vice versa.

> SECURITY: This transport is **NOT encrypted, NOT authenticated**. A machine that can access the listener can read/inject event contents and can claim any `MONITORED_HOST`. The `COLLECTOR_PEER_IP` column is the transport peer IP, not proof of the host identity. Only use on a restricted management VLAN or similarly isolated network. Restrict port 6515 by Windows Firewall and the collector's CIDR allowlist. Never expose this service to the Internet. If the network is not trusted, use TLS + device identities or carry TCP inside a protected VPN tunnel instead.

## V6: Uninstall buttons in both Windows installers

Both Windows graphical installers now provide a visible **Uninstall** button, without affecting the network protocol. The install/update buttons still work as in V5. Rebuild **both** Windows GUI installers from these source files to get the buttons (older already-compiled EXEs will not update themselves).

**Agent:** Open `Agent/output/windows-installer/WatcherInstaller.exe` **as Administrator**, select **Uninstall Agent**, and confirm. This stops and removes the `Watcher-Connection-Agent` scheduled task, terminates only the installed `WatcherAgent.exe` process, and deletes `C:\Program Files\Watcher\Agent`. By default **`C:\ProgramData\WatcherAgent` is preserved**, including `agent.json` and any unsent event spool. To remove these files too, check *Also delete local configuration and buffered unsent events* **before** clicking Uninstall. That action permanently deletes unsent events.

**Collector:** Open `Collector/output/windows-installer/WatcherCollectorInstaller.exe` **as Administrator**, select **Uninstall Collector**, and confirm. It stops/deletes the `WatcherCollector` service, removes its application binaries from `C:\Program Files\Watcher\Collector` and removes only Windows Firewall rules named `Watcher Collector TCP <port>` or `Watcher Collector TLS <port>`. By default **`C:\ProgramData\WatcherCollector` is preserved**, including the collector config, SQLite DB and logs. To delete this entire directory, select *Also delete collector configuration, SQLite database and logs in ProgramData* before uninstall and accept an additional confirmation.

**Preservation/safety:** The collector uninstall always keeps exported CSV files wherever they are configured (usually `C:\Watcher\Feeds\Connections`). A SQLite database configured **outside** ProgramData is also preserved even when data deletion is selected. The Agent uninstaller does not uninstall the Collector; the Collector uninstaller does not uninstall agents installed on other hosts. The Collector does not uninstall the older standalone Python collector; stop it separately if needed. Back up forensic records before any data removal. You may need a reboot if Windows still holds a deleted Windows service open.

After uninstalling, verify:

```powershell
Get-ScheduledTask -TaskName 'Watcher-Connection-Agent' -ErrorAction SilentlyContinue
Get-Service WatcherCollector -ErrorAction SilentlyContinue
Test-Path 'C:\Program Files\Watcher\Agent\WatcherAgent.exe'
Test-Path 'C:\Program Files\Watcher\Collector\WatcherCollector.exe'
```

An absent task/service and two `False` results indicate the installed components are gone. If you chose to preserve ProgramData, those folders should still exist.

## Project contents

- `Collector/Watcher-Collector.sln` — Visual Studio project containing native C# collector + graphical Windows collector installer.
- `Agent/Watcher-Compiled-Installer.sln` — native C# Windows/Linux agent + graphical Windows agent installer.
- `Collector/build/build-windows.ps1` — publishes a self-contained collector EXE, then embeds it in a self-contained GUI installer EXE.
- `Agent/build/build-windows.ps1` — builds a self-contained GUI agent installer embedding the agent EXE.
- `Agent/build/build-linux.sh` — creates a Debian 12/13 amd64 `.deb` (requires .NET 10 SDK, `dpkg-deb`).
- `build-windows-all.ps1` — publishes both Windows installer EXEs.

The source compiles with the .NET 10 SDK on Windows. The collector uses NuGet packages `Microsoft.Data.Sqlite` and `Microsoft.Extensions.Hosting.WindowsServices` for storage and service hosting. NuGet access (or configured offline cache) is needed at build time, but the published executables include the .NET runtime.

## Set the collector domain name during agent installation

The Windows agent installer has editable **Collector domain name** and **TCP port**
fields. Enter a DNS name such as `collector.example.local` and port `6515`.
Use the hostname alone, without `http://`, a path, or `:port` (the port has its
own field). The agent uses normal OS DNS resolution when connecting. The DNS
record must resolve to the collector PC from each agent machine.

The installer saves `collector_host` and `collector_port` in
`C:\ProgramData\WatcherAgent\agent.json` and reloads them on the next update.
No source edit or per-domain rebuild is required. The computer identity field
identifies the monitored PC; it is separate from the collector destination.

Example agent configuration (also supported on Linux):

```json
{
  "agent_id": "workstation-01",
  "spool_dir": "C:\\ProgramData\\WatcherAgent\\spool",
  "collector_host": "collector.example.local",
  "collector_port": 6515
}
```

Older config files without the two collector fields still use the compiled
`CollectorSettings` defaults. New installer builds are needed to get the new
fields; then use Install / Update on existing agents. IP addresses remain
supported as destinations.

## Build Windows installers

Open PowerShell at the root directory, then:

```powershell
.\build-windows-all.ps1
```

The resulting installers should be at:

```text
Collector\output\windows-installer\WatcherCollectorInstaller.exe
Agent\output\windows-installer\WatcherInstaller.exe
```

**Build the source with Visual Studio 2026 / .NET 10 SDK. Running or double-clicking the EXE generated in `src/.../bin/` is not a substitute for running the GUI installer.**

If MSBuild prompts to restore NuGet packages, allow restore on the development workstation. No Python setup is necessary for the C# binaries.

## Install the collector first (Windows)

1. On the Windows collector host, stop any old Python collector or V4 C# collector using port 6515. The V5 GUI installer can stop/reconfigure the existing `WatcherCollector` Windows service, but does not stop a separately launched Python collector.
2. **Back up** `C:\ProgramData\WatcherCollector\watcher-events.db` (or your previous Python collector database) before the upgrade. Never run old and new collectors against the same SQLite database simultaneously.
3. Run **`WatcherCollectorInstaller.exe`** as Administrator.
4. Set `Listening port` to **6515** and select the same port in the agent installer.
5. Optionally set `Allowed agent IP networks` to the **source IP ranges of your agent machines**, e.g. `10.90.20.0/24,156.100.0.77/32` if those are the actual agent ranges. Leave the field blank to accept any sender on the selected port. If supplying CIDRs, do not use `0.0.0.0/0`.
6. Click **Install / Update**. The installer writes `C:\ProgramData\WatcherCollector\collector.json`, installs `C:\Program Files\Watcher\Collector\WatcherCollector.exe`, creates `WatcherCollector` as an auto-start service, and adds an inbound Windows Firewall rule on Domain/Private profiles, restricted to the supplied CIDRs when present.

**No `.crt`, `.key`, `.pfx`, CA generation, password, or registration steps are required.** Old certificate files might still remain from prior V4 installations, but this V5 collector never loads them. Remove them separately after confirming they are not used elsewhere and after backing up anything needed.

Collector files:

```text
C:\ProgramData\WatcherCollector\
    collector.json
    watcher-events.db
    collector.log
C:\Watcher\Feeds\Connections\HOSTNAME\YYYY-MM-DD.csv
```

The CSV files use a `;` delimiter and are compatible with the earlier Watcher field names (`RECIEVER_IP`, `PORT_USED`, etc.).

## Install an agent (Windows)

1. Run **`WatcherInstaller.exe`** as Administrator.
2. Enter the collector DNS hostname and TCP port at the top.
3. Confirm the machine name/agent ID; click **Install / Update**. There are **no** token/certificate fields.
4. It installs `C:\Program Files\Watcher\Agent\WatcherAgent.exe` and creates `C:\ProgramData\WatcherAgent\agent.json` and `spool\`.
5. It registers a Windows scheduled task `Watcher-Connection-Agent` under the SYSTEM account.

To run one manual collection/transmission and see errors:

```powershell
& 'C:\Program Files\Watcher\Agent\WatcherAgent.exe' --once
```

To check startup registration:

```powershell
Get-ScheduledTask -TaskName 'Watcher-Connection-Agent'
```

The user must allow outbound TCP traffic from the agent machine to the collector IP on port 6515.

## Install an agent (Linux)

On a Debian 12 or 13 amd64 build machine with .NET 10 SDK, run (use the same Debian release as the target server):

```bash
cd Agent
./build/build-linux.sh
```

The package will be written to `Agent/output/watcher-agent_1.0.1_amd64.deb`. On the target Linux machine:

```bash
sudo apt install ./watcher-agent_1.0.1_amd64.deb
sudo systemctl status watcher-agent
sudo journalctl -u watcher-agent -n 50 --no-pager
```

It automatically creates `/etc/watcher-agent/agent.json`, installs the agent, and enables the systemd service. Set `collector_host` and `collector_port` in that file and run `sudo systemctl restart watcher-agent` to apply them. No certificates, keys, tokens or enrollment step.

The Linux agent uses `ss` from `iproute2`, and should be run with appropriate privileges to see process names.

The build script selects release-specific dependencies: Debian 12 uses `libicu72`/`libssl3`; Debian 13 uses `libicu76`/`libssl3t64`. See [Linux installation and dependency troubleshooting](Agent/README-linux.md).

## Validate communication and storage

On collector PC:

```powershell
Get-Service WatcherCollector
Get-NetTCPConnection -LocalPort 6515 -State Listen
Get-Content 'C:\ProgramData\WatcherCollector\collector.log' -Tail 30
Get-ChildItem 'C:\Watcher\Feeds\Connections' -Recurse -Filter *.csv
```

On agent PC:

```powershell
Test-NetConnection watcher-collector.domain.local -Port 6515
& 'C:\Program Files\Watcher\Agent\WatcherAgent.exe' --once
```

Replace the example host with your collector's actual configured hostname.

### Event protocol

Each agent sends one UTF-8 JSON object per line with an `event` field, e.g. `{"event":{"EVENT_ID":"<uuid>","MONITORED_HOST":"dc-03.local","EVENT_TYPE":"HEARTBEAT","TIMESTAMP_UTC":"2026-11-05T10:00:00Z"}}\n`. The collector stores the event in SQLite, then responds `ACK <uuid>\n`. An event is removed from the agent's spool **only after receiving its matching ACK**. Duplicate `EVENT_ID`s are stored once. CSV is regenerated for the affected host/day.

### Limitations

- The polling agent can miss TCP connections shorter than its poll interval and will report `TCP_DISCONNECTED` when a connection disappears between observations, not necessarily on a wire-level FIN/RST.
- An unauthenticated collector cannot reliably distinguish forged sender identities. Use a restricted infrastructure segment and avoid sensitive log contents.
- This version **does not interoperate** with existing TLS-enabled Watcher V3/V4 components. Upgrade both ends.

## Build/test status

These are **source files**, not compiled executables. If your machine has the .NET SDK, use the build script. C# binaries must still be compiled and tested on your Windows/Linux deployment targets. Static protocol/configuration checks are included in `tests/validate_plain_tcp.py`.

## Source review

See [REVIEW.md](REVIEW.md) for architecture, deployment limitations, and the build/test results recorded when importing this source into TEA-GD/Listener.

## SQLite columns and upgrading existing databases

The collector now stores each of the 19 standard event fields in a separate
column of `events`: `EVENT_ID`, `EVENT_TYPE`, `TIMESTAMP_UTC`, `DATE_AND_TIME`,
`SOURCE_IP`, `SOURCE_DOMAIN_NAME`, `PROCESS_NAME`, `PROCESS_ID`, `RECIEVER_IP`,
`RECIEVER_DOMAINNAME`, `PORT_USED`, `SOFTWARE_THAT_USED`, `SOURCE_PORT`, `PROTOCOL`,
`STATE`, `OPERATING_SYSTEM`, `MONITORED_HOST`, `SNAPSHOT_ID`, and `COLLECTOR_PEER_IP`.
The original `RECIEVER` spelling is retained for compatibility. Values are TEXT,
matching the agent protocol; absent optional values become empty strings.
`EVENT_ID` is the primary key. Internal `host`, `date_utc`, and `received_utc`
columns support export grouping and receipt tracking. `COLLECTOR_PEER_IP` always
comes from the TCP connection, never from the sender's claim.

On startup, legacy `payload_json` records are migrated into these columns and the
JSON column is removed. Existing IDs, timestamps, receipt times and export state
are preserved. Invalid legacy JSON aborts migration without replacing the old
table. Unknown, nonstandard JSON properties are not retained in the new schema.

Before upgrading, stop the collector and back up its SQLite database (including
any WAL/SHM sidecars). Rebuild and install/update the collector, then start it;
the installer configuration check also triggers migration. Agents do not need
rebuilding for this database-only change. Keep the backup if you need to roll
back to an older collector, which expects `payload_json`.

Example query:

```sql
SELECT EVENT_ID, EVENT_TYPE, TIMESTAMP_UTC, SOURCE_IP, RECIEVER_IP,
       PORT_USED, PROCESS_NAME, MONITORED_HOST, COLLECTOR_PEER_IP
FROM events
ORDER BY TIMESTAMP_UTC DESC;
```

Run the database integration checks with .NET 10:

```powershell
dotnet run --project tests/DatabaseIntegration -c Release
```

## Port-only collector setup

Choose the listening port (default **6515**) and leave **Optional allowed agent
networks** blank. The collector listens on all IPv4 interfaces on that PC;
no local IP or hostname needs to be entered. The default firewall rule allows
that port on Domain/Private profiles, with any remote address in this mode.
Public-profile inbound access is not enabled by the installer.

For manual configuration, omit `bind_host` (defaults to `0.0.0.0`) and omit
`allowed_client_cidrs`, or set it to `[]`. To retain sender filtering, supply
CIDRs as before. Existing configured CIDRs remain effective after upgrading;
clear the installer field and click Install / Update to switch to port-only mode.

Agents still need the collector PC's DNS name or IP and port as their destination;
listening on a port does not discover agents or capture unrelated network traffic.
This remains the Watcher JSON-line TCP protocol. With no sender filter, any sender
that can reach the port can submit events; the transport has no authentication
or encryption. Use a trusted network or protected VPN.

Agent destination and ACK integration tests:

```powershell
dotnet run --project tests/AgentIntegration -c Release
```

## Database web dashboard

[Listener_DB_GUI](Listener_DB_GUI/README.md) is a separate Visual Studio/.NET 10
web project for viewing the collector database. It lists monitored PCs and
provides per-PC statistics, filters, paginated events and a full event detail
view. Build with `Listener_DB_GUI/Build-Windows.bat`, start with
`Listener_DB_GUI/Start-Windows.bat`, then open `http://127.0.0.1:5080` locally.

## Lightweight agent for servers and domain controllers

[ServerAgent](ServerAgent/README.md) provides a standalone Windows PowerShell
TCP metadata agent and startup-task helper. It needs no GUI installer or package.
Run `Register-ServerAgentTask.ps1 -CollectorHost collector.example.local` as
Administrator to register and start `Watcher-Server-Agent` under SYSTEM after
every reboot. Events use the existing collector/database/dashboard format.
