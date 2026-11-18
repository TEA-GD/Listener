Watcher V6 Windows Agent installer includes Install / Update and Uninstall Agent buttons. See ../README.md for instructions.

TCP and UDP socket monitoring: see [UDP.md](UDP.md) for available fields, limitations and upgrade order.

For the device overview, the agent now queues a full TCP/UDP snapshot on startup and on every heartbeat (normally once a minute). A completion heartbeat declares the expected row count; partial deliveries are not displayed as complete. Upgrade the Collector before installing this agent, because `TCP_SNAPSHOT` and `UDP_SNAPSHOT` are new event types. Existing open/close monitoring and ACK-based retries are retained. Snapshot records increase stored event/CSV volume.

## Updating without uninstalling

Download the current repository, rebuild `Agent/build/Build-Windows.bat` with .NET 10 SDK, and run the newly built `Agent/output/windows-installer/WatcherInstaller.exe` elevated. Select **Install / Update**. Reusing an older installer executable will reuse the old update behavior.

The installer repairs ownership and permissions on its ProgramData directory and existing `agent.json`, removes the config's read-only attribute, and grants access only to SYSTEM and Administrators. It recovers access before loading existing settings, so a permissions error does not silently replace the saved configuration with defaults. Updates preserve unknown config settings and any custom `spool_dir`; pending events are not deleted. Only the installed WatcherAgent process is stopped, and complete staged files replace the binary and configuration atomically. If an update fails after stopping the agent, the installer attempts to restart the existing scheduled task. A failed staging/replacement leaves that file's previous contents intact.

Do not uninstall or select data deletion to work around `agent.json` permissions. The updated installer handles the normal legacy ACL/read-only cases in place. A genuinely corrupt configuration, linked install path, security product block or unrecoverable permissions failure is reported rather than overwriting unreadable settings.

Regression checks:

```powershell
dotnet run --project tests/InstallerUpdate -c Release
```

Run from the repository root in an elevated Windows terminal to exercise the actual explicit-deny NTFS ACL and sharing-lock checks. Portable preservation/replacement checks also run on Linux; Linux cannot validate the Windows ACL repair itself.
