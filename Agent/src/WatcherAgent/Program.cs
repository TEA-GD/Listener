using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace WatcherAgent;

internal sealed record Connection(string LocalIP, int LocalPort, string RemoteIP,
                                  int RemotePort, int PID, string Process, string Software, string Protocol = "TCP", string SocketState = "ESTABLISHED", string Direction = "unknown", string DirectionBasis = "unavailable")
{
    public string Key => string.Join("\u001f", LocalIP, LocalPort, RemoteIP, RemotePort, PID, Protocol);
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
        $tcp=@()
        try { $tcp=@(Get-NetTCPConnection -State Established -ErrorAction Stop) }
        catch { if ($_.FullyQualifiedErrorId -notlike 'CmdletizationQuery_NotFound*') { throw } }
        $listeners=@();$listenKnown=$true
        try { $listeners=@(Get-NetTCPConnection -State Listen -ErrorAction Stop) }
        catch { if ($_.FullyQualifiedErrorId -notlike 'CmdletizationQuery_NotFound*') { $listenKnown=$false } }
        $rows=@($tcp | ForEach-Object {
          $socket=$_;$direction='unknown';$basis='unavailable'
          if($listenKnown){
            $matching=@($listeners | Where-Object {
              $_.LocalPort -eq $socket.LocalPort -and
              ($_.LocalAddress -eq $socket.LocalAddress -or $_.LocalAddress -eq '0.0.0.0' -or $_.LocalAddress -eq '::') -and
              ($_.OwningProcess -eq $socket.OwningProcess -or $_.OwningProcess -eq 0 -or $socket.OwningProcess -eq 0)
            })
            if($matching.Count){$direction='incoming';$basis='local_listener'}else{$direction='outgoing';$basis='no_local_listener'}
          }
          $p=$procs[[int]$_.OwningProcess]
          [PSCustomObject]@{local_ip=[string]$_.LocalAddress;local_port=[int]$_.LocalPort;
           remote_ip=[string]$_.RemoteAddress;remote_port=[int]$_.RemotePort;
           pid=[int]$_.OwningProcess;process=[string]$p.Name;software=[string]$p.Path;protocol='TCP';socket_state='ESTABLISHED';connection_direction=$direction;direction_basis=$basis}
        })
        if (__COLLECT_UDP__) {
          $udp=@()
          try { $udp=@(Get-NetUDPEndpoint -ErrorAction Stop) }
          catch { if ($_.FullyQualifiedErrorId -notlike 'CmdletizationQuery_NotFound*') { throw } }
          $rows+=@($udp | ForEach-Object {
            $p=$procs[[int]$_.OwningProcess]
            [PSCustomObject]@{local_ip=[string]$_.LocalAddress;local_port=[int]$_.LocalPort;
             remote_ip='';remote_port=0;pid=[int]$_.OwningProcess;process=[string]$p.Name;software=[string]$p.Path;protocol='UDP';socket_state='BOUND'}
          })
        }
        ConvertTo-Json -InputObject @($rows) -Compress -Depth 3
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
            cfg.ValidateCollector();
            Directory.CreateDirectory(cfg.spool_dir);
            // Hold this for the process lifetime: the task and a manual launch must
            // never run competing uploaders against the same spool.
            using var spoolLock = new FileStream(Path.Combine(cfg.spool_dir, "agent.lock"),
                FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            using var stopping = new CancellationTokenSource();
            ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; stopping.Cancel(); };
            Console.CancelKeyPress += cancel;
            using var terminate = OperatingSystem.IsLinux()
                ? PosixSignalRegistration.Create(PosixSignal.SIGTERM, context => { context.Cancel = true; stopping.Cancel(); })
                : null;
            try
            {
                Console.WriteLine($"WatcherAgent {cfg.agent_id} -> {cfg.collector_host}:{cfg.collector_port}");
                using var uploader = once ? null : new UploadWorker(cfg, stopping.Token);
                using var mailBridge = new MailBridge(cfg, rows => {
                    QueueBatch(cfg.spool_dir, rows);
                    uploader?.Wake();
                });
                Run(cfg, once, stopping.Token, uploader);
                return 0;
            }
            finally { Console.CancelKeyPress -= cancel; }
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

    private static void Run(AgentConfig cfg, bool once, CancellationToken stoppingToken, UploadWorker? uploader)
    {
        Dictionary<string, Connection>? previous = null;
        DateTimeOffset lastHeartbeat = DateTimeOffset.MinValue;
        do
        {
            var started = Stopwatch.StartNew();
            var snapshot = DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssfffZ", CultureInfo.InvariantCulture);
            try
            {
                var current = Collect(cfg.collect_udp);
                // Retain the first estimate for the lifetime of an observed socket;
                // its listener may close before the accepted connection disappears.
                if (previous is not null) foreach (string key in current.Keys.ToArray())
                    if (previous.TryGetValue(key, out var prior) && prior.Direction != "unknown")
                        current[key] = current[key] with { Direction = prior.Direction, DirectionBasis = prior.DirectionBasis };
                var changes = new List<(string Event, Connection Connection)>();
                if (previous is null)
                {
                    changes.AddRange(current.Values.Select(c => (c.Protocol == "UDP" ? "UDP_ENDPOINT_AT_START" : "ACTIVE_AT_START", c)));
                }
                else
                {
                    foreach (var pair in current)
                        if (!previous.ContainsKey(pair.Key)) changes.Add((pair.Value.Protocol == "UDP" ? "UDP_ENDPOINT_OPENED" : "TCP_CONNECTED", pair.Value));
                    foreach (var pair in previous)
                        if (!current.ContainsKey(pair.Key)) changes.Add((pair.Value.Protocol == "UDP" ? "UDP_ENDPOINT_CLOSED" : "TCP_DISCONNECTED", pair.Value));
                }
                // Fail before adding any events if the spool lacks space for the batch.
                // Do not advance the baseline on failures; retry next iteration.
                bool heartbeatDue = DateTimeOffset.UtcNow - lastHeartbeat >=
                                    TimeSpan.FromSeconds(CollectorSettings.HeartbeatSeconds);
                var events = new List<Dictionary<string, string>>();
                foreach (var (type, connection) in changes)
                    events.Add(CreateEvent(cfg.agent_id, snapshot, type, connection));
                if (heartbeatDue) { events.AddRange(CreateSnapshot(cfg.agent_id, snapshot, current.Values)); events.AddRange(NetworkContext.Capture(cfg.agent_id,snapshot,cfg.device_category)); }
                foreach (var row in events) row["DEVICE_CATEGORY"] = cfg.device_category;
                QueueBatch(cfg.spool_dir, events);
                uploader?.Wake();
                previous = current;
                if (heartbeatDue) lastHeartbeat = DateTimeOffset.UtcNow;
                Console.WriteLine($"{DateTimeOffset.Now:o} sockets={current.Count} changes={changes.Count}");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Collection/spool error: {ex.Message}");
            }
            if (once)
            {
                // Foreground diagnostics still perform one bounded upload pass.
                try
                {
                    int delivered = SendQueuedEventsAsync(cfg, stoppingToken, limitDuration: true).GetAwaiter().GetResult();
                    if (delivered > 0) Console.WriteLine($"Acknowledged: {delivered}");
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
                catch (Exception ex) { Console.Error.WriteLine($"Collector unavailable; events retained: {ex.Message}"); }
                break;
            }
            int delay = (int)Math.Max(0, CollectorSettings.PollSeconds * 1000 - started.ElapsedMilliseconds);
            if (stoppingToken.WaitHandle.WaitOne(delay)) break;
        } while (!stoppingToken.IsCancellationRequested);
    }

    private static Dictionary<string, Connection> Collect(bool collectUdp = true)
    {
        var entries = OperatingSystem.IsWindows() ? CollectWindows(collectUdp) : CollectLinux(collectUdp);
        var output = new Dictionary<string, Connection>(StringComparer.Ordinal);
        foreach (var c in entries)
        {
            if (c.LocalIP.Length == 0 || (c.Protocol == "TCP" && c.RemoteIP.Length == 0) ||
                (IPAddress.TryParse(c.RemoteIP, out var remote) && IPAddress.IsLoopback(remote))) continue;
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

    private static IEnumerable<Connection> CollectWindows(bool collectUdp)
    {
        string data = RunCommand("powershell.exe", "-NoProfile", "-NonInteractive", "-Command", WindowsQuery.Replace("__COLLECT_UDP__", collectUdp ? "$true" : "$false", StringComparison.Ordinal));
        return ParseWindowsConnections(data);
    }

    private static IEnumerable<Connection> ParseWindowsConnections(string data)
    {
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
                           Str(item, "process", "Unknown"), Str(item, "software"),
                           Str(item, "protocol", "TCP"), Str(item, "socket_state", "ESTABLISHED"),
                           Str(item, "connection_direction", "unknown"), Str(item, "direction_basis", "unavailable")));
        }
        return output;
    }

    private static string Str(JsonElement x, string name, string fallback = "")
    {
        if (!x.TryGetProperty(name, out var p) || p.ValueKind == JsonValueKind.Null) return fallback;
        return p.ToString();
    }

    private static IEnumerable<Connection> CollectLinux(bool collectUdp)
    {
        var rows = ParseLinuxConnections(RunCommand("ss", "-H", "-n", "-t", "-p", "state", "established"), "TCP").ToList();
        try
        {
            var listeners = ParseLinuxListeners(RunCommand("ss", "-H", "-n", "-t", "-l", "-p")).ToArray();
            rows = rows.Select(c => EstimateDirection(c, listeners)).ToList();
        }
        catch { /* Missing listener visibility retains Unknown; never guess from port ranges. */ }
        if (collectUdp) rows.AddRange(ParseLinuxConnections(RunCommand("ss", "-H", "-n", "-u", "-a", "-p"), "UDP"));
        return rows;
    }

    internal sealed record Listener(string IP, int Port, int PID);

    internal static IEnumerable<Listener> ParseLinuxListeners(string data)
    {
        foreach (string line in data.Split('\n'))
        {
            var fields = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            int at = fields.Length > 0 && fields[0] == "LISTEN" ? 3 : 2;
            if (fields.Length <= at || !Endpoint(fields[at], out string ip, out int port)) continue;
            var owner = LinuxProc.Match(line);
            yield return new Listener(ip, port, owner.Success ? int.Parse(owner.Groups["pid"].Value, CultureInfo.InvariantCulture) : 0);
        }
    }

    internal static Connection EstimateDirection(Connection socket, IEnumerable<Listener> listeners)
    {
        if (socket.Protocol != "TCP") return socket;
        bool match = listeners.Any(l => l.Port == socket.LocalPort &&
            (l.IP == socket.LocalIP || l.IP is "0.0.0.0" or "::" or "*") &&
            (l.PID == socket.PID || l.PID == 0 || socket.PID == 0));
        return socket with { Direction = match ? "incoming" : "outgoing", DirectionBasis = match ? "local_listener" : "no_local_listener" };
    }

    private static IEnumerable<Connection> ParseLinuxConnections(string data, string protocol)
    {
        var output = new List<Connection>();
        foreach (var line in data.Split('\n'))
        {
            var words = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (words.Length < 4) continue;
            int i = words[0].Equals("ESTAB", StringComparison.OrdinalIgnoreCase) ||
                    words[0].Equals("ESTABLISHED", StringComparison.OrdinalIgnoreCase) ||
                    words[0].Equals("UNCONN", StringComparison.OrdinalIgnoreCase) ||
                    words[0].Equals("UNCONNECTED", StringComparison.OrdinalIgnoreCase) ? 3 : 2;
            if (words.Length <= i + 1 || !Endpoint(words[i], out string lip, out int lp) ||
                !Endpoint(words[i + 1], out string rip, out int rp, protocol == "UDP")) continue;
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
            if (protocol == "UDP" && (rp == 0 || rip is "*" or "0.0.0.0" or "::")) { rip = ""; rp = 0; }
            output.Add(new(lip, lp, rip, rp, pid, name, software, protocol,
                protocol == "UDP" ? (rip.Length > 0 ? "PEER_CONFIGURED" : "BOUND") : "ESTABLISHED"));
        }
        return output;
    }

    private static bool Endpoint(string token, out string ip, out int port, bool allowWildcard = false)
    {
        ip = ""; port = 0;
        int pos = token.LastIndexOf(':');
        if (pos < 1) return false;
        if (!(allowWildcard && token[(pos + 1)..] == "*") && !int.TryParse(token[(pos + 1)..], out port)) return false;
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
            ["PORT_USED"] = c is null || c.RemotePort == 0 ? "" : c.RemotePort.ToString(CultureInfo.InvariantCulture),
            ["SOFTWARE_THAT_USED"] = c?.Software ?? "",
            ["SOURCE_PORT"] = c is null ? "" : c.LocalPort.ToString(CultureInfo.InvariantCulture),
            ["PROTOCOL"] = c?.Protocol ?? "",
            ["CONNECTION_DIRECTION"] = c?.Direction ?? "unknown",
            ["DIRECTION_BASIS"] = c?.DirectionBasis ?? "unavailable",
            ["STATE"] = type is "TCP_DISCONNECTED" or "UDP_ENDPOINT_CLOSED" ? "CLOSED_OR_NOT_OBSERVED" : c?.SocketState ?? "",
            ["OPERATING_SYSTEM"] = isLinux ? "Linux" : "Windows",
            ["MONITORED_HOST"] = agentId,
            ["SNAPSHOT_ID"] = snapshot
        };
    }

    private static List<Dictionary<string,string>> CreateSnapshot(string agentId, string snapshot, IEnumerable<Connection> sockets)
    {
        var rows=sockets.Select(c=>CreateEvent(agentId,snapshot,c.Protocol=="UDP"?"UDP_SNAPSHOT":"TCP_SNAPSHOT",c)).ToList();
        var complete=CreateEvent(agentId,snapshot,"HEARTBEAT",null);
        complete["STATE"]="SNAPSHOT_COMPLETE:"+rows.Count.ToString(CultureInfo.InvariantCulture);
        rows.Add(complete); // A complete zero-socket snapshot also resets old state.
        return rows;
    }

    private static readonly object SpoolGate=new();
    private static void QueueBatch(string dir, List<Dictionary<string, string>> events)
    {
        lock(SpoolGate)QueueBatchLocked(dir,events);
    }
    private static void QueueBatchLocked(string dir,List<Dictionary<string,string>> events)
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

    // Retain a synchronous entry point for foreground diagnostics and transport checks.
    private static int SendQueuedEvents(AgentConfig cfg) =>
        SendQueuedEventsAsync(cfg, CancellationToken.None, limitDuration: true).GetAwaiter().GetResult();

    internal static async Task<int> SendQueuedEventsAsync(AgentConfig cfg, CancellationToken stoppingToken, bool limitDuration = false)
    {
        stoppingToken.ThrowIfCancellationRequested();
        var files = Directory.GetFiles(cfg.spool_dir, "*.json");
        Array.Sort(files, StringComparer.Ordinal);
        if (files.Length == 0) return 0;
        var passClock = Stopwatch.StartNew();
        using var tcp = new TcpClient { NoDelay = true };
        using (var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken))
        {
            connectTimeout.CancelAfter(TimeSpan.FromSeconds(8));
            await tcp.ConnectAsync(cfg.collector_host, cfg.collector_port, connectTimeout.Token);
        }
        using var network = tcp.GetStream();
        using var reader = new StreamReader(network, Encoding.ASCII, false, 1024, leaveOpen: true);
        int sent = 0, index = 0, window = 1;
        var pending = files.Take(CollectorSettings.SendBatchSize).ToArray();
        byte[] Encode(string path, bool negotiate, out string id)
        {
            byte[] body = File.ReadAllBytes(path);
            if (body.Length > 30000) throw new IOException($"Oversized event, retained: {path}");
            using var payload = JsonDocument.Parse(body);
            id = payload.RootElement.GetProperty("EVENT_ID").GetString()!;
            byte[] json = negotiate
                ? JsonSerializer.SerializeToUtf8Bytes(new { @event = payload.RootElement, request_pipeline = true })
                : JsonSerializer.SerializeToUtf8Bytes(new { @event = payload.RootElement });
            if (json.Length + 1 > 32768) throw new IOException("Envelope too large; event retained");
            byte[] line = new byte[json.Length + 1];
            json.CopyTo(line, 0);
            line[^1] = (byte)'\n';
            return line;
        }
        while (index < pending.Length)
        {
            stoppingToken.ThrowIfCancellationRequested();
            if (limitDuration && sent > 0 && passClock.Elapsed >= TimeSpan.FromSeconds(5)) break;
            var group = new List<(string Path, string Id, byte[] Body)>();
            int bytes = 0;
            while (index < pending.Length && group.Count < window)
            {
                byte[] body = Encode(pending[index], index == 0, out string id);
                if (group.Count > 0 && bytes + body.Length > 131072) break;
                group.Add((pending[index], id, body));
                bytes += body.Length;
                index++;
            }
            // Collection runs independently; no five-second yield is needed here.
            // Preserve bounded groups and ordered, per-event durable ACKs.
            using (var writeTimeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken))
            {
                writeTimeout.CancelAfter(TimeSpan.FromSeconds(10));
                foreach (var item in group) await network.WriteAsync(item.Body, writeTimeout.Token);
            }
            foreach (var item in group)
            {
                using var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                readTimeout.CancelAfter(TimeSpan.FromSeconds(10));
                string? ack = await reader.ReadLineAsync(readTimeout.Token);
                if (sent == 0 && ack == "ACK " + item.Id + " PIPELINE64") window = 64;
                else if (ack != "ACK " + item.Id) throw new IOException("Collector did not acknowledge event: " + ack);
                File.Delete(item.Path);
                sent++;
            }
        }
        return sent;
    }
}
