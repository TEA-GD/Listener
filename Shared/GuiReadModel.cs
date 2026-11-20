using Microsoft.Data.Sqlite;

namespace WatcherCollector;

// Derived, rebuildable reader tables. Always updated inside the event transaction.
internal static class GuiReadModel
{
    internal static void Initialize(SqliteConnection db, SqliteTransaction transaction)
    {
        using var c=db.CreateCommand();c.Transaction=transaction;
        c.CommandText="""
            CREATE TABLE IF NOT EXISTS gui_schema(version INTEGER PRIMARY KEY);
            CREATE TABLE IF NOT EXISTS gui_devices(
                host TEXT PRIMARY KEY,events INTEGER NOT NULL,lastEvent TEXT NOT NULL,lastReceived TEXT NOT NULL,
                processes INTEGER NOT NULL DEFAULT 0,destinations INTEGER NOT NULL DEFAULT 0,heartbeat_revision INTEGER NOT NULL DEFAULT 0);
            CREATE TABLE IF NOT EXISTS gui_dimensions(host TEXT NOT NULL,kind TEXT NOT NULL,value TEXT NOT NULL,PRIMARY KEY(host,kind,value)) WITHOUT ROWID;
            CREATE TABLE IF NOT EXISTS gui_heartbeats(host TEXT NOT NULL,stamp_ms INTEGER NOT NULL,PRIMARY KEY(host,stamp_ms)) WITHOUT ROWID;
            SELECT COUNT(*) FROM gui_schema WHERE version=1;
            """;
        // ExecuteScalar returns the SELECT result after the schema statements.
        bool initialized=Convert.ToInt64(c.ExecuteScalar())>0;
        if(!initialized){
            c.CommandText="""
                DELETE FROM gui_devices; DELETE FROM gui_dimensions; DELETE FROM gui_heartbeats;
                INSERT INTO gui_devices(host,events,lastEvent,lastReceived,processes,destinations)
                    SELECT MONITORED_HOST,COUNT(*),MAX(TIMESTAMP_UTC),MAX(received_utc),
                        COUNT(DISTINCT NULLIF(PROCESS_NAME,'')),COUNT(DISTINCT NULLIF(RECIEVER_IP,''))
                    FROM events GROUP BY MONITORED_HOST;
                INSERT INTO gui_dimensions SELECT DISTINCT MONITORED_HOST,'process',PROCESS_NAME FROM events WHERE PROCESS_NAME<>'';
                INSERT INTO gui_dimensions SELECT DISTINCT MONITORED_HOST,'destination',RECIEVER_IP FROM events WHERE RECIEVER_IP<>'';
                INSERT INTO gui_heartbeats
                    SELECT DISTINCT host,CAST(ROUND((julianday(TIMESTAMP_UTC)-2440587.5)*86400000) AS INTEGER)
                    FROM events WHERE EVENT_TYPE='HEARTBEAT' AND julianday(TIMESTAMP_UTC) IS NOT NULL;
                UPDATE gui_devices SET heartbeat_revision=(SELECT COUNT(*) FROM gui_heartbeats h WHERE h.host=gui_devices.host);
                INSERT INTO gui_schema VALUES(1);
                """;
            c.ExecuteNonQuery();
        }
        // Version 2 retains receipt times without rewriting the event partitions or the v1 index.
        c.CommandText="CREATE TABLE IF NOT EXISTS gui_heartbeat_delivery(host TEXT NOT NULL,stamp_ms INTEGER NOT NULL,received_ms INTEGER,PRIMARY KEY(host,stamp_ms)) WITHOUT ROWID; SELECT COUNT(*) FROM gui_schema WHERE version=2";
        bool deliveryInitialized=Convert.ToInt64(c.ExecuteScalar())>0;
        if(!initialized||!deliveryInitialized){
            c.CommandText="""
                DELETE FROM gui_schema WHERE version=2;
                DELETE FROM gui_heartbeat_delivery;
                INSERT INTO gui_heartbeat_delivery
                    SELECT host,CAST(ROUND((julianday(TIMESTAMP_UTC)-2440587.5)*86400000) AS INTEGER),
                        MIN(CAST(ROUND((julianday(received_utc)-2440587.5)*86400000) AS INTEGER))
                    FROM events WHERE EVENT_TYPE='HEARTBEAT' AND julianday(TIMESTAMP_UTC) IS NOT NULL
                    GROUP BY host,CAST(ROUND((julianday(TIMESTAMP_UTC)-2440587.5)*86400000) AS INTEGER);
                INSERT INTO gui_schema VALUES(2);
                """;
            c.ExecuteNonQuery();
        }
    }

    internal static void Store(SqliteConnection db,SqliteTransaction transaction,string host,string stamp,string received,string process,string destination,bool heartbeat)
    {
        using var c=db.CreateCommand();c.Transaction=transaction;
        c.CommandText="""
            INSERT INTO gui_devices(host,events,lastEvent,lastReceived) VALUES($host,1,$stamp,$received)
                ON CONFLICT(host) DO UPDATE SET events=events+1,lastEvent=MAX(lastEvent,excluded.lastEvent),lastReceived=MAX(lastReceived,excluded.lastReceived);
            INSERT OR IGNORE INTO gui_dimensions SELECT $host,'process',$process WHERE $process<>'';
            UPDATE gui_devices SET processes=processes+changes() WHERE host=$host;
            INSERT OR IGNORE INTO gui_dimensions SELECT $host,'destination',$destination WHERE $destination<>'';
            UPDATE gui_devices SET destinations=destinations+changes() WHERE host=$host;
            INSERT OR IGNORE INTO gui_heartbeats SELECT $host,CAST(ROUND((julianday($stamp)-2440587.5)*86400000) AS INTEGER) WHERE $heartbeat=1;
            INSERT INTO gui_heartbeat_delivery
                SELECT $host,CAST(ROUND((julianday($stamp)-2440587.5)*86400000) AS INTEGER),CAST(ROUND((julianday($received)-2440587.5)*86400000) AS INTEGER) WHERE $heartbeat=1
                ON CONFLICT(host,stamp_ms) DO UPDATE SET received_ms=excluded.received_ms
                WHERE gui_heartbeat_delivery.received_ms IS NULL OR excluded.received_ms<gui_heartbeat_delivery.received_ms;
            UPDATE gui_devices SET heartbeat_revision=heartbeat_revision+changes() WHERE host=$host;
            """;
        c.Parameters.AddWithValue("$host",host);c.Parameters.AddWithValue("$stamp",stamp);c.Parameters.AddWithValue("$received",received);
        c.Parameters.AddWithValue("$process",process);c.Parameters.AddWithValue("$destination",destination);c.Parameters.AddWithValue("$heartbeat",heartbeat?1:0);
        c.ExecuteNonQuery();
    }
}
