# Listener Mail Metadata Receiver

Separate metadata-only companion to [Thunderbird_Addon_Metadata](../Thunderbird_Addon_Metadata/README.md). Both receiver variants share the mail database schema while keeping their collection contracts separate.

## Data contract

Accepted mail fields: `message_date_utc`, `sender`, `recipients`, `has_attachments`, `attachment_names`. Operational fields: `alert_id`, `client_id`, `client_label`, `account_id`, `message_key`.

Unknown JSON properties are rejected with HTTP 400, including `subject`, `body_text`, `text_parts` and attachment-content fields. The storage schema has no subject/body columns. Do not enable HTTP request-body logging upstream.

## Build and run

Requires .NET 10 SDK when building. On Windows, run `Build-Windows.bat` for a self-contained executable. Create a protected key file with a random secret of at least 32 characters, then:

```powershell
.\Start-Windows.ps1 -InitializeKey -InternalDomains 'example.com' -TokenFile 'C:\ProgramData\WatcherMailMetadata\receiver.key'
```

The script defaults to `http://127.0.0.1:6517` and `%ProgramData%\WatcherCollector\watcher-events.db`. It initializes both mail tables and can create the database before the TCP collector starts. The Collector adds connection tables on its own startup. Use `-DatabasePath` and `-ListenUrl` to override. For remote PCs use an HTTPS reverse proxy or configure trusted HTTPS directly, restrict access to your network, and supply its URL in the add-on options.

Cross-platform example (set `LISTENER_MAIL_TOKEN` or `LISTENER_MAIL_TOKEN_FILE` in your service environment first):

```bash
dotnet run --project Listener_MailReceiver_Metadata.csproj -- \
  --DatabasePath /var/lib/listener/mail-metadata.db \
  --InternalDomains example.com --urls http://127.0.0.1:6517
```

`GET /health` checks service availability. Authenticated `POST /api/mail-metadata` acknowledges stored/duplicate/ignored-internal alerts. The original `/api/mail-alerts` endpoint is not available. Failed writes return an error so metadata stays queued for retry. As in the original, sender filtering uses the reported From field; it is not proof of sender authenticity.

## View/export the five fields

Open the database with a SQLite viewer or use the SQLite CLI:

```sql
SELECT message_date_utc AS "Date",
       sender AS "Sender",
       recipients_json AS "Receiver",
       CASE has_attachments WHEN 1 THEN 'yes' ELSE 'no' END AS "Has attachments",
       attachment_names_json AS "Attachment filenames"
FROM email_metadata_alerts
ORDER BY received_utc DESC;
```

Example CSV export in the SQLite CLI:

```text
.headers on
.mode csv
.once mail-metadata.csv
```

Then execute the query above. Receiver and attachment filename cells contain JSON arrays, preserving individual values and filenames safely.

In the updated dashboard, select **Metadata only** in the **Mail variant** selector. Configure a mail viewing key as described in [the full receiver setup](../Listener_MailReceiver/README.md#mail-dashboard-key). Both mail tables are initialized automatically, and existing rows are preserved. It does not read, alter or purge historical full-text records. To stop full-text collection, disable the original Thunderbird add-on/receiver; installing this separate pair does not disable them automatically.

## Upgrade from metadata-only 1.0.0

The old metadata receiver defaulted to `%ProgramData%\WatcherMailMetadata\mail-metadata.db`. The new default is the shared `%ProgramData%\WatcherCollector\watcher-events.db`. Existing files are not moved or merged automatically. To keep using your previous metadata database, pass its path with `-DatabasePath` to both receiver and dashboard. Otherwise new alerts go to the shared database and historical metadata remains in the old file. Back up all databases before replacing binaries; stop writers before making a filesystem backup (including any SQLite WAL files).

Use `-InitializeKey` for first-time key creation only; an existing key is never rotated. Keep the repository's `Shared/` folder when running from source. The launch account needs permission to update the shared database; a different service account also needs read access to its protected key.
