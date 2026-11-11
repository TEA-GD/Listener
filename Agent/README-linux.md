# Linux agent: Debian 12 and Debian 13 amd64

The package builder targets the Debian release on the build machine. Build on
**the same Debian release as the server**. It rejects unsupported distributions,
releases and architectures before publishing. Ubuntu/RHEL/ARM packaging is not
provided by this script.

## Correcting the libicu72 installation error

Version 1.0.0 was hard-coded for Debian 12. On Debian 13 the ICU/OpenSSL packages
are named `libicu76` and `libssl3t64`, so APT cannot install that older package.
Version 1.0.1 automatically chooses the appropriate dependencies:

| Build/target release | ICU | OpenSSL |
| --- | --- | --- |
| Debian 12 | libicu72 | libssl3 |
| Debian 13 | libicu76 | libssl3t64 |

Do not force installation with missing dependencies or add another Debian
release's repositories. Refresh the source and rebuild on the target release.

## Build and install

Check your server:

```bash
cat /etc/os-release
dpkg --print-architecture
```

You need Debian 12/13 amd64, .NET 10 SDK, `dpkg-deb` and access to NuGet for the
build. The package is self-contained; the server does not need an SDK to run it
when you build the package on another machine of the same release.

From the existing repository root:

```bash
git pull --ff-only
bash Agent/build/build-linux.sh
dpkg-deb -f Agent/output/watcher-agent_1.0.1_amd64.deb Version Depends
sudo apt install ./Agent/output/watcher-agent_1.0.1_amd64.deb
```

APT installs the declared dependencies from your configured repositories. If it
fails, the systemd unit is not installed: resolve the package error first.

Edit the collector destination, preserving any existing identity/spool settings:

```bash
sudo nano /etc/watcher-agent/agent.json
```

Example:

```json
{
  "agent_id": "linux-server01.example.local",
  "spool_dir": "/var/lib/watcher-agent/spool",
  "collector_host": "collector.example.local",
  "collector_port": 6515
}
```

Use a unique `agent_id` on each PC. The package preserves an existing config on
upgrade. A fresh install generates a hostname identity and default destination;
set your real collector hostname and restart the service.

```bash
sudo systemctl daemon-reload
sudo systemctl enable --now watcher-agent
sudo systemctl restart watcher-agent
sudo systemctl status watcher-agent --no-pager
sudo journalctl -u watcher-agent -n 30 --no-pager
```

Expect `active (running)` and `Acknowledged:` messages. Autostart is configured
by systemd. Queued events remain in `/var/lib/watcher-agent/spool` during outages.
The collector service and network/firewall must permit TCP port 6515.

## Package validation

After building:

```bash
python tests/test_linux_package.py
apt-get -s install ./Agent/output/watcher-agent_1.0.1_amd64.deb
```

Tests extract the package without installing it, check service/script paths and
permissions, run the packaged binary, and verify heartbeat delivery plus ACK
cleanup against a local test TCP receiver. APT `-s` simulates dependency solving;
it does not install or enable the service. Real systemd startup and reboot checks
must still be performed on the target server.
