using System.Net;
using System.Net.NetworkInformation;
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
        TestUdp();
        var createSnapshot=typeof(WatcherAgent.Program).GetMethod("CreateSnapshot",BindingFlags.NonPublic|BindingFlags.Static)!;
        var sockets=new[]{new Connection("10.0.0.1",5000,"198.51.100.1",443,12,"app","app"),new Connection("0.0.0.0",5353,"",0,13,"udp","udp","UDP","BOUND")};
        var full=(List<Dictionary<string,string>>)createSnapshot.Invoke(null,new object[]{"agent","test-snapshot",sockets})!;
        Check(full.Count==3&&full[^1]["STATE"]=="SNAPSHOT_COMPLETE:2","Snapshot completion/count missing");
        Check(full.Select(r=>r["EVENT_TYPE"]).SequenceEqual(new[]{"TCP_SNAPSHOT","UDP_SNAPSHOT","HEARTBEAT"}),"Snapshot protocols mixed");
        Check(full.All(r=>r["SNAPSHOT_ID"]=="test-snapshot"),"Snapshot IDs differ");
        var empty=(List<Dictionary<string,string>>)createSnapshot.Invoke(null,new object[]{"agent","empty",Array.Empty<Connection>()})!;
        Check(empty.Count==1&&empty[0]["STATE"]=="SNAPSHOT_COMPLETE:0","Empty state must reset prior sockets");
        foreach (bool acknowledge in new[] { true, false }) await TestDelivery(acknowledge);
        await TestSlowBacklog();
        await TestPipeline(false);
        await TestPipeline(true);
        await TestPipeline(false,true);
        Console.WriteLine("PASS: hostname configuration, legacy defaults, validation, DNS TCP delivery, ACK deletion and failed-ACK retention");
    }

    private static async Task TestPipeline(bool reject,bool large=false)
    {
        string root=Path.Combine(Path.GetTempPath(),"watcher-pipeline-"+Guid.NewGuid());Directory.CreateDirectory(root);
        var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();
        var ids=Enumerable.Range(0,130).Select(_=>Guid.NewGuid().ToString()).ToArray();
        try{
            string padding=new string('x',large?20000:0);
            int encodedSize=JsonSerializer.SerializeToUtf8Bytes(new { @event=new { EVENT_ID=ids[0],TIMESTAMP_UTC="2026-11-01T00:00:00Z",padding } }).Length+1;
            for(int i=0;i<ids.Length;i++)File.WriteAllText(Path.Combine(root,i.ToString("D4")+".json"),JsonSerializer.Serialize(new{EVENT_ID=ids[i],TIMESTAMP_UTC="2026-11-01T00:00:00Z",padding}));
            var server=Task.Run(async()=>{
                using var client=await listener.AcceptTcpClientAsync();using var stream=client.GetStream();using var reader=new StreamReader(stream);
                using(var first=JsonDocument.Parse((await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)))!))Check(first.RootElement.GetProperty("request_pipeline").GetBoolean(),"Negotiation missing");
                await stream.WriteAsync(Encoding.ASCII.GetBytes("ACK "+ids[0]+" PIPELINE64\n"));
                int at=1;
                while(at<ids.Length){
                    int count=Math.Min(Math.Min(64,131072/encodedSize),ids.Length-at);
                    for(int n=0;n<count;n++){
                        using var row=JsonDocument.Parse((await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)))!);
                        Check(row.RootElement.GetProperty("event").GetProperty("EVENT_ID").GetString()==ids[at+n],"Pipeline changed FIFO order");
                        Check(row.RootElement.GetProperty("event").GetProperty("TIMESTAMP_UTC").GetString()=="2026-11-01T00:00:00Z","Replay timestamp changed");
                    }
                    // All requests must arrive BEFORE this group's acknowledgments.
                    await Task.Delay(100);
                    if(reject){await stream.WriteAsync(Encoding.ASCII.GetBytes(string.Concat(ids.Skip(at).Take(10).Select(id=>"ACK "+id+"\n"))+"ERR rejected event\n"));return;}
                    await stream.WriteAsync(Encoding.ASCII.GetBytes(string.Concat(ids.Skip(at).Take(count).Select(id=>"ACK "+id+"\n"))));at+=count;
                }
            });
            var config=new AgentConfig{collector_host="localhost",collector_port=((IPEndPoint)listener.LocalEndpoint).Port,spool_dir=root};bool failed=false;
            try{int sent=await Task.Run(()=>(int)typeof(WatcherAgent.Program).GetMethod("SendQueuedEvents",BindingFlags.NonPublic|BindingFlags.Static)!.Invoke(null,new object[]{config})!);Check(sent==130,"Not all pipeline records delivered");}
            catch(TargetInvocationException error) when(error.InnerException is IOException){failed=true;}
            await server;Check(failed==reject,"Pipeline rejection incorrectly reported");
            Check(Directory.GetFiles(root,"*.json").Length==(reject?119:0),"Missing ACK deleted pending files or matching ACK not removed");
        }
        finally{listener.Stop();Directory.Delete(root,true);}
    }

    private static async Task TestSlowBacklog()
    {
        string root=Path.Combine(Path.GetTempPath(),"watcher-slow-backlog-"+Guid.NewGuid());Directory.CreateDirectory(root);
        var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();
        try{
            const string stamp="2026-11-01T00:00:00Z";
            for(int i=0;i<8;i++)File.WriteAllText(Path.Combine(root,i.ToString("D4")+".json"),JsonSerializer.Serialize(new { EVENT_ID=Guid.NewGuid().ToString(),TIMESTAMP_UTC=stamp }));
            var server=Task.Run(async()=>{
                using var client=await listener.AcceptTcpClientAsync();using var stream=client.GetStream();using var reader=new StreamReader(stream);
                while(await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(15)) is string line){
                    using var data=JsonDocument.Parse(line);var row=data.RootElement.GetProperty("event");
                    Check(row.GetProperty("TIMESTAMP_UTC").GetString()==stamp,"Replay replaced original timestamp");
                    await Task.Delay(1100);await stream.WriteAsync(Encoding.ASCII.GetBytes("ACK "+row.GetProperty("EVENT_ID").GetString()+"\n"));
                }
            });
            var config=new AgentConfig { collector_host="localhost",collector_port=((IPEndPoint)listener.LocalEndpoint).Port,spool_dir=root };
            int sent=await Task.Run(()=>(int)typeof(WatcherAgent.Program).GetMethod("SendQueuedEvents",BindingFlags.NonPublic|BindingFlags.Static)!.Invoke(null,new object[]{config})!);
            await server;
            Check(sent>0&&sent<8,"Slow replay did not yield to collection");
            Check(Directory.GetFiles(root,"*.json").Length==8-sent,"Unsent backlog was lost");
        }
        finally{listener.Stop();Directory.Delete(root,true);}
    }

    private static void TestUdp()
    {
        var methods=typeof(WatcherAgent.Program);
        var udp=new Connection("0.0.0.0",5353,"",0,2147483000,"udp-app","/udp-app","UDP","BOUND");
        var tcp=udp with { Protocol="TCP" };
        Check(udp.Key!=tcp.Key,"TCP/UDP socket keys collide");
        var create=methods.GetMethod("CreateEvent",BindingFlags.NonPublic|BindingFlags.Static)!;
        var row=(Dictionary<string,string>)create.Invoke(null,new object?[]{"test-agent","snapshot","UDP_ENDPOINT_AT_START",udp})!;
        Check(row["PROTOCOL"]=="UDP"&&row["SOURCE_PORT"]=="5353"&&row["RECIEVER_IP"]==""&&row["PORT_USED"]==""&&row["STATE"]=="BOUND","UDP bind invented a destination");
        var closed=(Dictionary<string,string>)create.Invoke(null,new object?[]{"test-agent","snapshot","UDP_ENDPOINT_CLOSED",udp})!;
        Check(closed["STATE"]=="CLOSED_OR_NOT_OBSERVED","UDP close state incorrect");
        var parse=methods.GetMethod("ParseLinuxConnections",BindingFlags.NonPublic|BindingFlags.Static)!;
        const string data="UNCONN 0 0 0.0.0.0:5353 0.0.0.0:* users:((\"udp-app\",pid=2147483000,fd=8))\nESTAB 0 0 192.0.2.5:50001 203.0.113.5:443\nUNCONN 0 0 [::]:5353 [::]:*\nUNCONN 0 0 *:123 *:*";
        var parsed=((IEnumerable<Connection>)parse.Invoke(null,new object[]{data,"UDP"})!).ToArray();
        Check(parsed.Length==4,"UDP wildcard/IPv6 parser dropped endpoints");
        Check(parsed[0].RemoteIP==""&&parsed[0].RemotePort==0&&parsed[0].SocketState=="BOUND","Wildcard UDP peer must remain unknown");
        Check(parsed[1].RemoteIP=="203.0.113.5"&&parsed[1].RemotePort==443&&parsed[1].SocketState=="PEER_CONFIGURED","Configured UDP peer lost");
        var windows=methods.GetMethod("ParseWindowsConnections",BindingFlags.NonPublic|BindingFlags.Static)!;
        var win=((IEnumerable<Connection>)windows.Invoke(null,new object[]{"[{\"local_ip\":\"0.0.0.0\",\"local_port\":5353,\"remote_ip\":\"\",\"remote_port\":0,\"pid\":123,\"process\":\"app\",\"protocol\":\"UDP\",\"socket_state\":\"BOUND\"}]"})!).Single();
        Check(win.Protocol=="UDP"&&win.RemoteIP==""&&win.LocalPort==5353,"Windows UDP parsing failed");
        if(OperatingSystem.IsLinux())
        {
            using var socket=new UdpClient(new IPEndPoint(IPAddress.Any,0));
            int port=((IPEndPoint)socket.Client.LocalEndPoint!).Port;
            var collect=methods.GetMethod("Collect",BindingFlags.NonPublic|BindingFlags.Static)!;
            var snapshot=(Dictionary<string,Connection>)collect.Invoke(null,new object[]{true})!;
            Check(snapshot.Values.Any(c=>c.Protocol=="UDP"&&c.LocalPort==port&&c.RemoteIP==""),"Live bound UDP socket not observed");
            var tcpOnly=(Dictionary<string,Connection>)collect.Invoke(null,new object[]{false})!;
            Check(tcpOnly.Values.All(c=>c.Protocol=="TCP"),"collect_udp=false was ignored");
            var local=NetworkInterface.GetAllNetworkInterfaces().SelectMany(n=>n.GetIPProperties().UnicastAddresses).Select(a=>a.Address).FirstOrDefault(a=>a.AddressFamily==AddressFamily.InterNetwork&&!IPAddress.IsLoopback(a));
            if(local is not null)
            {
                using var connected=new UdpClient();connected.Connect(local,port); // Sets a peer; sends no datagram.
                int sourcePort=((IPEndPoint)connected.Client.LocalEndPoint!).Port;
                snapshot=(Dictionary<string,Connection>)collect.Invoke(null,new object[]{true})!;
                Check(snapshot.Values.Any(c=>c.Protocol=="UDP"&&c.LocalPort==sourcePort&&c.RemoteIP==local.ToString()&&c.SocketState=="PEER_CONFIGURED"),"Live configured UDP peer not observed");
            }
        }
        Console.WriteLine("PASS: UDP event fields, wildcard/IPv6 parsers, protocol separation and live Linux socket snapshots");
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
