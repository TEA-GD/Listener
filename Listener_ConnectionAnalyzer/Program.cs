using System.Globalization;
using System.Net;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace ListenerConnectionAnalyzer;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        AnalyzerLog? persistentLog = null;
        try
        {
            persistentLog = AnalyzerLog.Open(args);
            if (args.Contains("--help")) { Console.WriteLine("Listener_ConnectionAnalyzer --config config.json [--once | --check-config] [--log PATH]"); return 0; }
            int at = Array.IndexOf(args, "--config");
            string path = at < 0 ? "config.json" : at + 1 < args.Length ? args[at + 1] : throw new ArgumentException("Missing config path");
            var config = Config.Load(path);
            Directory.CreateDirectory(Path.GetDirectoryName(config.analysis_database)!);
            using var processLock = new FileStream(config.analysis_database + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            using var analyzer = new ConnectionAnalyzer(config);
            if (args.Contains("--check-config")) { Console.WriteLine("CONFIG OK: analyzer state initialized; no history processed."); return 0; }
            using var stop = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
            using var terminate = OperatingSystem.IsLinux() ? PosixSignalRegistration.Create(PosixSignal.SIGTERM, e => { e.Cancel = true; stop.Cancel(); }) : null;
            do
            {
                int count;
                do { count = analyzer.ProcessBatch(); } while (count > 0 && !stop.IsCancellationRequested);
                if (args.Contains("--once")) break;
                await Task.Delay(TimeSpan.FromSeconds(config.poll_seconds), stop.Token);
            } while (!stop.IsCancellationRequested);
            return 0;
        }
        catch (OperationCanceledException) { return 0; }
        catch (Exception ex) { Console.Error.WriteLine("Connection analyzer stopped: " + ex.Message); return 1; }
        finally { persistentLog?.Dispose(); }
    }
}

internal sealed class Config
{
    public string database_path { get; set; } = "";
    public string analysis_database { get; set; } = "";
    public int poll_seconds { get; set; } = 5;
    public int batch_size { get; set; } = 1000;
    internal static Config Load(string file)
    {
        var config = JsonSerializer.Deserialize<Config>(File.ReadAllText(file)) ?? throw new ArgumentException("Invalid config");
        string root = Path.GetDirectoryName(Path.GetFullPath(file))!;
        if (string.IsNullOrWhiteSpace(config.database_path) || string.IsNullOrWhiteSpace(config.analysis_database)) throw new ArgumentException("Both database paths are required");
        config.database_path = Path.GetFullPath(config.database_path, root);
        config.analysis_database = Path.GetFullPath(config.analysis_database, root);
        string Canonical(string p) => File.Exists(p) ? new FileInfo(p).ResolveLinkTarget(true)?.FullName ?? p : p;
        if (string.Equals(Canonical(config.database_path), Canonical(config.analysis_database), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new ArgumentException("The analysis database must be separate from the Collector database");
        // Also reject aliases/hard links to a Collector database before opening
        // the writable state connection. Path comparison alone cannot catch them.
        if (File.Exists(config.analysis_database))
        {
            using var probe = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = config.analysis_database, Mode = SqliteOpenMode.ReadOnly }.ToString());
            probe.Open(); using var check = probe.CreateCommand();
            check.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE name IN ('events','event_registry')";
            if (Convert.ToInt64(check.ExecuteScalar(), CultureInfo.InvariantCulture) > 0)
                throw new ArgumentException("The analysis path points to a Collector database; choose a separate output database");
        }
        if (config.poll_seconds is < 1 or > 3600 || config.batch_size is < 1 or > 10000) throw new ArgumentException("Invalid polling interval or batch size");
        return config;
    }
}

internal sealed class ConnectionAnalyzer : IDisposable
{
    private readonly Config config;
    private readonly SqliteConnection state;
    internal ConnectionAnalyzer(Config config)
    {
        this.config = config;
        // Check the source BEFORE initializing the writable analysis database.
        using var source = OpenSource();
        state = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = config.analysis_database, DefaultTimeout = 15 }.ToString());
        state.Open();
        Execute("""
            PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL;
            CREATE TABLE IF NOT EXISTS analysis_meta(key TEXT PRIMARY KEY,value TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS analysis_connections(
                host TEXT NOT NULL,signature TEXT NOT NULL,identity TEXT NOT NULL,direction TEXT NOT NULL,
                local_ip TEXT NOT NULL,peer_ip TEXT NOT NULL,service_port TEXT NOT NULL,protocol TEXT NOT NULL,
                process_name TEXT NOT NULL,software TEXT NOT NULL,direction_basis TEXT NOT NULL,
                first_seen_ms INTEGER NOT NULL,last_seen_ms INTEGER NOT NULL,observations INTEGER NOT NULL,
                PRIMARY KEY(host,signature));
            CREATE INDEX IF NOT EXISTS idx_analysis_host_direction ON analysis_connections(host,direction,first_seen_ms);
            CREATE INDEX IF NOT EXISTS idx_analysis_peer ON analysis_connections(host,peer_ip,first_seen_ms);
            CREATE INDEX IF NOT EXISTS idx_analysis_identity ON analysis_connections(host,identity,first_seen_ms);
            CREATE INDEX IF NOT EXISTS idx_analysis_direction ON analysis_connections(direction,first_seen_ms);
            """);
        if (Meta("schema_version") is string version && version != "1") throw new InvalidOperationException("Unsupported analysis schema version");
        if (Meta("source_path") is string previous && !string.Equals(previous, config.database_path, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new InvalidOperationException("This analysis database belongs to another source. Use a new analysis database.");
        bool hasCursor = Meta("cursor") is not null;
        using var tx = state.BeginTransaction();
        SetMeta("schema_version", "1", tx); SetMeta("source_path", config.database_path, tx);
        if (!hasCursor) { SetMeta("cursor", "0", tx); SetMeta("anchor", "", tx); }
        tx.Commit();
    }
    private SqliteConnection OpenSource()
    {
        if (!File.Exists(config.database_path)) throw new IOException("Collector database not found");
        var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = config.database_path, Mode = SqliteOpenMode.ReadOnly, DefaultTimeout = 15 }.ToString());
        try
        {
            db.Open(); using var c = db.CreateCommand(); c.CommandText = "PRAGMA query_only=ON; SELECT rowid,EVENT_ID,MONITORED_HOST,TIMESTAMP_UTC FROM events LIMIT 0";
            using var reader = c.ExecuteReader(); return db;
        }
        catch { db.Dispose(); throw; }
    }
    private SqliteCommand Command(string sql, SqliteTransaction? tx, params (string Key, object Value)[] values)
    {
        var c = state.CreateCommand(); c.CommandText = sql; c.Transaction = tx;
        foreach (var (key, value) in values) c.Parameters.AddWithValue(key, value);
        return c;
    }
    private void Execute(string sql, SqliteTransaction? tx = null, params (string, object)[] values) { using var c = Command(sql, tx, values); c.ExecuteNonQuery(); }
    private string? Meta(string key) { using var c = Command("SELECT value FROM analysis_meta WHERE key=$key", null, ("$key", key)); return c.ExecuteScalar() as string; }
    private void SetMeta(string key, string value, SqliteTransaction tx) => Execute("INSERT INTO analysis_meta VALUES($key,$value) ON CONFLICT(key) DO UPDATE SET value=excluded.value", tx, ("$key", key), ("$value", value));
    internal int ProcessBatch()
    {
        long cursor = long.Parse(Meta("cursor")!, CultureInfo.InvariantCulture);
        using var source = OpenSource();
        using var query = source.CreateCommand();
        if (cursor > 0)
        {
            query.CommandText = "SELECT EVENT_ID FROM events WHERE rowid=$cursor"; query.Parameters.AddWithValue("$cursor", cursor);
            if (!string.Equals(query.ExecuteScalar() as string, Meta("anchor"), StringComparison.Ordinal)) throw new InvalidOperationException("Source cursor changed: preserve this state and use a new analysis database to rebuild.");
        }
        query.Parameters.Clear(); query.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE name='event_registry' AND type='table'";
        bool registered = Convert.ToInt64(query.ExecuteScalar(), CultureInfo.InvariantCulture) > 0;
        // MAX over the union view can scan the entire history. The registry's
        // primary-key watermark is cheap; gaps from mail/context events are valid.
        query.CommandText = registered ? "SELECT COALESCE(MAX(sequence),0) FROM event_registry" : "SELECT COALESCE(MAX(rowid),0) FROM events";
        long maximum = Convert.ToInt64(query.ExecuteScalar(), CultureInfo.InvariantCulture);
        query.CommandText = "SELECT rowid AS source_rowid,* FROM events WHERE rowid>$cursor AND rowid<=$maximum ORDER BY rowid LIMIT $limit";
        query.Parameters.AddWithValue("$cursor", cursor); query.Parameters.AddWithValue("$limit", config.batch_size); query.Parameters.AddWithValue("$maximum", maximum);
        var rows = new List<Dictionary<string, string>>();
        using (var reader = query.ExecuteReader()) while (reader.Read())
        {
            var row = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < reader.FieldCount; i++) row[reader.GetName(i)] = reader.IsDBNull(i) ? "" : Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture)!;
            rows.Add(row);
        }
        using var tx = state.BeginTransaction();
        foreach (var row in rows)
        {
            Learn(row, tx);
            SetMeta("cursor", row["source_rowid"], tx); SetMeta("anchor", row["EVENT_ID"], tx);
        }
        SetMeta("scan_complete", (rows.Count < config.batch_size).ToString(), tx);
        SetMeta("source_max_rowid", maximum.ToString(CultureInfo.InvariantCulture), tx);
        SetMeta("last_processed_utc", DateTimeOffset.UtcNow.ToString("o"), tx);
        tx.Commit();
        if (rows.Count > 0) Console.WriteLine($"Analyzed {rows.Count} source rows; cursor={rows[^1]["source_rowid"]}/{maximum}");
        return rows.Count;
    }
    private void Learn(Dictionary<string, string> row, SqliteTransaction tx)
    {
        string Text(string key) => row.GetValueOrDefault(key, "");
        string type = Text("EVENT_TYPE"), protocol = Text("PROTOCOL").ToUpperInvariant();
        // Snapshots and disconnects also prove prior observation. UDP bindings without
        // a known peer cannot establish a communication or its direction.
        if (protocol is not ("TCP" or "UDP") || type is not ("ACTIVE_AT_START" or "TCP_CONNECTED" or "TCP_DISCONNECTED" or "TCP_SNAPSHOT" or "UDP_ENDPOINT_AT_START" or "UDP_ENDPOINT_OPENED" or "UDP_ENDPOINT_CLOSED" or "UDP_SNAPSHOT")) return;
        if (!IPAddress.TryParse(Text("RECIEVER_IP"), out var peer) || peer.Equals(IPAddress.Any) || peer.Equals(IPAddress.IPv6Any) || IPAddress.IsLoopback(peer)) return;
        if (peer.IsIPv4MappedToIPv6) peer = peer.MapToIPv4();
        if (!DateTimeOffset.TryParse(Text("TIMESTAMP_UTC"), CultureInfo.InvariantCulture, DateTimeStyles.None, out var time) || time.Offset != TimeSpan.Zero || Text("MONITORED_HOST").Length == 0) return;
        string direction = Text("CONNECTION_DIRECTION"), basis = Text("DIRECTION_BASIS");
        if (protocol != "TCP" || !((direction == "incoming" && basis == "local_listener") || (direction == "outgoing" && basis == "no_local_listener"))) { direction = "unknown"; basis = "unavailable"; }
        string port = direction == "incoming" ? Text("SOURCE_PORT") : Text("PORT_USED");
        int servicePort = 0;
        if (direction != "unknown" && (!int.TryParse(port, out servicePort) || servicePort is < 1 or > 65535)) return;
        string service = direction == "unknown" ? "" : servicePort.ToString(CultureInfo.InvariantCulture);
        string identity = JsonSerializer.Serialize(new[] { peer.ToString(), protocol, service, Text("PROCESS_NAME"), Text("SOFTWARE_THAT_USED") });
        string signature = JsonSerializer.Serialize(new[] { direction, identity });
        long stamp = time.ToUnixTimeMilliseconds();
        Execute("""
            INSERT INTO analysis_connections VALUES($host,$signature,$identity,$direction,$local,$peer,$port,$protocol,$process,$software,$basis,$time,$time,1)
            ON CONFLICT(host,signature) DO UPDATE SET
                local_ip=CASE WHEN excluded.last_seen_ms>=last_seen_ms THEN excluded.local_ip ELSE local_ip END,
                first_seen_ms=MIN(first_seen_ms,excluded.first_seen_ms),last_seen_ms=MAX(last_seen_ms,excluded.last_seen_ms),observations=observations+1
            """, tx, ("$host", Text("MONITORED_HOST")), ("$signature", signature), ("$identity", identity), ("$direction", direction),
            ("$local", Text("SOURCE_IP")), ("$peer", peer.ToString()), ("$port", service),
            ("$protocol", protocol), ("$process", Text("PROCESS_NAME")), ("$software", Text("SOFTWARE_THAT_USED")), ("$basis", basis), ("$time", stamp));
    }
    public void Dispose() => state.Dispose();
}
