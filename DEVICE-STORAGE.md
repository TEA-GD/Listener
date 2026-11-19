# Device categories and database storage

Update the Collector before deploying categorized agents. Stop it and make a consistent SQLite backup before upgrading. On startup it migrates the shared database transactionally. Older Collectors accept the new field but do not partition records, so upgrade order matters.

| Installation choice | Configuration value | Physical connection table |
| --- | --- | --- |
| Linux servers | `linux_servers` | `events_linux_servers` |
| Windows servers | `windows_servers` | `events_windows_servers` |
| User PCs | `user_pcs` | `events_user_pcs` |
| Project Stands | `project_stands` | `events_project_stands` |
| Existing unclassified data / old agents | missing field or `legacy` | `events_legacy` |
| Mail alerts | selected mail receiver | `email_alerts` / `email_metadata_alerts` |

The Windows installer asks for **Device type**. The PowerShell registration helper prompts or accepts `-DeviceCategory`; the Linux package prompts or accepts `WATCHER_DEVICE_CATEGORY` for unattended installation. Saved selections survive updates. The agent configuration field is `device_category`. Restart an agent after editing its configuration.

Device role is selected explicitly, rather than inferred from its operating system. Mail receivers route mail independently of agent device type. The metadata receiver continues storing only date, sender, recipients, attachment flag and filenames, with delivery identifiers; categorizing connection events does not enable message-body collection.

Existing rows are preserved in `events_legacy`; installing a categorized agent routes future records to the selected table. Historical records and already spooled events are not reassigned. Old agents keep working without configuration changes.

The former `events` table becomes a read-only UNION view over all five connection tables. Existing GUI pages, online timelines, CSV exports and Analyzer queries continue reading all categories together. `DEVICE_CATEGORY` is available in that view for SQL filtering. The daily CSV column format remains compatible.

`event_registry` holds one global event ID and sequence per connection event. Migration preserves existing rowids (including gaps), protecting Analyzer checkpoints and frozen GUI snapshot watermarks. Global deduplication prevents a replay with a different category from duplicating or moving a record. Payload and registry writes commit together before acknowledgment. Unknown category values are rejected; category values cannot supply SQL table names.

Do not insert directly into the partition tables or modify `event_registry`; deliver events through the Collector. External tools that wrote directly into the former `events` table must be changed: it is now a read-only compatibility view. Readers can keep using `events` or query a specific physical table.
