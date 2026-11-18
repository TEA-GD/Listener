# Listener Thunderbird metadata-only variant

This is a separate variant of `Thunderbird_Addon`, for Thunderbird **140+**. It reports only newly received external mail from explicitly selected accounts after collection is enabled.

## Collected mail fields

- **Date**: the message's date, normalized to UTC.
- **Sender**: the visible From value.
- **Receiver**: deduplicated To, Cc and visible Bcc recipients. Bcc recipients not exposed by Thunderbird cannot be recovered; these are message header recipients, not verified SMTP envelope recipients.
- **Has attachments**: boolean, displayed as yes/no when queried.
- **Attachment filenames**: names only, including named inline attachments reported by Thunderbird. Unnamed attachments use `(unnamed attachment)`.

Subjects, body text, HTML/MIME text and attachment contents are not queued, transmitted or stored. The extension never calls `messages.getFull`, `messages.getRaw` or `messages.getAttachmentFile`. Thunderbird itself still stores the original email in its normal mail profile; this variant does not change that.

Client/account labels and IDs, a message deduplication hash, alert IDs and a receipt timestamp remain as delivery bookkeeping. No raw Message-ID is transmitted. Attachment enumeration failures keep the alert pending rather than incorrectly reporting "no attachments".

## Install

1. Build/start [Listener_MailReceiver_Metadata](../Listener_MailReceiver_Metadata/README.md). It uses **port 6517**, endpoint `/api/mail-metadata`, and a separate database/table.
2. Download [listener-mail-metadata-1.0.0.xpi](packages/listener-mail-metadata-1.0.0.xpi) using the file's **Download raw file** button. Or run `python build.py` in this folder to rebuild the XPI.
3. In Thunderbird, open **Add-ons and Themes → gear → Install Add-on From File** and select the XPI. This is an unsigned internal/development package; Thunderbird's signing and enterprise installation requirements still apply.
4. Open the add-on's options. Select accounts, set internal domains, a client label, receiver URL and the receiver's secret key (at least 32 characters). Enable collection, save and grant receiver access.
5. Send a synthetic test from options, then receive a new external test message.

The default `http://127.0.0.1:6517` is for Thunderbird and receiver on the same PC. For other PCs use the receiver server's HTTPS URL. Sender-domain filtering and incoming-folder behavior match the original add-on. There is no historical inbox scan.

## Switching from the full-text add-on

The metadata variant's add-on ID is **`listener-mail-metadata@tea-gd.local`**; the original ID is different. It uses a fresh `listener-mail-metadata` IndexedDB queue and does **not** import old full-text pending jobs or settings. Configure it explicitly.

**Disable the original `Listener External Mail Alerts` add-on before enabling this variant.** If both are enabled, the original will continue sending full text. Stopping/replacing the old receiver also prevents the old extension from delivering its queue. Existing full-text rows and the old profile queue are not automatically deleted; handle their retention separately.

For AD/GPO deployment use the new add-on ID in Thunderbird's `ExtensionSettings` policy and host this XPI at your organization's HTTPS URL. Force-installing the extension alone does not configure or enable collection. Do not use a policy that leaves the original full-text add-on enabled if metadata-only collection is required.

## Viewing results

Use the SQL query in the metadata receiver README to view/export the five requested fields. The existing Listener dashboard reads the original `email_alerts` table and does **not** display this variant's new metadata table.
