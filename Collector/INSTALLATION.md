# Collector installation with both analyzers

The Collector installer now includes the **connection analyzer** (Incoming/Outgoing,
first-seen views) and the **behavioral analyzer** (profiles and rule-based alerts).
The applications remain independent; setup installs/configures their background startup.
No separate analyzer build/configuration is needed for a normal installation.

## Windows

1. Build the current `Collector/build/Build-Windows.bat` with .NET 10 SDK on the build
   machine. The self-contained installer embeds all three executables. An older
   installer EXE will not contain this feature.
2. Run `Collector/output/windows-installer/WatcherCollectorInstaller.exe` as Administrator.
3. Leave **Install and manage both analyzers** checked, set the port/allowed networks,
   then click **Install / Update**.

Setup installs:

| Component | Program | Configuration/data | Startup |
|---|---|---|---|
| Collector | `C:\Program Files\Watcher\Collector\WatcherCollector.exe` | `C:\ProgramData\WatcherCollector` | `WatcherCollector` Windows service |
| Connection analyzer | `C:\Program Files\Watcher\Collector\Analyzers\Connection\Listener_ConnectionAnalyzer.exe` | `C:\ProgramData\WatcherConnectionAnalyzer` | `Watcher-Connection-Analyzer` task |
| Behavioral analyzer | `C:\Program Files\Watcher\Collector\Analyzers\Behavior\Listener_Analyzer.exe` | `C:\ProgramData\WatcherAnalyzer` | `Watcher-Behavior-Analyzer` task |

Both analyzers receive the Collector's **actual configured database path**, including
an existing custom path. Config files are `config.json` in their data directories.
The connection DB defaults to `connection-analysis.db`; behavioral state defaults to
`profiles.db` and the alert output to a local `alerts.jsonl`. These default connection
analysis paths match the updated GUI. Open a device and **It's time to analyze**.
The GUI is still deployed/launched separately.

Before starting tasks, setup calls `--check-config` to initialize/validate analyzer
state without processing history or delivering pending alerts. Historical learning
then runs in the background. The GUI may initially show learning/provisional results.

Tasks run as SYSTEM at startup after a 30-second delay, without user login. They
allow battery operation, have no 72-hour runtime limit, ignore duplicate instances
and restart after failures. Each module saves stdout/stderr, including startup
errors, to `analyzer.log` in its data directory. Logs are append-only.

The existing **Start Service** / **Stop Service** setup buttons now manage the
Collector and enabled analyzer tasks together. Windows Services alone controls the
Collector service; it is not a service manager for the separate scheduled tasks.
Unchecking the analyzer option during Install / Update stops/disables their managed
startup while retaining binaries/configuration/history. Rechecking installs/enables
them again. Managed settings are recorded in `WatcherCollector\analyzers.json`.

Check:

```powershell
Get-Service WatcherCollector
Get-ScheduledTask -TaskName Watcher-Connection-Analyzer,Watcher-Behavior-Analyzer
Get-Content C:\ProgramData\WatcherConnectionAnalyzer\analyzer.log -Tail 20
Get-Content C:\ProgramData\WatcherAnalyzer\analyzer.log -Tail 20
```

## Updates and existing installations

Collector settings such as custom DB/CSV/log paths, export interval, client limit
and unknown settings are now preserved. The visible port/network fields still apply
your chosen edits. Analyzer thresholds, custom state/DB/alert paths and unknown
settings are preserved too; source DB paths are bound to the managed Collector.
Changed configurations receive `.bak` backups and are replaced via staged writes.
Unreadable JSON is reported instead of being replaced with defaults.

Setup discovers analyzer configuration at the managed paths above. If a module was
previously deployed elsewhere, stop its independently managed task/process and copy
its configuration to the managed `config.json` before installing; preserve its
existing absolute state/DB paths. Setup does not kill unrelated analyzer deployments.
An already-running module using the same state is detected by its lock and must be
stopped before preflight can proceed.

Do not reuse an analyzer state DB for a different Collector dataset. Source identity
checks intentionally reject that. Preserve the old state and choose a fresh output
DB/state directory when intentionally changing datasets. Custom shared alert paths
are preserved; the task's SYSTEM identity needs the appropriate share permissions
(computer-account access) or a separately managed task identity.

Analyzer startup/availability is checked by process, not by completion of historical
backfill. If setup fails, configuration backups and databases remain; setup attempts
to restart a configured Collector. Inspect its error and module logs before retrying.
This is a per-file update, not an atomic rollback of the whole installation.

## Linux: one bundle and one setup command

Build on a compatible Linux amd64 machine with .NET 10 SDK:

```bash
bash Collector/build/build-linux.sh
```

Copy the **whole** `Collector/output/linux-collector` folder to the server. It contains
Collector, both analyzers and the installation/removal helpers. Setup needs Python 3,
systemd, `useradd`/`groupadd`, and `runuser` (Debian's standard account/util-linux
utilities), plus the native libraries described in [Linux deployment](../LINUX-DEPLOYMENT.md).
Only the build machine needs the SDK.

```bash
sudo bash ./install-linux.sh --port 6515 --allow-cidr 192.168.10.0/24 --allow-cidr 10.8.0.0/24
```

Run that command from the copied bundle folder. Network/port arguments are optional;
on updates omitted values retain their configuration. Fresh installs default to
all IPv4 interfaces, TCP 6515 and no IP allowlist. Configure firewall/CIDR restrictions
for the intended management network; the protocol remains plaintext/unauthenticated.

Setup creates/reuses `watcher-collector`, installs programs under `/opt/watcher-collector`,
creates configurations and writable state directories, initializes all three DBs,
registers/enables services and starts them:

| Service | Config | Default state |
|---|---|---|
| `watcher-collector.service` | `/etc/watcher-collector/collector.json` | `/var/lib/watcher-collector` |
| `listener-connection-analyzer.service` | `/etc/watcher-connection-analyzer/config.json` | `/var/lib/watcher-connection-analyzer` |
| `listener-analyzer.service` | `/etc/watcher-analyzer/config.json` | `/var/lib/watcher-analyzer` |

The analyzer units follow Collector stop/restart operations. Starting Collector pulls
in the enabled modules. Their stdout/stderr is available in `journalctl`. Service
bundle extraction uses writable state paths and does not require a writable home.
Existing config/state/alert paths are preserved; custom locations need existing
service-account permissions. Default directories are owned/configured by setup.

```bash
sudo systemctl status watcher-collector listener-connection-analyzer listener-analyzer --no-pager
sudo journalctl -u listener-connection-analyzer -n 20 --no-pager
```

Setup writes `/etc/watcher-collector/gui.env` with the actual Collector and analysis
DB paths. For a GUI launched from a shell with permission to read that file:

```bash
set -a
. /etc/watcher-collector/gui.env
set +a
# Launch the separately deployed GUI in this environment.
```

A systemd GUI can use this file as `EnvironmentFile`. Its account must have read
access to both DBs, their directories and SQLite WAL/SHM files. Setup does not change
an unrelated GUI service or automatically expose it to the network.

Options:

- `--without-analyzers`: disable managed analyzer startup; preserve existing data.
- `--with-analyzers`: enable them again. Otherwise updates retain the saved choice.
- `--no-start`: install/enable units for boot, without starting them immediately.
- `--stage-root DIR`: stage files/configs for testing/packaging without changing host
  accounts/services or running preflight; this is not a production installation.

## Uninstall

Windows: use **Uninstall Collector**. It removes the service, bundled programs,
analyzer tasks and managed firewall rules. Analyzer configs, state, logs and alerts
are always retained. The existing optional data-deletion checkbox affects only the
Collector's own ProgramData directory; external DBs and CSVs remain preserved.

Linux:

```bash
sudo bash /opt/watcher-collector/uninstall-linux.sh
```

This stops/disables/removes the three managed services and program directory. It
preserves all configuration, databases, CSVs and alerts. The account is retained
because other Listener components may use it. Explicit data cleanup remains a
separate administrative operation.

## Verification

```bash
dotnet run --project tests/CollectorSetup -c Release
python tests/test_collector_setup.py
```

Tests cover config/state preservation, malformed config rejection, Windows task XML,
persistent logs, all three embedded Windows payloads, Linux staging/update/removal,
service command order using a fake host, and real analyzer initialization without
history/alert delivery. Native bundle checks exercise the generated configs with the
real Collector/analyzers. Actual Windows scheduling and live systemd activation must
be verified on target hosts.
