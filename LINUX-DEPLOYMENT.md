# Install and uninstall Listener on Linux servers

Use this procedure for **Debian 12 or Debian 13 on amd64 with systemd**. Install the Collector first, then the Agents. The Collector receives events on TCP 6515; each Agent monitors its own machine. Both can run on the same server. The GUI and the Thunderbird add-on are separate components; mail uses the compiled Agent loopback bridge.

The Agent has a Debian package. The Collector uses a self-contained Linux build and the systemd unit supplied in `Collector/linux/`; there is no Collector `.deb`. The opt-in Linux build leaves the normal Windows build unchanged. Ubuntu, RHEL and ARM package installation is not covered by these steps.

## 1 Prepare a build machine

Build on the same Debian release as the target. You can build on the target server, or copy the complete Collector publish folder and Agent `.deb` to matching servers. Only the build machine needs the .NET **10 SDK**, Git and NuGet access. Self-contained applications still require native OS libraries.

```bash
cat /etc/os-release
dpkg --print-architecture
sudo apt update
sudo apt install -y ca-certificates curl git iproute2 sqlite3 libstdc++6 libgcc-s1 zlib1g
```

Install the runtime prerequisites matching that release:

```bash
# Debian 12 only:
sudo apt install -y libicu72 libssl3

# Debian 13 only (do not run the Debian 12 line above):
sudo apt install -y libicu76 libssl3t64
```

If the .NET 10 SDK is not installed, the [official installer](https://learn.microsoft.com/dotnet/core/tools/dotnet-install-script) can install it for your build account:

```bash
curl -fsSL https://dot.net/v1/dotnet-install.sh -o /tmp/listener-dotnet-install.sh
bash /tmp/listener-dotnet-install.sh --channel 10.0 --install-dir "$HOME/.dotnet"
export DOTNET_ROOT="$HOME/.dotnet"
export PATH="$DOTNET_ROOT:$PATH"
dotnet --list-sdks
```

Keep those exports in the shell used for building. Obtain the complete repository, including `Shared/`:

```bash
git clone https://github.com/TEA-GD/Listener.git
cd Listener
```

The following build commands run from this repository root. In an existing checkout, fetch the current source with `git pull --ff-only` first; preserve local configuration separately.

## 2 Build the Linux Collector

```bash
bash Collector/build/build-linux.sh
```

Output: `Collector/output/linux-collector/WatcherCollector` and any accompanying publish files. Copy the **whole folder** if building elsewhere. Internally the script sets `BuildForLinux=true`, selecting `net10.0` and `linux-x64`; the project's default remains Windows.

## 3 Install and configure the Collector

Run on the Collector server from the repository root, with the publish folder available at the path above. These initial-install commands copy an example configuration; during an upgrade, preserve the existing file instead.

```bash
id -u watcher-collector >/dev/null 2>&1 || \
  sudo useradd --system --user-group --home-dir /var/lib/watcher-collector \
    --no-create-home --shell /usr/sbin/nologin watcher-collector

sudo install -d -m 755 /opt/watcher-collector
sudo cp -a Collector/output/linux-collector/. /opt/watcher-collector/
sudo chown -R root:root /opt/watcher-collector
sudo chmod 755 /opt/watcher-collector/WatcherCollector
sudo install -d -o root -g watcher-collector -m 750 /etc/watcher-collector
sudo install -o root -g watcher-collector -m 640 \
  Collector/linux/collector.example.json /etc/watcher-collector/collector.json
sudo install -d -o watcher-collector -g watcher-collector -m 750 \
  /var/lib/watcher-collector /var/log/watcher-collector
sudo install -o root -g root -m 644 \
  Collector/linux/watcher-collector.service /etc/systemd/system/watcher-collector.service
sudo nano /etc/watcher-collector/collector.json
```

Use Linux paths, not the Windows example configuration:

```json
{
  "bind_host": "0.0.0.0",
  "bind_port": 6515,
  "database_path": "/var/lib/watcher-collector/watcher-events.db",
  "csv_dir": "/var/lib/watcher-collector/csv",
  "log_file": "/var/log/watcher-collector/collector.log",
  "csv_export_seconds": 10,
  "max_clients": 64,
  "allowed_client_cidrs": ["127.0.0.1/32", "192.168.10.0/24", "10.8.0.0/24"]
}
```

**Replace the example LAN and VPN ranges with yours before starting.** The allowlist matches the source IP seen by the Collector, which may be a VPN/NAT gateway address. `127.0.0.1/32` allows an Agent on this same server to use the loopback address. An empty list disables Collector source filtering; `0.0.0.0/0` is rejected. `0.0.0.0` binds all IPv4 interfaces; use a specific local address if appropriate.

The transport is plain TCP without authentication or encryption. Permit it only on your management LAN or protected VPN, with the host/network firewall restricting access. If UFW is already your firewall, example inbound rules are:

```bash
sudo ufw allow from 192.168.10.0/24 to any port 6515 proto tcp
sudo ufw allow from 10.8.0.0/24 to any port 6515 proto tcp
```

Use your actual ranges. This does not enable UFW or alter SSH rules. For nftables/firewalld or a centrally managed firewall, add equivalent source-restricted rules using that system. Agents need outbound TCP to the Collector; they need no inbound listener port.

Check the configuration as the service account, then start:

```bash
sudo -u watcher-collector env \
  DOTNET_BUNDLE_EXTRACT_BASE_DIR=/var/lib/watcher-collector/.net \
  /opt/watcher-collector/WatcherCollector \
  --config /etc/watcher-collector/collector.json --check-config

sudo systemctl daemon-reload
sudo systemctl enable --now watcher-collector.service
sudo systemctl status watcher-collector.service --no-pager
sudo journalctl -u watcher-collector.service -n 50 --no-pager
sudo ss -ltnp 'sport = :6515'
```

Expect `CONFIG OK`, then an active service and a `STARTED PLAIN TCP` log entry. `--check-config` also creates/migrates the database; it does not start the listener or check firewall reachability. The service runs as the dedicated account. Its writable bundle-extraction directory is inside its state directory, so it does not depend on access to a home directory.

The current GUI reads SQLite directly. A GUI installed on another machine will not automatically see this Linux database. Run it alongside the database with the correct `WATCHER_DB_PATH` and read permissions, or use an appropriate separate data-access architecture; do not share a live SQLite file over a network filesystem as a substitute for deployment planning. This guide does not automatically move an existing Windows database or install the GUI/Thunderbird add-on.

## 4 Build and install an Agent on each Linux device

From the source checkout on a matching Debian build machine:

```bash
bash Agent/build/build-linux.sh
dpkg-deb -f Agent/output/watcher-agent_1.0.4_amd64.deb Version Depends
```

The script rejects distributions other than Debian 12/13 and architectures other than amd64. The independent-upload-worker package is version 1.0.4. Build from current source; it upgrades the older 1.0.3 package without removing configuration or pending events.

On each target device, install the newly built package. If you copied it to another directory, adjust the path:

```bash
sudo env WATCHER_DEVICE_CATEGORY=linux_servers \
  apt install ./Agent/output/watcher-agent_1.0.4_amd64.deb
sudo systemctl stop watcher-agent.service
sudo nano /etc/watcher-agent/agent.json
```

Use `linux_servers`, `windows_servers`, `user_pcs` or `project_stands` as appropriate. The package starts automatically; stop it while setting the real destination. The explicit environment variable also works for noninteractive installation. Existing category choices survive upgrades.

Example configuration:

```json
{
  "agent_id": "linux-server01.example.local",
  "device_category": "linux_servers",
  "spool_dir": "/var/lib/watcher-agent/spool",
  "collector_host": "collector.example.local",
  "collector_port": 6515,
  "collect_udp": true
}
```

Give every device a unique hostname-style `agent_id`. Use a DNS hostname or IP, without `http://`, path or appended port. On the Collector server itself you may set `collector_host` to `127.0.0.1` with the loopback allowlist entry shown above. Preserve existing identity and spool paths when updating.

```bash
sudo chmod 600 /etc/watcher-agent/agent.json
sudo systemctl daemon-reload
sudo systemctl enable --now watcher-agent.service
sudo systemctl restart watcher-agent.service
sudo systemctl status watcher-agent.service --no-pager
sudo journalctl -u watcher-agent.service -n 50 --no-pager
```

The Agent runs as root to observe socket/process metadata. Its systemd unit restricts filesystem writes to `/var/lib/watcher-agent` and places single-file runtime extraction in `/var/lib/watcher-agent/.net`. Keep the default spool path unless you also configure a matching systemd writable-path override.

`iproute2` provides `ss`. NetworkManager (`nmcli`) is optional for Linux network/profile names; systemd supplies service-name observations. Missing metadata/permissions can produce partial context samples. Ethernet has no SSID, and Wi-Fi profile names may be unavailable.

## 5 Confirm data delivery

On an Agent device, replacing the destination with your configured Collector:

```bash
getent ahostsv4 collector.example.local
# Optional TCP reachability tool:
sudo apt install -y netcat-openbsd
nc -vz collector.example.local 6515
sudo journalctl -u watcher-agent.service -f
```

Look for `Acknowledged:` lines. A successful TCP connection alone does not prove event acceptance. On the Collector:

```bash
sudo tail -n 50 /var/log/watcher-collector/collector.log
sudo sqlite3 -readonly /var/lib/watcher-collector/watcher-events.db \
  'SELECT MONITORED_HOST,COUNT(*) FROM events GROUP BY MONITORED_HOST;'
```

Count pending files on an Agent:

```bash
sudo find /var/lib/watcher-agent/spool -maxdepth 1 -type f -name '*.json' | wc -l
```

After an outage, old files replay with original timestamps and are removed only after matching durable ACKs. Over a few minutes the count should trend down while catching up, with increases when new snapshots are created. The compiled Windows/Linux Agent uploads in a background worker, independently of sampling, and drains successive passes without a collection-loop pause. It retries failed sends after five seconds. The compiled Agent buffer is limited to 50,000 events; full capacity preserves existing files but prevents new collection. Update the Collector before Agents to support current event types and pipelined VPN delivery. Check source allowlists against actual VPN/NAT addresses if TCP connects but events are rejected.

## 6 Upgrade without deleting data

Build fresh artifacts from the updated repository. Back up the existing database consistently; stop all writers to that database before a filesystem backup. `sqlite3 .backup` is an alternative that accounts for SQLite WAL state.

For the manually installed Collector:

```bash
sudo systemctl stop watcher-collector.service
sudo cp -a Collector/output/linux-collector/. /opt/watcher-collector/
sudo chown -R root:root /opt/watcher-collector
sudo install -m 644 Collector/linux/watcher-collector.service \
  /etc/systemd/system/watcher-collector.service
sudo systemctl daemon-reload
sudo systemctl start watcher-collector.service
```

Keep `/etc/watcher-collector/collector.json` and `/var/lib/watcher-collector`. Startup applies supported schema migrations. Recheck logs before updating Agents.

For an Agent, install version 1.0.4 over the old package. If reinstalling a rebuilt package with the same version, use:

```bash
sudo env WATCHER_DEVICE_CATEGORY=linux_servers \
  apt install --reinstall ./Agent/output/watcher-agent_1.0.4_amd64.deb
```

Existing configuration and queued events are retained. Confirm the destination and restart the service. Do not purge or delete the spool to work around a delivery problem.

## 7 Uninstall the Agent and retain its data

First confirm pending files are delivered, or back up the configuration and spool if they must be retained for later replay. Removing the program stops further delivery.

```bash
sudo systemctl stop watcher-agent.service
sudo apt remove watcher-agent
sudo systemctl daemon-reload
```

The package removal disables/stops the unit and removes the packaged executable/unit. It retains `/etc/watcher-agent/agent.json` and `/var/lib/watcher-agent`, including unsent files. Custom spool paths and systemd overrides also remain. Reinstalling with the same identity/spool allows delivery to resume.

**Optional full data removal — irreversible:** back up anything needed before running these commands. Package purge removes the main config, but does not itself delete the spool; the last command explicitly does so.

```bash
sudo apt purge watcher-agent
sudo rm -rf -- /etc/watcher-agent /var/lib/watcher-agent
```

Review any custom spool path or `/etc/systemd/system/watcher-agent.service.d` overrides separately; these commands do not remove them. Existing journal entries follow the operating system's journal retention policy.

## 8 Uninstall the Collector and retain its database

These commands apply to the manual installation in this guide:

```bash
sudo systemctl disable --now watcher-collector.service
sudo rm -f -- /etc/systemd/system/watcher-collector.service
sudo systemctl daemon-reload
sudo rm -rf -- /opt/watcher-collector
```

The configuration, database, CSV exports and file logs remain in `/etc/watcher-collector`, `/var/lib/watcher-collector` and `/var/log/watcher-collector`. The dedicated account is retained for stable ownership on reinstall. Agents will buffer while no Collector is reachable, subject to their capacity limits.

If UFW rules were added solely for this deployment, remove only those specific rules. Do not remove shared LAN/VPN rules used by other services.

**Optional full data removal — irreversible:** after stopping the Collector and any other writers, and retaining any required backups:

```bash
sudo rm -rf -- /etc/watcher-collector /var/lib/watcher-collector /var/log/watcher-collector
sudo userdel watcher-collector
```

Do not remove the account if another component was configured to use it. These commands do not uninstall the separately managed GUI/Thunderbird add-on or erase copies/backups stored elsewhere.
