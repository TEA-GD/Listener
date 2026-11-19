# GUI performance update

Rebuild and update the **Collector and GUI**. Agents and mail add-ons do not need updates for these optimizations.

1. Back up the SQLite database consistently and stop the old Collector.
2. Build the updated Collector installer and GUI (`Build-Windows-All.bat`, or their individual build scripts).
3. Update/start the Collector first. Its first start builds derived tables and additional indexes transactionally. Large databases can take longer to initialize; wait for its listening/start message before restarting agents unnecessarily.
4. Restart the GUI and reload the browser page to load the updated scripts.

The GUI still opens the source database read-only. Older databases without the derived schema remain supported through the original read queries, but do not receive the full speed improvement.

## What changed

- The device list reads `gui_devices`, maintained as each event commits, instead of grouping the complete `events` view on every request. Counts, latest timestamps, and distinct process/destination totals retain their existing meaning.
- `gui_dimensions` tracks distinct process names and destination IPs across all categories. Duplicate delivery does not inflate counts. These are derived connection metadata, not mail fields.
- `gui_heartbeats` keeps distinct numeric heartbeat timestamps indexed by device. Timeline queries no longer join and parse raw connection records to obtain heartbeats.
- The GUI caches calculated sessions with a size limit of 200,000 sessions/entries and 15-minute absolute expiry. New/delayed heartbeat stamps change the device revision and invalidate its cached history. A future heartbeat becoming eligible also changes the cache key. Online/offline status and elapsed durations are evaluated against the current time on every request, including cache hits. The timeout setting and time-zone/DST rules remain supported.
- Each event partition has additional indexes for per-device history order and event-time ordering.
- Initial history uses the selected device's **latest recorded UTC day**. Clear date fields or click **Reset** for all retained history. Explicit date filters and API requests retain their existing semantics.
- The new/returning connection report runs when **Check connections** is clicked. Selecting a device no longer starts it automatically.
- The live overview stops refreshing while the Last minute tab or browser page is hidden. Returning to Overview refreshes it; the frozen Last minute capture stays frozen. A slow overview request is allowed to finish instead of being restarted by the timer.

Existing category tables, event IDs/global rowids, Analyzer checkpoints, CSV format, and mail storage are preserved. Derived tables update inside the same transaction as the source event and acknowledgment.

These optimizations add database space and a small amount of work to each Collector event transaction. Full-history searches/grouping and the first timeline calculation after new heartbeat data can still be expensive on large deployments.

## Synthetic measurement

On the development Linux environment with 200,000 synthetic events and 100 devices, seven warmed executions of the device-list SQL had these medians:

| Query | Median |
| --- | ---: |
| Aggregate the unified raw event view | 378.690 ms |
| Read maintained device summaries | 0.080 ms |

Results were checked for exact equality. Initial migration, indexes, summary backfill and Collector startup took 4.62 seconds for that fixture. These numbers measure a database query on synthetic data, not browser page load or production performance. Storage, device/event counts and concurrent users affect results.

To reproduce after building `tests/ServerAgentCollector`:

```bash
python tests/benchmark_gui.py --rows 200000
```

The script creates a temporary database and runs the real Collector migration. It never uses production data. Set `DOTNET_HOST_PATH` if `dotnet` is not on PATH.

## Maintenance

Treat the `gui_*` tables as Collector-owned derived data. Direct inserts/deletes into physical event partitions bypass maintenance and are unsupported. If a deliberate offline database repair changes historical rows, stop the Collector and GUI, make a backup, run `DELETE FROM gui_schema WHERE version=1`, then start the updated Collector to rebuild summaries; restart the GUI to clear cached sessions. Normal upgrades and event delivery do not require this step.
