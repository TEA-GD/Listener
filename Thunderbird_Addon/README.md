# Thunderbird → Listener external-mail alerts

MailExtension for **Thunderbird 140 or newer**, plus a small authenticated HTTP
receiver in [Listener_MailReceiver](../Listener_MailReceiver/README.md). It reports
**newly received mail from outside configured domains** to Listener. Collection
is disabled until a user configures and enables the selected mail accounts.

The packaged add-on is [packages/listener-external-mail-1.0.0.xpi](packages/listener-external-mail-1.0.0.xpi).
It contains the source files in `extension/`, not credentials, mail or local
settings. It is an unsigned internal/development package, not an add-on published
to addons.thunderbird.net. Follow your organization's extension policy.

## What each alert contains

- Reported From header, parsed sender address/domain (classified by the receiver).
- To/Cc, visible Bcc if supplied by Thunderbird, receiving account addresses and
  Delivered-To/X-Original-To headers when available.
- Subject, Message-ID, message date, client label, account identifier, received time.
- Complete available decoded inline text MIME parts. The display body prefers
  plain text in a multipart/alternative, or converts HTML to text. Original
  inline HTML/text representations remain in `text_parts_json` for inspection.
- Attachment **names**. File contents and attached-message bodies are not
  forwarded. Inline named attachments may also appear in the attachment list.

Receiver fields live in the separate `email_alerts` table of `watcher-events.db`.
TCP events and connection profiles are unaffected. These are mail alerts, not
TCP events sent to port 6515. Use **Mail alerts** in the updated dashboard.

The From header is reported mail data, not proof of SMTP origin or DKIM/SPF
validation. A forged internal From can appear internal. Multiple/malformed From
values are conservatively treated as unparseable/external. Internal-domain
checks match exact domain boundaries, optionally including subdomains; a name
such as `example.com.evil.test` never matches `example.com`.

## 1. Prepare Listener

Follow [receiver setup](../Listener_MailReceiver/README.md) to:

1. Start the collector once so its database exists.
2. Build/start the Mail Receiver, configure your actual internal domains and
   receiver key, and expose it via trusted HTTPS for domain-network access.
3. Rebuild/restart Listener_DB_GUI with a separate mail-viewing key.

The receiver defaults to `http://127.0.0.1:6516`; that URL works only when
Thunderbird and the receiver run on the **same PC**. Thunderbird on another PC
needs the collector PC's HTTPS endpoint, e.g. `https://listener.example.local`.
Neither the TCP ingestion port 6515 nor the dashboard port 5080 is this endpoint.

## 2. Install the add-on

In Thunderbird:

1. Open **Add-ons and Themes**.
2. Open the gear menu and choose **Install Add-on From File**.
3. Select `listener-external-mail-1.0.0.xpi` and approve the displayed permissions.
4. Open the add-on's preferences/options, or click its toolbar button.

No user passwords are needed. The add-on requests `accountsRead`, `messagesRead`,
local storage, unlimited queue storage and alarms. When enabling the receiver,
it asks for access to that configured HTTP(S) host (host permissions cover its ports). It does not have
blanket host access granted by default.

If a policy prevents unsigned internal add-ons, use your organization's approved
Thunderbird deployment/review process. Do not disable signing/trust checks on
production clients as a shortcut.

## 3. Configure it

- **Internal domains:** e.g. `example.com, subsidiary.example.org`, without `@`.
  Keep this list aligned with the receiver's InternalDomains configuration.
- **Subdomains:** decide whether `user@dept.example.com` is internal too.
- **Accounts:** select the work mail accounts to monitor; none are selected by default.
- **Client label:** a useful PC/user label, e.g. `PC-01 / Alice`.
- **Receiver URL:** the Mail Receiver base URL.
- **Receiver key:** paste the receiver's secret key. This is not a mail password
  and is separate from the dashboard's read-only mail-viewing key.
- Tick **Enable collection and forwarding** and click **Save settings and grant receiver access**.

HTTPS is required for a remote receiver unless the user explicitly enables the
unencrypted-HTTP exception for a trusted network. HTTP carries both mail and key
in cleartext. Loopback HTTP is accepted for local testing. Redirects are refused
so credentials/message bodies are not forwarded to a different location.

The receiving service also checks internal domains independently. The add-on
filters internal senders before requesting full bodies when the header is
parseable; the receiver skips internal mail if local settings were inconsistent.
Sent/draft/template/outbox folders and unselected accounts are ignored. Incoming
junk mail is included when delivered through the new-mail event. There is no
automatic scan/backfill of historical messages on installation.

## 4. Test and view alerts

1. In settings click **Send synthetic test**. This queues an invented message;
   it does not read a real email body.
2. Refresh **Delivery status**. Expect pending count to fall to zero and a last
   delivery timestamp. If there is an error, fix receiver/DNS/key/HTTPS settings,
   then click **Retry pending alerts**.
3. Open the dashboard's **Mail alerts** page (`/mail.html`) and enter the separate
   mail-viewing key. The test alert shows subject `Listener synthetic mail test`.
4. For actual mail delivery, receive a new external message in a selected account.
   Check sender, recipients, text and attachment names in **Read message**.
5. Receive an internal-domain message: it should not create an external-mail alert.

The receiver acknowledges only after committing the email alert to SQLite.
Duplicate deliveries use the same client/account/message key and do not create
another row. The dashboard renders text and HTML source as plain text; it does
not execute email HTML or load remote email images.

## Queue, availability and limits

The add-on first persists received-message metadata to IndexedDB, captures the
body, then persists the complete payload before posting. It retries failed
requests with backoff (one minute up to an hour) while Thunderbird is open, and
can resume stored jobs after a restart. Sent/ignored tombstones retain dedupe IDs
without retaining their message bodies in the add-on queue.

Disabling collection pauses forwarding; an already in-flight request may have
reached the server before cancellation. Removing an account selection pauses
its queued work. Removing the add-on or its stored data deletes its queue. Local
profile access and free disk space matter; protect that profile like your mail.
There is no automatic queue/body retention limit or server-side pruning.

If the message moves before capture, the add-on attempts recovery by Message-ID
within the same account. A message purged before capture may be unrecoverable;
the job stays pending with an error rather than silently claiming delivery.
Encrypted/headers-only messages cannot always provide readable text or attachment
names. `body_status` records this (`headers_only`, `decryption_failed`,
`decryption_skipped`, `empty`, or `available`). It cannot reveal hidden envelope
recipients/Bcc data that Thunderbird does not expose.

The receiver defaults to a **16 MiB JSON request** limit. Body text and preserved
MIME parts can duplicate content, so the HTTP payload is larger than the displayed
body. Larger payloads stay queued with HTTP 413; raise MaximumRequestBytes on the
receiver (up to 64 MiB) and any reverse-proxy limit if needed. Nothing is silently
truncated. Large queues can use substantial disk and memory.

## Build and tests

```bash
python Thunderbird_Addon/build.py
node tests/test_thunderbird_core.js
python tests/test_mail.py
```

The packaging script creates a deterministic XPI from `extension/` and places it
in `packages/`. Python/Node are development tools, not client requirements.
Integration tests build the receiver/dashboard first and use synthetic mail,
mock Thunderbird APIs, IndexedDB and Chromium. They verify opt-in/account/domain
filtering, MIME/attachment separation, receiver authorization, SQLite persistence,
queue restart/retry, safe display and duplicate suppression. They are not a real
Thunderbird mail-server/installation test; validate this package on your actual
Thunderbird version and organizational deployment policy before rolling it out.

API behavior was checked against Mozilla's Thunderbird schemas:
`mail/components/extensions/schemas/messages.json` in `mozilla/releases-comm-central`.
