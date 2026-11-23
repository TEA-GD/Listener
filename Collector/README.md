Watcher V6 Windows Collector installer includes Install / Update and Uninstall Collector buttons. See ../README.md for instructions.

Linux Collector deployment is available as a self-contained Linux amd64 build with `bash Collector/build/build-linux.sh` from the repository root. Follow [Linux installation, service configuration and uninstall](../LINUX-DEPLOYMENT.md); the default project build and Windows installer remain Windows targets.

Mail collection now uses one attachment-only add-on → local compiled Agent → Collector route. Startup migrates attachment metadata into `mail_information` and drops the two legacy mail tables, intentionally removing non-attachment mail and old subject/body fields. Back up consistently and stop old receiver deployments before this upgrade. See [replacement and migration instructions](../Thunderbird_Attachments/README.md).
