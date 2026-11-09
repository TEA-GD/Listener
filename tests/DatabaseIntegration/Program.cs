using System.Text.Json;
using Microsoft.Data.Sqlite;
using WatcherCollector;

internal static class DatabaseIntegration
{
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private static void Main()
    {
        string root = Path.Combine(Path.GetTempPath(), "watcher-db-tests-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        try
        {
            foreach (bool legacy in new[] { false, true }) Test(root, legacy);
            TestRollback(root);
            Console.WriteLine("PASS: fresh storage, migration, idempotency, deduplication, peer IP, CSV and migration rollback");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, true);
        }
    }

    private static void Test(string root, bool legacy)
    {
        string path = Path.Combine(root, legacy ? "legacy.db" : "fresh.db");
        var row = Database.CsvFields.ToDictionary(f => f, f => "value-" + f);
        row["EVENT_ID"] = Guid.NewGuid().ToString();
        row["MONITORED_HOST"] = "test-host";
        row["EVENT_TYPE"] = "ACTIVE_AT_START";
        row["TIMESTAMP_UTC"] = "2026-11-09T09:02:25.877+00:00";
        row["SOURCE_IP"] = "192.168.88.175";
        row["PROCESS_ID"] = "4884";
        row["RECIEVER_DOMAINNAME"] = "";
        row["PROCESS_NAME"] = "a;\"b.exe";
        row["COLLECTOR_PEER_IP"] = "trusted-peer";
        using var db = new SqliteConnection("Data Source=" + path);
        db.Open();
        using var cmd = db.CreateCommand();
        if (legacy)
        {
            cmd.CommandText = "CREATE TABLE events(event_id TEXT PRIMARY KEY,host TEXT NOT NULL,date_utc TEXT NOT NULL,timestamp_utc TEXT NOT NULL,payload_json TEXT NOT NULL,received_utc TEXT NOT NULL);" +
                "INSERT INTO events VALUES($id,'test-host','2026-11-09',$stamp,$json,'original-received');";
            cmd.Parameters.AddWithValue("$id", row["EVENT_ID"]);
            cmd.Parameters.AddWithValue("$stamp", row["TIMESTAMP_UTC"]);
            cmd.Parameters.AddWithValue("$json", JsonSerializer.Serialize(row));
            cmd.ExecuteNonQuery();
            cmd.Parameters.Clear();
        }
        Database.Initialize(path);
        Database.Initialize(path);
        // Verify all migrated values, or all newly stored values, independently.
        if (!legacy)
        {
            row["COLLECTOR_PEER_IP"] = "spoofed-peer";
            using var envelope = JsonDocument.Parse(JsonSerializer.Serialize(new { @event = row }));
            Check(Database.Store(path, envelope.RootElement, "trusted-peer") == row["EVENT_ID"], "Wrong ACK ID");
            Database.Store(path, envelope.RootElement, "trusted-peer");
            row["COLLECTOR_PEER_IP"] = "trusted-peer";
        }
        cmd.CommandText = "SELECT COUNT(*) FROM events";
        Check((long)cmd.ExecuteScalar()! == 1, "Duplicate event inserted");
        cmd.CommandText = "PRAGMA table_info(events)";
        using (var schema = cmd.ExecuteReader())
            while (schema.Read()) Check(schema.GetString(1) != "payload_json", "JSON blob column remains");
        cmd.CommandText = "SELECT * FROM events";
        using (var reader = cmd.ExecuteReader())
        {
            Check(reader.Read(), "Event missing");
            foreach (string field in Database.CsvFields)
                Check(reader.GetString(reader.GetOrdinal(field)) == row[field], "Incorrect column " + field);
            if (legacy) Check(reader.GetString(reader.GetOrdinal("received_utc")) == "original-received", "Receipt changed");
        }
        cmd.CommandText = "INSERT OR IGNORE INTO dirty VALUES('test-host','2026-11-09',1)";
        cmd.ExecuteNonQuery();
        string csv = Path.Combine(root, legacy ? "legacy-csv" : "fresh-csv");
        Database.ExportDirty(path, csv);
        string content = File.ReadAllText(Path.Combine(csv, "test-host", "2026-11-09.csv"));
        Check(content.Contains("\"192.168.88.175\"") && content.Contains("\"a;\"\"b.exe\""), "CSV values/escaping incorrect");
        cmd.CommandText = "SELECT COUNT(*) FROM dirty";
        Check((long)cmd.ExecuteScalar()! == 0, "Export not marked complete");
    }

    private static void TestRollback(string root)
    {
        string path = Path.Combine(root, "invalid.db");
        using var db = new SqliteConnection("Data Source=" + path);
        db.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "CREATE TABLE events(event_id TEXT PRIMARY KEY,host TEXT,date_utc TEXT,timestamp_utc TEXT,payload_json TEXT,received_utc TEXT); INSERT INTO events VALUES('id','host','day','stamp','invalid-json','receipt')";
        cmd.ExecuteNonQuery();
        bool failed = false;
        try { Database.Initialize(path); } catch (SqliteException) { failed = true; }
        Check(failed, "Invalid legacy JSON must not be silently discarded");
        cmd.CommandText = "SELECT payload_json FROM events";
        Check((string)cmd.ExecuteScalar()! == "invalid-json", "Migration did not roll back");
    }
}
