using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace WatcherCollector;

internal sealed class CollectorConfig
{
    public string bind_host { get; set; } = "0.0.0.0";
    public int bind_port { get; set; } = 6515;
    public string database_path { get; set; } = "";
    public string csv_dir { get; set; } = "";
    public string log_file { get; set; } = "";
    public int csv_export_seconds { get; set; } = 10;
    public int max_clients { get; set; } = 64;
    public string[] allowed_client_cidrs { get; set; } = Array.Empty<string>();

    public static CollectorConfig Load(string path)
    {
        var c = JsonSerializer.Deserialize<CollectorConfig>(File.ReadAllText(path, Encoding.UTF8),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? throw new InvalidOperationException("Invalid collector configuration");
        if (c.bind_port is < 1 or > 65535) throw new ArgumentException("Invalid bind_port");
        if (!IPAddress.TryParse(c.bind_host, out _)) throw new ArgumentException("bind_host must be an IP address, such as 0.0.0.0");
        if (c.csv_export_seconds is < 1 or > 3600) throw new ArgumentException("csv_export_seconds must be 1..3600");
        if (c.max_clients is < 1 or > 1024) throw new ArgumentException("max_clients must be 1..1024");
        if (new[] { c.database_path, c.csv_dir, c.log_file }.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("database_path, csv_dir, and log_file are required");
        if (c.allowed_client_cidrs is null || c.allowed_client_cidrs.Length == 0)
            throw new ArgumentException("allowed_client_cidrs must list the actual endpoint networks");
        foreach (string entry in c.allowed_client_cidrs) _ = Cidr.Parse(entry);
        return c;
    }
}

internal sealed class Cidr
{
    private readonly byte[] _network;
    private readonly int _prefix;
    private Cidr(byte[] network, int prefix) { _network = network; _prefix = prefix; }

    public static Cidr Parse(string text)
    {
        var parts = text.Split('/');
        if (parts.Length != 2 || !IPAddress.TryParse(parts[0].Trim(), out IPAddress? ip) ||
            !int.TryParse(parts[1].Trim(), CultureInfo.InvariantCulture, out int prefix))
            throw new ArgumentException("Invalid CIDR: " + text);
        int bits = ip.GetAddressBytes().Length * 8;
        if (prefix <= 0 || prefix > bits) throw new ArgumentException("CIDR must be restricted, not 0/0: " + text);
        return new Cidr(ip.GetAddressBytes(), prefix);
    }

    public bool Contains(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        var address = ip.GetAddressBytes();
        if (address.Length != _network.Length) return false;
        for (int bit = 0; bit < _prefix; bit++)
        {
            int b = bit / 8, mask = 128 >> (bit % 8);
            if ((address[b] & mask) != (_network[b] & mask)) return false;
        }
        return true;
    }
}

internal static class Log
{
    private static readonly object Gate = new();
    public static string PathName { get; set; } = "";
    public static void Write(string message)
    {
        string line = $"{DateTimeOffset.UtcNow:O} {message}";
        lock (Gate)
        {
            try { Console.WriteLine(line); } catch { /* Service may have no console. */ }
            try
            {
                if (!string.IsNullOrEmpty(PathName))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(PathName)!);
                    File.AppendAllText(PathName, line + Environment.NewLine, new UTF8Encoding(false));
                }
            }
            catch { /* No recursive logging. */ }
        }
    }
}

internal static class Program
{
    private static int Main(string[] args)
    {
        try
        {
            if (args.Contains("--help", StringComparer.OrdinalIgnoreCase))
            {
                Console.WriteLine("WatcherCollector.exe --config <path> [--export-once | --check-config]");
                return 0;
            }
            string configPath = GetOption(args, "--config") ??
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "WatcherCollector", "collector.json");
            var cfg = CollectorConfig.Load(configPath);
            Log.PathName = cfg.log_file;
            Directory.CreateDirectory(Path.GetDirectoryName(cfg.database_path)!);
            Directory.CreateDirectory(cfg.csv_dir);
            Database.Initialize(cfg.database_path);
            if (args.Contains("--check-config", StringComparer.OrdinalIgnoreCase))
            {
                Log.Write("CONFIG OK: TCP listener settings, SQLite and CSV paths checked.");
                return 0;
            }
            if (args.Contains("--export-once", StringComparer.OrdinalIgnoreCase))
            {
                Database.ExportDirty(cfg.database_path, cfg.csv_dir);
                Log.Write("CSV export completed.");
                return 0;
            }
            var builder = Host.CreateApplicationBuilder(args);
            builder.Logging.ClearProviders();
            builder.Services.AddWindowsService(options => options.ServiceName = "WatcherCollector");
            builder.Services.AddSingleton(cfg);
            builder.Services.AddHostedService<CollectorWorker>();
            using var app = builder.Build();
            app.Run();
            return 0;
        }
        catch (Exception ex)
        {
            Log.Write("FATAL: " + ex);
            return 1;
        }
    }

    private static string? GetOption(string[] args, string name)
    {
        for (int i = 0; i + 1 < args.Length; i++)
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
        return null;
    }
}

internal sealed class CollectorWorker(CollectorConfig cfg) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            var networks = cfg.allowed_client_cidrs.Select(Cidr.Parse).ToArray();
            using var clientSlots = new SemaphoreSlim(cfg.max_clients, cfg.max_clients);
            var listener = new TcpListener(IPAddress.Parse(cfg.bind_host), cfg.bind_port);
            listener.Start(128);
            Log.Write($"STARTED PLAIN TCP {cfg.bind_host}:{cfg.bind_port}; no TLS; no tokens; permitted CIDRs: {string.Join(",", cfg.allowed_client_cidrs)}");
            Log.Write("WARNING: INSECURE PLAIN TCP. No encryption, no authentication. Restrict firewall and use isolated management networks.");
            Task exportTask = ExportLoop(stoppingToken);
            var active = new ConcurrentDictionary<long, Task>();
            long clientNumber = 0;
            try
            {
                while (!stoppingToken.IsCancellationRequested)
                {
                    TcpClient client;
                    try { client = await listener.AcceptTcpClientAsync(stoppingToken); }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                    var peer = (IPEndPoint)client.Client.RemoteEndPoint!;
                    if (!networks.Any(n => n.Contains(peer.Address)))
                    {
                        Log.Write($"BLOCKED {peer.Address} (CIDR)");
                        client.Dispose();
                        continue;
                    }
                    if (!clientSlots.Wait(0))
                    {
                        Log.Write($"BUSY {peer.Address} (max clients)");
                        client.Dispose();
                        continue;
                    }
                    long id = Interlocked.Increment(ref clientNumber);
                    Task task = Task.Run(() => HandleClient(client, peer.Address.ToString(), cfg.database_path, clientSlots));
                    active[id] = task;
                    _ = task.ContinueWith(t => { active.TryRemove(id, out var removed); }, TaskScheduler.Default);
                }
            }
            finally
            {
                listener.Stop();
                Log.Write("STOPPING; exporting pending CSV files");
                try { await exportTask; } catch (OperationCanceledException) { }
                try { await Task.WhenAll(active.Values).WaitAsync(TimeSpan.FromSeconds(25)); }
                catch (TimeoutException) { Log.Write("Some client connections did not close before shutdown."); }
            }
        }
        catch (Exception ex)
        {
            Log.Write("SERVER FAILURE: " + ex);
            throw;
        }
    }

    private async Task ExportLoop(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                try { Database.ExportDirty(cfg.database_path, cfg.csv_dir); }
                catch (Exception ex) { Log.Write("CSV EXPORT ERROR (records kept in SQLite): " + ex.Message); }
                await Task.Delay(TimeSpan.FromSeconds(cfg.csv_export_seconds), token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        try { Database.ExportDirty(cfg.database_path, cfg.csv_dir); }
        catch (Exception ex) { Log.Write("Final CSV export failed: " + ex.Message); }
    }

    private static void HandleClient(TcpClient client, string peerIp,
                                     string database, SemaphoreSlim slots)
    {
        try
        {
            using (client)
            {
                client.ReceiveTimeout = 20000;
                client.SendTimeout = 20000;
                using var stream = client.GetStream();
                stream.ReadTimeout = 20000;
                stream.WriteTimeout = 20000;
                using var buffered = new BufferedStream(stream, 65536);
                while (true)
                {
                    byte[]? message;
                    try { message = ReadLineBounded(buffered); }
                    catch (InvalidDataException)
                    {
                        buffered.Write(Encoding.ASCII.GetBytes("ERR invalid message length\n"));
                        buffered.Flush();
                        break;
                    }
                    if (message is null) break;
                    try
                    {
                        using var doc = JsonDocument.Parse(message);
                        string id = Database.Store(database, doc.RootElement, peerIp);
                        byte[] ack = Encoding.ASCII.GetBytes("ACK " + id + "\n");
                        buffered.Write(ack);
                        buffered.Flush();
                    }
                    catch (Exception ex) when (ex is JsonException or ArgumentException or FormatException or InvalidOperationException)
                    {
                        Log.Write($"REJECTED {peerIp}: {ex.Message}");
                        buffered.Write(Encoding.ASCII.GetBytes("ERR rejected event\n"));
                        buffered.Flush();
                        break;
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
        {
            Log.Write($"Client {peerIp} disconnected: {ex.Message}");
        }
        catch (Exception ex) { Log.Write($"Client {peerIp} error: {ex.Message}"); }
        finally { slots.Release(); }
    }

    private static byte[]? ReadLineBounded(BufferedStream reader)
    {
        using var buffer = new MemoryStream();
        while (true)
        {
            int b = reader.ReadByte();
            if (b == -1) return buffer.Length == 0 ? null : throw new InvalidDataException("Incomplete JSON line");
            if (b == '\n') return buffer.ToArray();
            if (buffer.Length >= 32767) throw new InvalidDataException("JSON line > 32768 bytes");
            buffer.WriteByte((byte)b);
        }
    }
}

internal static class Database
{
    public static readonly string[] CsvFields = {
        "DATE_AND_TIME", "SOURCE_IP", "SOURCE_DOMAIN_NAME", "PROCESS_NAME", "PROCESS_ID",
        "RECIEVER_IP", "RECIEVER_DOMAINNAME", "PORT_USED", "SOFTWARE_THAT_USED", "SOURCE_PORT",
        "PROTOCOL", "STATE", "OPERATING_SYSTEM", "MONITORED_HOST", "SNAPSHOT_ID",
        "EVENT_TYPE", "EVENT_ID", "TIMESTAMP_UTC", "COLLECTOR_PEER_IP"
    };
    private static readonly Regex SafeHost = new(@"^[A-Za-z0-9_.-]{1,120}$", RegexOptions.Compiled);
    private static readonly HashSet<string> EventTypes = new(StringComparer.Ordinal) {
        "ACTIVE_AT_START", "TCP_CONNECTED", "TCP_DISCONNECTED", "HEARTBEAT"
    };
    private static SqliteConnection Connect(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate }.ToString());
        connection.Open();
        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA busy_timeout=15000; PRAGMA synchronous=FULL;";
        pragma.ExecuteNonQuery();
        return connection;
    }
    public static void Initialize(string path)
    {
        using var db = Connect(path);
        using var c = db.CreateCommand();
        c.CommandText = """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS events (
                event_id TEXT PRIMARY KEY,
                host TEXT NOT NULL,
                date_utc TEXT NOT NULL,
                timestamp_utc TEXT NOT NULL,
                payload_json TEXT NOT NULL,
                received_utc TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_events_host_date ON events(host,date_utc,timestamp_utc);
            CREATE TABLE IF NOT EXISTS dirty (
                host TEXT NOT NULL,
                date_utc TEXT NOT NULL,
                generation INTEGER NOT NULL DEFAULT 1,
                PRIMARY KEY(host,date_utc)
            );
            """;
        c.ExecuteNonQuery();
    }
    public static string Store(string path, JsonElement envelope, string peerIp)
    {
        if (envelope.ValueKind != JsonValueKind.Object || !envelope.TryGetProperty("event", out var row) ||
            row.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("Envelope must have an 'event' object");
        static string Required(JsonElement source, string name)
        {
            if (!source.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
                throw new ArgumentException("Missing required string field " + name);
            return value.GetString() ?? "";
        }
        string host = Required(row, "MONITORED_HOST");
        string eventId = Required(row, "EVENT_ID");
        string eventType = Required(row, "EVENT_TYPE");
        string timestamp = Required(row, "TIMESTAMP_UTC");
        if (host is "." or ".." || !SafeHost.IsMatch(host)) throw new ArgumentException("Invalid MONITORED_HOST");
        if (!Guid.TryParseExact(eventId, "D", out _)) throw new ArgumentException("Invalid EVENT_ID GUID");
        if (!EventTypes.Contains(eventType)) throw new ArgumentException("Invalid EVENT_TYPE");
        if (!timestamp.EndsWith('Z') && !timestamp.EndsWith("+00:00", StringComparison.Ordinal))
            throw new ArgumentException("TIMESTAMP_UTC must use UTC Z or +00:00");
        if (!DateTimeOffset.TryParse(timestamp, CultureInfo.InvariantCulture, DateTimeStyles.None, out var stamp) ||
            stamp.Offset != TimeSpan.Zero)
            throw new ArgumentException("Invalid UTC timestamp");
        string day = stamp.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var trusted = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var property in row.EnumerateObject()) trusted[property.Name] = property.Value.Clone();
        trusted["COLLECTOR_PEER_IP"] = peerIp; // ALWAYS overwrite agent-supplied peer IP.
        string payload = JsonSerializer.Serialize(trusted);
        using var db = Connect(path);
        using var transaction = db.BeginTransaction();
        using var insert = db.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = "INSERT OR IGNORE INTO events(event_id,host,date_utc,timestamp_utc,payload_json,received_utc) VALUES ($id,$host,$day,$stamp,$json,$now)";
        insert.Parameters.AddWithValue("$id", eventId);
        insert.Parameters.AddWithValue("$host", host);
        insert.Parameters.AddWithValue("$day", day);
        insert.Parameters.AddWithValue("$stamp", timestamp);
        insert.Parameters.AddWithValue("$json", payload);
        insert.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        int inserted = insert.ExecuteNonQuery();
        if (inserted > 0)
        {
            using var dirty = db.CreateCommand();
            dirty.Transaction = transaction;
            dirty.CommandText = "INSERT INTO dirty(host,date_utc,generation) VALUES ($host,$day,1) ON CONFLICT(host,date_utc) DO UPDATE SET generation=generation+1";
            dirty.Parameters.AddWithValue("$host", host);
            dirty.Parameters.AddWithValue("$day", day);
            dirty.ExecuteNonQuery();
        }
        transaction.Commit(); // ACK must only be sent after successful durable commit.
        return eventId;
    }

    public static void ExportDirty(string path, string csvDir)
    {
        var pending = new List<(string Host, string Day, long Generation)>();
        using (var db = Connect(path))
        using (var cmd = db.CreateCommand())
        {
            cmd.CommandText = "SELECT host,date_utc,generation FROM dirty ORDER BY date_utc,host";
            using var reader = cmd.ExecuteReader();
            while (reader.Read()) pending.Add((reader.GetString(0), reader.GetString(1), reader.GetInt64(2)));
        }
        foreach (var (host, day, generation) in pending)
        {
            var rows = new List<string>();
            using (var db = Connect(path))
            using (var cmd = db.CreateCommand())
            {
                cmd.CommandText = "SELECT payload_json FROM events WHERE host=$host AND date_utc=$day ORDER BY timestamp_utc,event_id";
                cmd.Parameters.AddWithValue("$host", host);
                cmd.Parameters.AddWithValue("$day", day);
                using var reader = cmd.ExecuteReader();
                while (reader.Read()) rows.Add(reader.GetString(0));
            }
            string folder = Path.Combine(csvDir, host);
            Directory.CreateDirectory(folder);
            string destination = Path.Combine(folder, day + ".csv");
            string temp = destination + ".tmp." + Guid.NewGuid().ToString("N");
            try
            {
                using (var writer = new StreamWriter(temp, false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true)))
                {
                    writer.WriteLine(string.Join(";", CsvFields.Select(Quote)));
                    foreach (var payload in rows)
                    {
                        using var json = JsonDocument.Parse(payload);
                        writer.WriteLine(string.Join(";", CsvFields.Select(field =>
                        {
                            if (!json.RootElement.TryGetProperty(field, out var value) || value.ValueKind == JsonValueKind.Null)
                                return Quote("");
                            return Quote(value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : value.ToString());
                        })));
                    }
                    writer.Flush();
                    ((FileStream)writer.BaseStream).Flush(flushToDisk: true);
                }
                File.Move(temp, destination, overwrite: true);
                using var db = Connect(path);
                using var c = db.CreateCommand();
                c.CommandText = "DELETE FROM dirty WHERE host=$host AND date_utc=$day AND generation=$gen";
                c.Parameters.AddWithValue("$host", host);
                c.Parameters.AddWithValue("$day", day);
                c.Parameters.AddWithValue("$gen", generation);
                c.ExecuteNonQuery();
                Log.Write($"EXPORTED {host} {day}: {rows.Count} events");
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
    }
    private static string Quote(string input)
    {
        // Avoid spreadsheet formula execution if a CSV is later opened in Excel.
        if (input.Length > 0 && (input[0] is '=' or '+' or '-' or '@' or '\t' or '\r')) input = "'" + input;
        return "\"" + input.Replace("\"", "\"\"") + "\"";
    }
}
