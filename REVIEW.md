# Source review and validation

Reviewed upload: `Watcher-NoKeys-V6-Uninstall-VisualStudio-Source2.zip`.

## How it works

Watcher records TCP connection metadata, rather than packet contents. The Windows/Linux agent polls established connections every 5 seconds using PowerShell (`Get-NetTCPConnection` and process inventory) or Linux `ss`. It emits `ACTIVE_AT_START`, `TCP_CONNECTED`, `TCP_DISCONNECTED`, and a heartbeat every 60 seconds. Loopback peers 127.0.0.1 and ::1 are excluded. Metadata includes addresses, ports, PID, process name/path, host identity and timestamps.

Events are flushed to local JSON spool files (maximum 50,000 pending events). Each send handles up to 1,000 queued events. The agent sends newline-delimited JSON over TCP port 6515 and removes an event only after receiving its matching `ACK <EVENT_ID>`.

The Windows collector checks the source IP against configured CIDRs, validates required event fields, and commits events to SQLite before acknowledging them. Duplicate event IDs are ignored. It periodically regenerates semicolon-delimited CSV files per host and UTC day. The peer IP is supplied by the collector, overriding any agent-supplied value.

Windows GUI installers embed the application binaries. The collector runs as a Windows service with a restricted firewall rule; the agent runs as a SYSTEM scheduled task at startup. Linux packaging targets Debian 12 amd64 with systemd. Uninstall defaults preserve ProgramData and unsent events; optional deletion removes that local data. Collector CSV exports outside the deleted data directory are preserved.

## Deployment requirements and limitations

- Change `Agent/src/WatcherAgent/CollectorSettings.cs` from `watcher-collector.domain.local` to your actual collector address before building agents. No deployment address was guessed during review.
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
