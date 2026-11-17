# Listener Mail Receiver

Separate .NET 10 HTTP service for the [Thunderbird add-on](../Thunderbird_Addon/README.md).
It stores external-mail alerts in a new `email_alerts` table in the existing
collector database. It does not alter TCP event rows or send them to the analyzer.
The updated dashboard has a separate, key-protected Mail alerts view.

## Windows setup

Run setup on the collector PC. First start the collector or run its configuration
check so `watcher-events.db` and the `events` table exist.

1. Build `Listener_MailReceiver/Build-Windows.bat` with .NET 10 SDK/NuGet access.
   Output is self-contained in `output/windows/`.
2. Open PowerShell as Administrator and create a protected receiver-key file:

```powershell
$dir = "$env:ProgramData\WatcherMailReceiver"
New-Item -ItemType Directory -Path $dir -Force | Out-Null
icacls.exe $dir /inheritance:r /grant:r '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F'
$path = Join-Path $dir 'receiver.key'
if (Test-Path $path) { throw 'Key already exists. Preserve it or rotate it deliberately.' }
$bytes = New-Object byte[] 32
$rng = [Security.Cryptography.RandomNumberGenerator]::Create()
$rng.GetBytes($bytes)
$rng.Dispose()
[IO.File]::WriteAllText($path, [Convert]::ToBase64String($bytes))
Set-Clipboard -Value ([IO.File]::ReadAllText($path))
```

The key is copied to your local clipboard for pasting into the add-on's receiver
key field. Do not put it in Git, chat, URLs or startup arguments. Protect the
folder/key file and avoid copying private keys or mail into support logs.

3. From `Listener_MailReceiver`, start the service with your actual domain list:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Start-Windows.ps1 -InternalDomains 'example.com,subsidiary.example.org'
```

Defaults: database `%ProgramData%\WatcherCollector\watcher-events.db`, key file
`%ProgramData%\WatcherMailReceiver\receiver.key`, local HTTP port **6516**.
This opens a foreground process; keep the window open or configure a startup task.
For another database path, pass `-DatabasePath 'D:\Watcher\watcher-events.db'`.
The launcher uses the published EXE when available, otherwise the source project.

4. For Thunderbird on other PCs, publish the receiver through a trusted HTTPS
reverse proxy/certificate. The add-on base URL should reach this service, with
`/api/mail-alerts` forwarded unchanged. Preserve Authorization and Content-Type
headers. The application checks its own bearer key; an extra proxy login that
expects browser cookies/Windows integrated auth will prevent this add-on's
credential-free HTTP client from reaching it.

The default loopback bind can sit behind IIS on the same PC. An explicit bind
such as `-ListenUrl 'http://0.0.0.0:6516'` exposes HTTP directly and needs your
firewall/network controls. Remote HTTP requires an explicit opt-in in the add-on;
HTTPS is recommended for full email contents and the receiver key. Do not disable
certificate verification to make a client connect.

## Mail dashboard key

Mail endpoints are disabled unless the GUI has a mail-viewing key. This avoids
exposing full email content to everyone who can already reach the TCP dashboard.
Create a **separate random key** in a protected file, using the same generation
steps above with filename `mail-view.key`. Keep the receiver key unchanged.
Rebuild the GUI, then from `Listener_DB_GUI` start it with:

```powershell
.\Start-Windows.ps1 -MailViewKeyFile "$env:ProgramData\WatcherMailReceiver\mail-view.key"
```

Copy that viewing key to authorized viewers via your approved secure channel.
Open `/mail.html`, enter the key and click **Load / search mail**. The key is held
in that page's password field, not placed in the URL or browser local storage.
Use trusted HTTPS for remote dashboard access. The viewing key cannot submit
mail when the receiver has a different key. It grants access to all mail alerts
in this database; per-user/per-mailbox viewing permissions are not implemented.
Restart the receiver/GUI after rotating its key file, and update affected clients.

## Configure startup

Use Windows Task Scheduler to run `Start-Windows.ps1` with the domain/database/key
file arguments on startup, under an account allowed to read the key and update
the database. Use Run whether user is logged on or not, disable time limits and
configure restart after failure. Protect application/configuration paths from
untrusted writers. All credentials remain in protected local files rather than
task arguments. The receiver and GUI can use separate startup tasks.

## Configuration / Linux

Direct service configuration:

| Setting | Meaning |
| --- | --- |
| `DatabasePath` | Existing local collector SQLite file. |
| `InternalDomains` | Comma-separated DNS email domains (required). |
| `IncludeSubdomains` | `true` by default. |
| `MaximumRequestBytes` | Default 16777216, configurable from 1024 to 67108864. |
| `--urls` | Default `http://127.0.0.1:6516`. |
| `LISTENER_MAIL_TOKEN` | Receiver key supplied securely via environment. |
| `LISTENER_MAIL_TOKEN_FILE` | Protected UTF-8 receiver-key file, if direct token is unset. |

For the GUI, corresponding read-key variables are `LISTENER_MAIL_VIEW_TOKEN`
and `LISTENER_MAIL_VIEW_TOKEN_FILE`. Source and published processes support the
same configuration. On a .NET-10-supported Linux host:

```bash
LISTENER_MAIL_TOKEN_FILE=/etc/listener-mail/receiver.key dotnet run --project Listener_MailReceiver -c Release -- --DatabasePath /path/to/watcher-events.db --InternalDomains example.com
```

Use a local writable collector database, not a live SQLite DB over SMB. The
receiver uses WAL mode, short write transactions, a 15-second SQLite timeout,
parameterized SQL and deduplication by client/account/message key. Back up the
SQLite DB and WAL/SHM consistently; email text increases its size. There is no
automatic retention/deletion in this version.

## Stored columns and limitations

`email_alerts` stores sender/address/domain, To/Cc/visible Bcc, receiving-account
addresses, delivery headers, subject, decoded body text, MIME text parts,
attachment names, IDs, client/account labels, message date, body status and
Listener receipt time. Arrays are JSON within their individual columns; the
whole message is not stored as one opaque JSON blob.

Internal classification uses the reported From header, not sender-authentication
results. Ambiguous/unparseable senders are stored with an empty parsed domain.
All data is from the authenticated reporting client; possessing a receiver key
does not prove the original sender identity. Attachment bytes are not uploaded.
The server doesn't send SMTP replies or modify the user's mailbox.

`GET /health` reports service readiness without a key. Mail ingestion requires
`Authorization: Bearer <receiver key>`. Invalid/oversized requests are rejected
and must remain queued at the add-on. Receiver acknowledgement follows SQLite
commit. CORS allows MailExtension origins, not ordinary web origins, and email
content is never included in application log messages.

## Validation

```bash
dotnet build Listener_MailReceiver -c Release
dotnet build Listener_DB_GUI -c Release
node tests/test_thunderbird_core.js
python tests/test_mail.py
python tests/test_dashboard.py
```

Tests use synthetic messages/keys with real HTTP/SQLite and mock Thunderbird
APIs in Chromium. Real Thunderbird installation, IMAP/POP new-mail reception,
encrypted-message behavior, domain HTTPS configuration and startup tasks remain
deployment checks. No real email or live recipients are used by development tests.
