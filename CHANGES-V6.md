# Watcher V6 release notes

- Added **Uninstall Agent** to the Windows Watcher Agent GUI installer.
- Added **Uninstall Collector** to the Windows Watcher Collector GUI installer.
- Uninstall actions require elevation and explicit confirmation.
- Agent task + installed process and binary removed; agent configuration and event spool preserved by default.
- Collector Windows service + binary + matching Watcher firewall rules removed; collector database, config and logs preserved by default.
- Each installer has an optional irreversible ProgramData cleanup checkbox (unchecked by default).
- CSV exports always retained by the collector uninstall.
- No changes to the tokenless/plain-TCP Watcher V5 event protocol.
- Source distribution only; .NET 10 SDK is necessary to build installers on Windows.
