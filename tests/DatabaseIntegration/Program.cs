using System.Text.Json;
using System.Net;
using System.Net.Sockets;
using System.Text;
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
            TestPartitions(root);
            TestRollback(root);
            TestUdpStorage(root);
            TestListener(root).GetAwaiter().GetResult();
            Console.WriteLine("PASS: fresh storage, migration, idempotency, deduplication, peer IP, CSV and migration rollback, port-only listener and optional CIDR filtering");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, true);
        }
    }

    private static void TestPartitions(string root)
    {
        string path=Path.Combine(root,"partitions.db");
        Database.Initialize(path);
        using var db=new SqliteConnection("Data Source="+path); db.Open();
        using var command=db.CreateCommand();
        long previous=0;
        foreach(string category in new[]{"legacy","linux_servers","windows_servers","user_pcs","project_stands"})
        {
            var row=new Dictionary<string,string> { ["EVENT_ID"]=Guid.NewGuid().ToString(),["MONITORED_HOST"]="test-device",["EVENT_TYPE"]="HEARTBEAT",["TIMESTAMP_UTC"]="2026-11-19T09:00:00Z" };
            if(category!="legacy")row["DEVICE_CATEGORY"]=category;
            using(var envelope=JsonDocument.Parse(JsonSerializer.Serialize(new { @event=row }))) Database.Store(path,envelope.RootElement,"127.0.0.1");
            command.CommandText=$"SELECT count(*) FROM events_{category}";
            Check((long)command.ExecuteScalar()! == 1,"Incorrect partition "+category);
            command.CommandText="SELECT MAX(rowid) FROM events";
            long current=(long)command.ExecuteScalar()!; Check(current>previous,"Nonmonotonic global rowid");previous=current;
            row["DEVICE_CATEGORY"]=category=="user_pcs"?"project_stands":"user_pcs";
            using(var envelope=JsonDocument.Parse(JsonSerializer.Serialize(new { @event=row }))) Database.Store(path,envelope.RootElement,"127.0.0.1");
            command.CommandText=$"SELECT count(*) FROM events_{category}";
            Check((long)command.ExecuteScalar()! == 1,"Replay moved original event");
            row["EVENT_ID"]=Guid.NewGuid().ToString();row["DEVICE_CATEGORY"]="invalid;drop table events";
            bool rejected=false;
            try { using var bad=JsonDocument.Parse(JsonSerializer.Serialize(new { @event=row }));Database.Store(path,bad.RootElement,"127.0.0.1"); } catch(ArgumentException){rejected=true;}
            Check(rejected,"Invalid category accepted");
        }
        Database.Initialize(path);
        command.CommandText="SELECT events FROM gui_devices WHERE host='test-device'";Check((long)command.ExecuteScalar()! == 5,"Summary counted duplicate replay");
        command.CommandText="SELECT count(*) FROM gui_heartbeats WHERE host='test-device'";Check((long)command.ExecuteScalar()! == 1,"Heartbeat timestamps not deduplicated");
        command.CommandText="SELECT heartbeat_revision FROM gui_devices WHERE host='test-device'";Check((long)command.ExecuteScalar()! == 1,"Duplicate heartbeat invalidates cache");
        command.CommandText="SELECT count(*) FROM events";Check((long)command.ExecuteScalar()! == 5,"Cross-category duplicate");
        Database.ExportDirty(path,Path.Combine(root,"partition-csv"));
        Check(File.ReadAllLines(Path.Combine(root,"partition-csv","test-device","2026-11-19.csv")).Length==6,"Partition CSV lost records");
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
                "INSERT INTO events(rowid,event_id,host,date_utc,timestamp_utc,payload_json,received_utc) VALUES(42,$id,'test-host','2026-11-09',$stamp,$json,'original-received');";
            cmd.Parameters.AddWithValue("$id", row["EVENT_ID"]);
            cmd.Parameters.AddWithValue("$stamp", row["TIMESTAMP_UTC"]);
            cmd.Parameters.AddWithValue("$json", JsonSerializer.Serialize(row));
            cmd.ExecuteNonQuery();
            cmd.Parameters.Clear();
        }
        Database.Initialize(path);
        Database.Initialize(path);
        cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name IN ('email_alerts','email_metadata_alerts')";
        Check((long)cmd.ExecuteScalar()! == 2, "Collector did not initialize both mail tables");
        if (legacy) { cmd.CommandText="SELECT rowid FROM events"; Check((long)cmd.ExecuteScalar()! == 42,"Migration changed Analyzer checkpoint rowid"); }
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
        cmd.CommandText="SELECT events,processes,destinations,lastEvent,lastReceived FROM gui_devices WHERE host='test-host'";
        using(var summary=cmd.ExecuteReader()) {
            Check(summary.Read()&&summary.GetInt64(0)==1&&summary.GetInt64(1)==1&&summary.GetInt64(2)==1,"Summary counters differ from raw records");
            Check(summary.GetString(3)==row["TIMESTAMP_UTC"],"Summary latest timestamp differs");
        }
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

    private static async Task<TcpClient> ConnectWhenReady(int port)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (true)
        {
            var client = new TcpClient();
            try
            {
                await client.ConnectAsync(IPAddress.Loopback, port).WaitAsync(TimeSpan.FromSeconds(5));
                return client;
            }
            catch (SocketException) when (DateTime.UtcNow < deadline)
            {
                client.Dispose();
                await Task.Delay(25);
            }
            catch { client.Dispose(); throw; }
        }
    }

    private static async Task TestListener(string root)
    {
        foreach (bool restricted in new[] { false, true })
        {
            // No bind_host or sender addresses are needed for port-only mode.
            var portProbe = new TcpListener(IPAddress.Loopback, 0);
            portProbe.Start();
            int port = ((IPEndPoint)portProbe.LocalEndpoint).Port;
            portProbe.Stop();
            string name = restricted ? "restricted" : "port-only";
            string config = Path.Combine(root, name + ".json");
            File.WriteAllText(config, JsonSerializer.Serialize(new {
                bind_port = port,
                database_path = Path.Combine(root, name + ".db"),
                csv_dir = Path.Combine(root, name + "-csv"),
                log_file = Path.Combine(root, name + ".log"),
                allowed_client_cidrs = restricted ? new[] { "192.0.2.0/24" } : Array.Empty<string>()
            }));
            var cfg = CollectorConfig.Load(config);
            Check(cfg.bind_host == "0.0.0.0", "Default must listen on all IPv4 interfaces");
            Database.Initialize(cfg.database_path);
            using var worker = new CollectorWorker(cfg);
            await worker.StartAsync(CancellationToken.None);
            try
            {
                using var client = await ConnectWhenReady(port);
                using var stream = client.GetStream();
                if (restricted)
                {
                    byte[] buffer = new byte[1];
                    try
                    {
                        int read = await stream.ReadAsync(buffer).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
                        Check(read == 0, "Disallowed sender was not disconnected");
                    }
                    catch (IOException) { /* Connection reset also indicates rejection. */ }
                }
                else
                {
                    string id = Guid.NewGuid().ToString();
                    byte[] message = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { @event = new {
                        EVENT_ID = id, EVENT_TYPE = "HEARTBEAT", MONITORED_HOST = "port-only-test",
                        TIMESTAMP_UTC = "2026-11-09T09:00:00Z"
                    } }) + "\n");
                    await stream.WriteAsync(message);
                    using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
                    Check(await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)) == "ACK " + id,
                        "Port-only listener did not ACK heartbeat");
                    using var db = new SqliteConnection("Data Source=" + cfg.database_path);
                    db.Open();
                    using var cmd = db.CreateCommand();
                    cmd.CommandText = "SELECT COLLECTOR_PEER_IP FROM events WHERE EVENT_ID=$id";
                    cmd.Parameters.AddWithValue("$id", id);
                    Check((string?)cmd.ExecuteScalar() == "127.0.0.1", "Received event missing from database");
                }
            }
            finally
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await worker.StopAsync(timeout.Token);
            }
        }
    }

    private static void TestUdpStorage(string root)
    {
        string path=Path.Combine(root,"udp-storage.db");Database.Initialize(path);
        var row=Database.CsvFields.ToDictionary(f=>f,_=>"");
        row["EVENT_ID"]=Guid.NewGuid().ToString();row["EVENT_TYPE"]="UDP_ENDPOINT_AT_START";
        row["TIMESTAMP_UTC"]="2026-11-17T09:00:00Z";row["MONITORED_HOST"]="udp-host";
        row["PROTOCOL"]="UDP";row["SOURCE_IP"]="0.0.0.0";row["SOURCE_PORT"]="5353";row["STATE"]="BOUND";
        using(var envelope=JsonDocument.Parse(JsonSerializer.Serialize(new{@event=row})))
        {
            Database.Store(path,envelope.RootElement,"127.0.0.1");Database.Store(path,envelope.RootElement,"127.0.0.1");
        }
        using var db=new SqliteConnection("Data Source="+path);db.Open();using var cmd=db.CreateCommand();
        cmd.CommandText="SELECT PROTOCOL,SOURCE_PORT,RECIEVER_IP,PORT_USED FROM events";
        using(var reader=cmd.ExecuteReader()){Check(reader.Read(),"UDP event missing");Check(reader.GetString(0)=="UDP"&&reader.GetString(1)=="5353"&&reader.GetString(2)==""&&reader.GetString(3)=="","UDP columns changed unknown destination");}
        cmd.CommandText="SELECT COUNT(*) FROM events";Check((long)cmd.ExecuteScalar()! == 1,"UDP deduplication failed");
        row["EVENT_ID"]=Guid.NewGuid().ToString();row["PROTOCOL"]="TCP";
        bool rejected=false;
        using(var envelope=JsonDocument.Parse(JsonSerializer.Serialize(new{@event=row})))
            try{Database.Store(path,envelope.RootElement,"127.0.0.1");}catch(ArgumentException){rejected=true;}
        Check(rejected,"UDP event with TCP protocol accepted");
        Database.ExportDirty(path,Path.Combine(root,"udp-csv"));
        Check(File.ReadAllText(Path.Combine(root,"udp-csv","udp-host","2026-11-17.csv")).Contains("UDP_ENDPOINT_AT_START"),"UDP CSV export missing");
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
