# Attachment only Thunderbird mail collection

There is one add-on: **Listener Attachment Mail**, ID `listener-mail-attachments@tea-gd.local`, for Thunderbird 140 or later. It reports newly observed incoming mail **only when Thunderbird lists at least one attachment**. Internal and external senders are both included. Sent/draft/outbox/template folders are excluded.

The route is Thunderbird → authenticated loopback HTTP on the **compiled Agent** → the existing durable spool → Collector TCP 6515 → `mail_information`. There are no standalone mail receivers. The PowerShell ServerAgent remains a connection/network sampler; use the compiled Agent for mail collection.

## Fields

- Date/time when Thunderbird notified the add-on of new mail, in UTC.
- Sender and visible To/Cc/Bcc recipients.
- Attachment count, names, MIME type (or extension fallback) and size in bytes when Thunderbird exposes it. Zero bytes is a known size; `null` means unknown.

No subject, body, attachment content, command lines or credentials are collected. Receipt time is the add-on's arrival-notification time, not the Date header and not proof of the original IMAP/SMTP server delivery time. Mail downloaded after Thunderbird was closed may be observed later. Collection begins when enabled; there is no historical mailbox scan.

## Replace old installations

1. Stop/remove the old `Listener_MailReceiver` and `Listener_MailReceiver_Metadata` deployments before upgrading the Collector. Do not run those old binaries against the updated database.
2. Remove both old Thunderbird extensions, including from any forced-install GPO: `listener-mail-alerts@tea-gd.local` and `listener-mail-metadata@tea-gd.local`. Removing source files does not uninstall deployed add-ons. Remove old XPI download locations/policies and uninstall or disable those IDs on each client before enabling the replacement.
3. Back up the shared database consistently. Start the updated Collector. Its transaction creates `mail_information`, migrates only old attachment-bearing metadata, then drops `email_alerts` and `email_metadata_alerts`. Non-attachment records, subjects and bodies are intentionally removed. Existing connection/process/network tables remain intact.
4. Update the compiled Agent using **Install / Update**, retaining its existing configuration/spool. Configure the loopback endpoint below, then restart it.
5. Build/install the single new XPI and enable it after entering the same local Agent token.

Historical variants did not record attachment sizes or a trustworthy Thunderbird receipt time. Migrated rows therefore have unknown receipt date and sizes, `date_source=legacy_unknown`, with the old Collector receipt retained as `collected_utc`. Names and recipients are retained. Standalone old mail database files are not automatically discovered or merged; only the database opened by this Collector is migrated. Old backups/WAL copies or Thunderbird extension storage are not securely erased by dropping SQL tables; manage retained copies separately if required.

## Configure the Windows Agent

Run PowerShell as Administrator on the Thunderbird device. Generate a different local bridge token per device; do not publish it in GPO download URLs or source control.

```powershell
Stop-ScheduledTask -TaskName 'Watcher-Connection-Agent'
$path = 'C:\ProgramData\WatcherAgent\agent.json'
$config = Get-Content $path -Raw | ConvertFrom-Json
$keyBytes = New-Object byte[] 32
$rng = [Security.Cryptography.RandomNumberGenerator]::Create()
$rng.GetBytes($keyBytes)
$rng.Dispose()
$key = [Convert]::ToBase64String($keyBytes)
$config | Add-Member -NotePropertyName mail_token -NotePropertyValue $key -Force
$config | Add-Member -NotePropertyName mail_port -NotePropertyValue 58766 -Force
$config | ConvertTo-Json -Depth 10 | Set-Content $path -Encoding UTF8
Start-ScheduledTask -TaskName 'Watcher-Connection-Agent'
$key  # Enter this value only in the local add-on settings; keep it private.
```

This preserves the saved Collector destination, device type and spool path. An empty/missing `mail_token` disables the endpoint. Use at least 32 characters. The default port is 58766, bound only to `127.0.0.1`; do not open it on the LAN. SYSTEM has the privileges needed for the Windows HTTP listener. Linux compiled Agents can use the same two JSON settings in `/etc/watcher-agent/agent.json`, then `sudo systemctl restart watcher-agent`; protect the config as before. Set a different high port if another application uses this one, and match it in the add-on.

## Build and install

From the current repository root:

```bash
python Thunderbird_Attachments/build.py
```

Install `Thunderbird_Attachments/packages/listener-attachment-mail-1.0.0.xpi` through Thunderbird Add-ons Manager → Install Add-on From File. Open its settings, enter the local Agent port and token, enable collection, and save. `Build-Windows-All.bat` now packages this add-on only and no longer builds old receivers. Enterprise force-install policies should point only at the new ID/XPI; token provisioning is per profile/device, through the add-on settings.

Send a test email with a small attachment. The add-on retains metadata in IndexedDB until the Agent acknowledges its flushed local spool write. It then keeps only a deduplication marker. The Agent retains that event until the Collector durably acknowledges it; Collector replay uses the alert GUID for deduplication. If the Agent or Collector is offline, the corresponding queue retries. Do not uninstall extensions, delete their storage, or delete the Agent spool while undelivered data is needed.

The local request is limited to 16 KiB, 100 recipients and 100 attachments, with filenames/formats at most 256 characters. The normalized event must also fit the existing 30,000-byte spool limit; unusually large metadata reports are rejected with HTTP 413 and stay queued with a visible error. Attachment contents are never fetched to calculate a missing size. Correct invalid/oversize reports before expecting delivery. The existing Agent spool capacity remains finite.

## GUI and storage

The single table is `mail_information`: `alert_id`, `host`, `date_received_utc`, `sender`, `recipients_json`, `attachment_count`, `attachments_json`, plus Collector receipt/provenance fields. Attachment JSON contains only `name`, `format` and nullable `size_bytes`. No legacy subject/body columns are created.

The GUI Mail alerts page shows only these fields. Configure a separate viewing token using `LISTENER_MAIL_VIEW_TOKEN` or the launcher's existing `-MailViewKeyFile`. The viewing key and local Agent mail token serve different purposes; the GUI key does not authorize mail submission. Collector transport remains plain TCP, so keep its existing protected LAN/VPN and source restrictions.

Example protected GUI viewing-key file, created as Administrator on the GUI server (separate from every Agent token):

```powershell
$viewFile = 'C:\ProgramData\WatcherCollector\mail-view.key'
New-Item -ItemType Directory -Path (Split-Path $viewFile) -Force | Out-Null
$viewKey = [Guid]::NewGuid().ToString('N') + [Guid]::NewGuid().ToString('N')
[IO.File]::WriteAllText($viewFile, $viewKey, [Text.UTF8Encoding]::new($false))
icacls.exe $viewFile /inheritance:r /grant:r '*S-1-5-18:F' '*S-1-5-32-544:F'
.\Listener_DB_GUI\Start-Windows.ps1 -MailViewKeyFile $viewFile
```

The GUI server account must be permitted to read that file. Supply the viewing key only to authorized mail viewers; never use it as the local Agent submission token.

If legacy JSON metadata is invalid, migration fails and its transaction preserves the old tables. Repair the invalid records against a backup and restart; migration never drops a table after silently skipping invalid metadata.
