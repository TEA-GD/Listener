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

## Launch and stop controls

The Agent installer now has **Launch Agent** and **Stop Agent** buttons alongside Install / Update and Uninstall Agent, matching the Collector's start/stop workflow.

- **Launch Agent** starts the existing `Watcher-Connection-Agent` scheduled task with the installed executable and saved `agent.json`. It does not apply unsaved setup-field edits. An already-running installed agent is left running, and the log reports whether the process could be confirmed. Starting the process does not confirm Collector delivery.
- **Stop Agent** ends the startup task's current run and stops only a WatcherAgent process whose executable path matches the installed agent. It keeps the task registered, preserves configuration and unsent events, and verifies that the process stopped. The agent can start again with Launch Agent or at the next Windows startup.

Commands run asynchronously, with other installer actions disabled until completion. Use the rebuilt installer elevated; an agent must already be installed before Launch Agent can work. These controls do not uninstall the agent or change monitoring settings.

Portable lifecycle regression checks: `dotnet run --project tests/AgentControls -c Release`. Actual Task Scheduler/process control and button layout require verification on a Windows test PC.

## Device category

Setup now requires a **Device type** selection: Linux servers, Windows servers, User PCs, or Project Stands. Updates load the saved choice; older configurations without a choice require selection once. Future events are tagged with that category before entering the spool. Updates preserve queued events with their original category. Update the Collector first. See [device storage and migration](../DEVICE-STORAGE.md).
