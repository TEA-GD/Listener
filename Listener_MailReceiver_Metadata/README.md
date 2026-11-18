# Listener Mail Metadata Receiver

Separate metadata-only companion to [Thunderbird_Addon_Metadata](../Thunderbird_Addon_Metadata/README.md). The original full-text receiver is unchanged.

## Data contract

Accepted mail fields: `message_date_utc`, `sender`, `recipients`, `has_attachments`, `attachment_names`. Operational fields: `alert_id`, `client_id`, `client_label`, `account_id`, `message_key`.

Unknown JSON properties are rejected with HTTP 400, including `subject`, `body_text`, `text_parts` and attachment-content fields. The storage schema has no subject/body columns. Do not enable HTTP request-body logging upstream.

## Build and run

Requires .NET 10 SDK when building. On Windows, run `Build-Windows.bat` for a self-contained executable. Create a protected key file with a random secret of at least 32 characters, then:

```powershell
.\Start-Windows.ps1 -InternalDomains 'example.com' -TokenFile 'C:\ProgramData\WatcherMailMetadata\receiver.key'
```

The script defaults to `http://127.0.0.1:6517` and `%ProgramData%\WatcherMailMetadata\mail-metadata.db`. It creates its own database and does not require the TCP collector. Use `-DatabasePath` and `-ListenUrl` to override. For remote PCs use an HTTPS reverse proxy or configure trusted HTTPS directly, restrict access to your network, and supply its URL in the add-on options.

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

The original dashboard reads `email_alerts`, not `email_metadata_alerts`. No change to that dashboard is included. You can point this receiver at an existing Collector database if desired: it creates only its own table. It does not read, alter or purge historical full-text records. To stop full-text collection, disable the original Thunderbird add-on/receiver; installing this separate pair does not disable them automatically.
