# Source review and validation

Reviewed upload: `Watcher-NoKeys-V6-Uninstall-VisualStudio-Source2.zip`.

## How it works

Watcher records TCP connection metadata, rather than packet contents. The Windows/Linux agent polls established connections every 5 seconds using PowerShell (`Get-NetTCPConnection` and process inventory) or Linux `ss`. It emits `ACTIVE_AT_START`, `TCP_CONNECTED`, `TCP_DISCONNECTED`, and a heartbeat every 60 seconds. Loopback peers 127.0.0.1 and ::1 are excluded. Metadata includes addresses, ports, PID, process name/path, host identity and timestamps.

Events are flushed to local JSON spool files (maximum 50,000 pending events). Each send handles up to 1,000 queued events. The agent sends newline-delimited JSON over TCP port 6515 and removes an event only after receiving its matching `ACK <EVENT_ID>`.

The Windows collector checks the source IP when optional CIDRs are configured, validates required event fields, and commits events to SQLite before acknowledging them. Duplicate event IDs are ignored. It periodically regenerates semicolon-delimited CSV files per host and UTC day. The peer IP is supplied by the collector, overriding any agent-supplied value.

Windows GUI installers embed the application binaries. The collector runs as a Windows service with a restricted firewall rule; the agent runs as a SYSTEM scheduled task at startup. Linux packaging targets Debian 12 amd64 with systemd. Uninstall defaults preserve ProgramData and unsent events; optional deletion removes that local data. Collector CSV exports outside the deleted data directory are preserved.

## Deployment requirements and limitations

- Enter the collector DNS hostname and port in the agent installer. These settings are saved in agent.json; older configurations fall back to CollectorSettings defaults.
- Transport is plaintext and unauthenticated. The CIDR filter is not host authentication. Restrict access to a trusted management network or protected VPN; do not expose port 6515 publicly.
- Connections shorter than the polling interval can be missed. Disconnection means absence from the next snapshot, not observation of TCP FIN/RST.
- `--once` catches collection/transmission failures and can exit successfully despite them. Inspect logs and collector storage to confirm delivery.
- Spool files are individually flushed/renamed, but an entire batch is not atomic. A write failure partway through a batch can leave some events queued while the baseline remains unchanged, permitting logical duplicates on retry.
- No retention policy is implemented for SQLite/CSV/logs. CSV regeneration reads a host/day into memory and rewrites the whole file; capacity planning is needed for large installations.

## Validation performed (2026-11-09)

- All 11 supplied Python structural regression tests passed. These inspect source text; they are not end-to-end protocol tests.
- `bash -n Agent/build/build-linux.sh` passed.
- Both solution builds passed with .NET SDK 10.0.403, Release configuration, zero warnings/errors. This compiled all four C# projects and published embedded Windows payloads. Initial nested builds required adding the SDK directory to PATH; no source changes were needed.
- Ran the compiled Linux agent with a temporary config and `--once`: heartbeat generation and persistence after failed delivery verified. The sandbox reported no visible sockets and denied the send attempt; this does not verify live connection discovery or delivery.
- Windows GUI, service/task registration, uninstall behavior, Debian package installation, and live collector/agent ACK + SQLite/CSV integration were not executed. These remain deployment-target checks.

Application source from the upload is unchanged. Added `.gitignore`, this review, and a README link; marked the Linux build script executable. Generated build artifacts and temporary test data are excluded from Git.

## Subsequent database-column update

The collector storage implementation now uses individual event columns and an
atomic migration from the original JSON schema; the original import's statement
that application sources were unchanged describes only that initial import.
DatabaseIntegration exercises the actual collector database methods and verifies
fresh storage, legacy backfill, repeated initialization, event deduplication,
trusted peer IP, CSV escaping/export state, and rollback for malformed legacy
JSON. These database checks pass on Linux; Windows installation and live network
integration remain deployment-target checks.

## Port-only listener update

The installer no longer requires agent IP networks. Empty/omitted CIDRs accept
any reachable sender; supplied CIDRs continue to filter clients. Default binding
is all IPv4 interfaces. Live loopback integration tests exercise a port-only
heartbeat, matching ACK and database record, and rejection with a nonmatching
CIDR list. Windows firewall installation remains a deployment-target check.

## Configurable agent destination

The agent installer now accepts and persists the collector DNS hostname and TCP
port. Settings reload during updates; runtime validation rejects URLs/paths and
invalid ports. AgentIntegration tests configuration round-trip, legacy defaults,
invalid input, actual TCP transmission using the DNS name localhost, deletion
on matching ACK, and retention after rejection. Windows GUI interaction remains
untested on this Linux host.

## Listener_DB_GUI web project

Added an independent ASP.NET Core/.NET 10 solution with a read-only SQLite API
and a dependency-free browser UI. It lists PCs, shows per-PC aggregate counts,
top processes/destinations/ports/types, UTC filters, paginated events and all
fields in an event detail dialog. It defaults to localhost HTTP port 5080.

The Release build and four dashboard test cases passed, including real HTTP
queries, filters/pagination, missing/legacy schema errors, database immutability,
and Chromium desktop/mobile UI navigation. Test data was synthetic. Windows
build/launch scripts are provided; Windows execution itself was not tested on
this Linux environment. This GUI does not start or repair the collector service.

## Lightweight ServerAgent module

Added a PowerShell 5.1-targeted TCP metadata agent for Windows servers/DCs and
helpers to register/remove a SYSTEM startup task. This is independent of the GUI
agent; it buffers events, requires matching ACKs, preserves unsent data and uses
the existing collector protocol. Scope is TCP metadata only, not Event Logs.
Testing uses PowerShell 7 and mocked Windows TCP/CIM calls against the real C#
collector with temporary SQLite/CSV storage. Windows 5.1 execution, Task Scheduler
registration and reboot behavior still require a Windows target check.

## New & rare connections board

Added a per-PC/day destination-IP board with new-in-history and returned-after-gap
classifications (default 30 elapsed days). Positive connection/startup events
create candidates; prior disconnects also provide history evidence. The API uses
parameterized queries against retained history and remains read-only. Tests cover
cross-PC isolation, future records, repeated same-day observations, recent and
long-gap history, exact threshold boundaries, disconnect/heartbeat exclusions,
IPv6, pagination, input validation and browser drill-down with an exact IP filter.

## Linux package dependency correction

The package builder now selects runtime dependencies for Debian 12 versus 13
instead of always requiring libicu72/libssl3. Package version is 1.0.1. It rejects
unsupported build platforms before publishing and creates staging directories
with explicit 0755 permissions, including with a restrictive caller umask.
On Debian 13, package creation and APT installation simulation passed. Tests
verified package contents, maintainer-script syntax and actual execution of the
extracted self-contained agent with heartbeat delivery/ACK cleanup. No system
package was installed during validation; systemd boot/start validation remains a
target-server check. Debian 12 dependency mapping is retained but was not built
on a Debian 12 host in this check.

## Listener_Analyzer module

Added a separate rule-based profiling console project with Windows/Linux build
scripts and local/shared JSONL alert output, as selected by the user. Historical
rows are learned without alerting. Per-device destinations and full
IP/port/protocol/process/path combinations track observations and daily activity.
Live new, infrequent and returning patterns produce reasoned alerts after the
baseline, with cooldown/age/order rules and durable local outbox delivery.

Integration tests use the actual analyzer executable, synthetic collector-shaped
SQLite source files and local output. They verify profiles and Windows/Linux
isolation, usual-traffic suppression, new/rare/returned combinations, cooldowns,
restart behavior, backlog retry, profile export and unchanged source bytes.
Source path/cursor changes halt rather than silently missing data. Shared-file
transport is at least once with stable alert IDs; consumers deduplicate and skip
partial JSON lines after interrupted writes. Actual SMB/domain permissions and
Windows startup execution remain deployment checks. No messenger credentials or
live recipients are used during tests.
