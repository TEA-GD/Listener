using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using WatcherAgent;

internal static class AgentIntegration
{
    private static void Check(bool value, string message)
    {
        if (!value) throw new Exception(message);
    }

    private static async Task Main()
    {
        var old = JsonSerializer.Deserialize<AgentConfig>("{\"agent_id\":\"old-agent\",\"spool_dir\":\"spool\"}")!;
        old.ValidateCollector();
        Check(old.collector_host == CollectorSettings.Host && old.collector_port == CollectorSettings.Port,
            "Old configuration defaults changed");
        var domain = new AgentConfig { collector_host = " collector.example.local ", collector_port = 16515 };
        domain.ValidateCollector();
        var saved = JsonSerializer.Deserialize<AgentConfig>(JsonSerializer.Serialize(domain))!;
        Check(saved.collector_host == "collector.example.local" && saved.collector_port == 16515,
            "Domain/port not preserved in configuration");
        foreach (string invalid in new[] { "", "https://collector.example.local", "collector.example.local:6515", "host/path", "host name" })
        {
            bool rejected = false;
            try { new AgentConfig { collector_host = invalid }.ValidateCollector(); }
            catch (ArgumentException) { rejected = true; }
            Check(rejected, "Invalid hostname accepted: " + invalid);
        }
        foreach (int port in new[] { 0, 65536 })
        {
            bool rejected = false;
            try { new AgentConfig { collector_port = port }.ValidateCollector(); }
            catch (ArgumentException) { rejected = true; }
            Check(rejected, "Invalid port accepted");
        }
        foreach (bool acknowledge in new[] { true, false }) await TestDelivery(acknowledge);
        Console.WriteLine("PASS: hostname configuration, legacy defaults, validation, DNS TCP delivery, ACK deletion and failed-ACK retention");
    }

    private static async Task TestDelivery(bool acknowledge)
    {
        string root = Path.Combine(Path.GetTempPath(), "watcher-agent-test-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            string id = Guid.NewGuid().ToString();
            string file = Path.Combine(root, "event.json");
            File.WriteAllText(file, JsonSerializer.Serialize(new { EVENT_ID = id, EVENT_TYPE = "HEARTBEAT" }));
            var cfg = new AgentConfig { collector_host = "localhost", collector_port = ((IPEndPoint)listener.LocalEndpoint).Port, spool_dir = root };
            cfg.ValidateCollector();
            var server = Task.Run(async () =>
            {
                using var client = await listener.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(10));
                using var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
                string request = (await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)))!;
                using var envelope = JsonDocument.Parse(request);
                Check(envelope.RootElement.GetProperty("event").GetProperty("EVENT_ID").GetString() == id,
                    "Wrong event delivered");
                await stream.WriteAsync(Encoding.ASCII.GetBytes(acknowledge ? "ACK " + id + "\n" : "ERR rejected\n"));
            });
            var method = typeof(WatcherAgent.Program).GetMethod("SendQueuedEvents", BindingFlags.NonPublic | BindingFlags.Static)!;
            bool failed = false;
            try { Check((int)method.Invoke(null, new object[] { cfg })! == 1, "Unexpected delivery count"); }
            catch (TargetInvocationException ex) when (ex.InnerException is IOException) { failed = true; }
            await server;
            Check(failed != acknowledge, "Unexpected delivery outcome");
            Check(File.Exists(file) != acknowledge, "Spool deletion must require matching ACK");
        }
        finally { listener.Stop(); Directory.Delete(root, true); }
    }
}
