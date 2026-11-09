using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace WatcherAgent;

internal sealed class AgentConfig
{
    public string agent_id { get; set; } = "";
    public string spool_dir { get; set; } = "";
}

internal sealed record Connection(string LocalIP, int LocalPort, string RemoteIP,
                                  int RemotePort, int PID, string Process, string Software)
{
    public string Key => string.Join("\u001f", LocalIP, LocalPort, RemoteIP, RemotePort, PID);
}

internal static class Program
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };
    private static readonly Regex LinuxProc = new("users:\\(\\(\"(?<name>[^\"]+)\",pid=(?<pid>\\d+)", RegexOptions.Compiled);
    private const string WindowsQuery = """
        $ErrorActionPreference='Stop'
        [Console]::OutputEncoding=New-Object System.Text.UTF8Encoding($false)
        $procs=@{}
        Get-CimInstance Win32_Process | ForEach-Object { $procs[[int]$_.ProcessId] = @{Name=[string]$_.Name;Path=[string]$_.ExecutablePath} }
        $rows=@(Get-NetTCPConnection -State Established | ForEach-Object {
          $p=$procs[[int]$_.OwningProcess]
          [PSCustomObject]@{local_ip=[string]$_.LocalAddress;local_port=[int]$_.LocalPort;
           remote_ip=[string]$_.RemoteAddress;remote_port=[int]$_.RemotePort;
           pid=[int]$_.OwningProcess;process=[string]$p.Name;software=[string]$p.Path}
        })
        ConvertTo-Json -InputObject $rows -Compress -Depth 3
        """;

    private static int Main(string[] args)
    {
        try
        {
            bool once = args.Contains("--once", StringComparer.OrdinalIgnoreCase);
            string? configPath = Option(args, "--config") ??
                (OperatingSystem.IsWindows()
                    ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "WatcherAgent", "agent.json")
                    : "/etc/watcher-agent/agent.json");
            if (args.Contains("--help"))
            {
                Console.WriteLine("WatcherAgent [--config PATH] [--once]");
                return 0;
            }
            AgentConfig cfg = JsonSerializer.Deserialize<AgentConfig>(File.ReadAllText(configPath))
                              ?? throw new InvalidOperationException("Invalid configuration JSON");
            if (!Regex.IsMatch(cfg.agent_id, @"^[A-Za-z0-9_.-]{1,120}$"))
                throw new InvalidOperationException("Invalid agent_id; must contain only letters, digits, dots, underscores or hyphens.");
            if (string.IsNullOrWhiteSpace(cfg.spool_dir))
                throw new InvalidOperationException("spool_dir is required.");
            Directory.CreateDirectory(cfg.spool_dir);
            Console.WriteLine($"WatcherAgent {cfg.agent_id} -> {CollectorSettings.Host}:{CollectorSettings.Port}");
            Run(cfg, once);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"WatcherAgent fatal: {ex.Message}");
            return 1;
        }
    }

    private static string? Option(string[] args, string name)
    {
        for (int i = 0; i + 1 < args.Length; i++)
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
        return null;
    }

    private static void Run(AgentConfig cfg, bool once)
    {
        Dictionary<string, Connection>? previous = null;
        DateTimeOffset lastHeartbeat = DateTimeOffset.MinValue;
        do
        {
            var started = Stopwatch.StartNew();
            var snapshot = DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssfffZ", CultureInfo.InvariantCulture);
            try
            {
                var current = Collect();
                var changes = new List<(string Event, Connection Connection)>();
                if (previous is null)
                {
                    changes.AddRange(current.Values.Select(c => ("ACTIVE_AT_START", c)));
                }
                else
                {
                    foreach (var pair in current)
                        if (!previous.ContainsKey(pair.Key)) changes.Add(("TCP_CONNECTED", pair.Value));
                    foreach (var pair in previous)
                        if (!current.ContainsKey(pair.Key)) changes.Add(("TCP_DISCONNECTED", pair.Value));
                }
                // Fail before adding any events if the spool lacks space for the batch.
                // Do not advance the baseline on failures; retry next iteration.
                bool heartbeatDue = DateTimeOffset.UtcNow - lastHeartbeat >=
                                    TimeSpan.FromSeconds(CollectorSettings.HeartbeatSeconds);
                var events = new List<Dictionary<string, string>>();
                foreach (var (type, connection) in changes)
                    events.Add(CreateEvent(cfg.agent_id, snapshot, type, connection));
                if (heartbeatDue) events.Add(CreateEvent(cfg.agent_id, snapshot, "HEARTBEAT", null));
                QueueBatch(cfg.spool_dir, events);
                previous = current;
                if (heartbeatDue) lastHeartbeat = DateTimeOffset.UtcNow;
                Console.WriteLine($"{DateTimeOffset.Now:o} sockets={current.Count} changes={changes.Count}");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Collection/spool error: {ex.Message}");
            }
            try
            {
                int delivered = SendQueuedEvents(cfg);
                if (delivered > 0) Console.WriteLine($"Acknowledged: {delivered}");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Collector unavailable; events retained: {ex.Message}");
            }
            if (once) break;
            Thread.Sleep(TimeSpan.FromMilliseconds(Math.Max(0, CollectorSettings.PollSeconds * 1000 - started.ElapsedMilliseconds)));
        } while (true);
    }

    private static Dictionary<string, Connection> Collect()
    {
        var entries = OperatingSystem.IsWindows() ? CollectWindows() : CollectLinux();
        var output = new Dictionary<string, Connection>(StringComparer.Ordinal);
        foreach (var c in entries)
        {
            if (c.RemoteIP is "127.0.0.1" or "::1" or "" || c.LocalIP.Length == 0) continue;
            output[c.Key] = c;
        }
        return output;
    }

    private static string RunCommand(string command, params string[] args)
    {
        var start = new ProcessStartInfo(command)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new IOException("Could not run " + command);
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(25000))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("Collection command timed out: " + command);
        }
        Task.WaitAll(stdout, stderr);
        if (process.ExitCode != 0) throw new IOException($"{command} exited {process.ExitCode}: {stderr.Result[..Math.Min(250, stderr.Result.Length)]}");
        return stdout.Result;
    }

    private static IEnumerable<Connection> CollectWindows()
    {
        string data = RunCommand("powershell.exe", "-NoProfile", "-NonInteractive", "-Command", WindowsQuery);
        using JsonDocument doc = JsonDocument.Parse(data);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) throw new FormatException("Unexpected PowerShell output");
        var output = new List<Connection>();
        foreach (var item in doc.RootElement.EnumerateArray())
        {
            string local = Str(item, "local_ip");
            string remote = Str(item, "remote_ip");
            if (!int.TryParse(Str(item, "local_port"), out int lp) ||
                !int.TryParse(Str(item, "remote_port"), out int rp)) continue;
            int.TryParse(Str(item, "pid"), out int pid);
            output.Add(new(local, lp, remote, rp, pid,
                           Str(item, "process", "Unknown"), Str(item, "software")));
        }
        return output;
    }

    private static string Str(JsonElement x, string name, string fallback = "")
    {
        if (!x.TryGetProperty(name, out var p) || p.ValueKind == JsonValueKind.Null) return fallback;
        return p.ToString();
    }

    private static IEnumerable<Connection> CollectLinux()
    {
        string data = RunCommand("ss", "-H", "-n", "-t", "-p", "state", "established");
        var output = new List<Connection>();
        foreach (var line in data.Split('\n'))
        {
            var words = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (words.Length < 4) continue;
            int i = words[0].Equals("ESTAB", StringComparison.OrdinalIgnoreCase) ||
                    words[0].Equals("ESTABLISHED", StringComparison.OrdinalIgnoreCase) ? 3 : 2;
            if (words.Length <= i + 1 || !Endpoint(words[i], out string lip, out int lp) ||
                !Endpoint(words[i + 1], out string rip, out int rp)) continue;
            string details = string.Join(" ", words.Skip(i + 2));
            var match = LinuxProc.Match(details);
            int pid = match.Success ? int.Parse(match.Groups["pid"].Value, CultureInfo.InvariantCulture) : 0;
            string name = match.Success ? match.Groups["name"].Value : "Unknown";
            string software = "";
            if (pid > 0)
            {
                try { software = File.ResolveLinkTarget($"/proc/{pid}/exe", true)?.FullName ?? ""; }
                catch (Exception) { /* Process may have exited or be inaccessible. */ }
            }
            output.Add(new(lip, lp, rip, rp, pid, name, software));
        }
        return output;
    }

    private static bool Endpoint(string token, out string ip, out int port)
    {
        ip = ""; port = 0;
        int pos = token.LastIndexOf(':');
        if (pos < 1 || !int.TryParse(token[(pos + 1)..], out port)) return false;
        ip = token[..pos].Trim('[', ']');
        return ip.Length > 0;
    }

    private static Dictionary<string, string> CreateEvent(string agentId, string snapshot, string type, Connection? c)
    {
        bool isLinux = OperatingSystem.IsLinux();
        return new Dictionary<string, string>
        {
            ["EVENT_ID"] = Guid.NewGuid().ToString(),
            ["EVENT_TYPE"] = type,
            ["TIMESTAMP_UTC"] = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffzzz", CultureInfo.InvariantCulture),
            ["DATE_AND_TIME"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture),
            ["SOURCE_IP"] = c?.LocalIP ?? "",
            ["SOURCE_DOMAIN_NAME"] = agentId,
            ["PROCESS_NAME"] = c?.Process ?? "",
            ["PROCESS_ID"] = c is null ? "" : c.PID.ToString(CultureInfo.InvariantCulture),
            ["RECIEVER_IP"] = c?.RemoteIP ?? "",
            ["RECIEVER_DOMAINNAME"] = "",
            ["PORT_USED"] = c is null ? "" : c.RemotePort.ToString(CultureInfo.InvariantCulture),
            ["SOFTWARE_THAT_USED"] = c?.Software ?? "",
            ["SOURCE_PORT"] = c is null ? "" : c.LocalPort.ToString(CultureInfo.InvariantCulture),
            ["PROTOCOL"] = c is null ? "" : "TCP",
            ["STATE"] = type == "TCP_DISCONNECTED" ? "CLOSED_OR_NOT_OBSERVED" : c is null ? "" : "ESTABLISHED",
            ["OPERATING_SYSTEM"] = isLinux ? "Linux" : "Windows",
            ["MONITORED_HOST"] = agentId,
            ["SNAPSHOT_ID"] = snapshot
        };
    }

    private static void QueueBatch(string dir, List<Dictionary<string, string>> events)
    {
        int pending = Directory.EnumerateFiles(dir, "*.json").Count();
        if (pending + events.Count > CollectorSettings.MaxSpoolEvents)
            throw new IOException("Spool capacity exceeded; refusing to discard queued evidence");
        foreach (var data in events)
        {
            string file = DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssfffffffZ", CultureInfo.InvariantCulture)
                          + "_" + data["EVENT_ID"];
            string tmp = Path.Combine(dir, file + ".tmp");
            string final = Path.Combine(dir, file + ".json");
            using (var stream = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                byte[] json = JsonSerializer.SerializeToUtf8Bytes(data, JsonOptions);
                stream.Write(json);
                stream.Flush(flushToDisk: true);
            }
            File.Move(tmp, final);
        }
    }

    private static int SendQueuedEvents(AgentConfig cfg)
    {
        var files = Directory.GetFiles(cfg.spool_dir, "*.json");
        Array.Sort(files, StringComparer.Ordinal);
        if (files.Length == 0) return 0;
        using var tcp = new TcpClient();
        tcp.ConnectAsync(CollectorSettings.Host, CollectorSettings.Port)
           .WaitAsync(TimeSpan.FromSeconds(8)).GetAwaiter().GetResult();
        tcp.ReceiveTimeout = 10000;
        tcp.SendTimeout = 10000;
        using var network = tcp.GetStream();
        network.ReadTimeout = 10000;
        network.WriteTimeout = 10000;
        using var reader = new StreamReader(network, Encoding.ASCII, false, 1024, leaveOpen: true);
        int sent = 0;
        foreach (var path in files.Take(CollectorSettings.SendBatchSize))
        {
            byte[] body = File.ReadAllBytes(path);
            if (body.Length > 30000) throw new IOException($"Oversized event, retained: {path}");
            using var payload = JsonDocument.Parse(body);
            string eventId = payload.RootElement.GetProperty("EVENT_ID").GetString()!;
            byte[] request = JsonSerializer.SerializeToUtf8Bytes(new
            {
                @event = payload.RootElement
            });
            if (request.Length + 1 > 32768) throw new IOException("Envelope too large; event retained");
            network.Write(request);
            network.WriteByte((byte)'\n');
            network.Flush();
            string? ack = reader.ReadLine();
            if (ack != "ACK " + eventId) throw new IOException("Collector did not acknowledge event: " + ack);
            File.Delete(path);
            sent++;
        }
        return sent;
    }

}
