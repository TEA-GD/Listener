using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Text;
using System.Text.Json;

namespace WatcherAgent;
internal static class NetworkContext
{
    internal static List<Dictionary<string,string>> Capture(string host,string snapshot,string category)
    {
        var networks=new List<Dictionary<string,string>>();var processes=new List<Dictionary<string,string>>();string status="ok",note="";
        try{
            if(OperatingSystem.IsWindows()){
                using var source=typeof(NetworkContext).Assembly.GetManifestResourceStream("Watcher.NetworkContext.ps1")!;
                using var reader=new StreamReader(source);
                string raw=Execute("powershell.exe",["-NoProfile","-NonInteractive","-EncodedCommand",Convert.ToBase64String(Encoding.Unicode.GetBytes(reader.ReadToEnd()))],15000);
                using var doc=JsonDocument.Parse(raw);
                foreach(var row in doc.RootElement.GetProperty("networks").EnumerateArray())networks.Add(Read(row));
                foreach(var row in doc.RootElement.GetProperty("processes").EnumerateArray())processes.Add(Read(row));
                status=doc.RootElement.GetProperty("status").GetString()!;note=doc.RootElement.GetProperty("note").GetString()!;
            }else{
                var names=new Dictionary<string,string>();
                try{foreach(string line in Execute("nmcli",["-t","--escape","no","-f","DEVICE,CONNECTION","device","status"],2000).Split('\n')){int i=line.IndexOf(':');if(i>0)names[line[..i]]=line[(i+1)..].Trim();}}catch{note="Network names unavailable without NetworkManager; adapter metadata retained.";}
                foreach(var adapter in NetworkInterface.GetAllNetworkInterfaces().Where(a=>a.OperationalStatus==OperationalStatus.Up&&a.NetworkInterfaceType!=NetworkInterfaceType.Loopback)){
                    var ip=adapter.GetIPProperties();
                    string type=adapter.NetworkInterfaceType==NetworkInterfaceType.Wireless80211?"wifi":adapter.NetworkInterfaceType==NetworkInterfaceType.Ethernet?"ethernet":"other";
                    if(adapter.Name.StartsWith("tun")||adapter.Name.StartsWith("wg")||adapter.NetworkInterfaceType==NetworkInterfaceType.Ppp)type="vpn";
                    networks.Add(new(){{"adapter_id",adapter.Id},{"adapter_name",adapter.Name},{"network_type",type},{"network_name",names.GetValueOrDefault(adapter.Name,"")},{"connection_type",adapter.Description},{"state","Up"},{"addresses",string.Join(',',ip.UnicastAddresses.Select(a=>a.Address.ToString()))},{"gateway",string.Join(',',ip.GatewayAddresses.Select(a=>a.Address.ToString()))}});
                }
                foreach(var process in Process.GetProcesses())using(process){try{string path="",start="";try{path=process.MainModule?.FileName??"";start=process.StartTime.ToUniversalTime().ToString("o");}catch{}
                    processes.Add(new(){{"entry_type","process"},{"pid",process.Id.ToString()},{"name",process.ProcessName},{"path",path},{"started_utc",start}});
                }catch{status="partial";}}
                try{foreach(string line in Execute("systemctl",["list-units","--type=service","--state=running","--no-legend","--no-pager","--plain"],2000).Split('\n',StringSplitOptions.RemoveEmptyEntries)){
                    string name=line.Trim().Split(' ',StringSplitOptions.RemoveEmptyEntries)[0];processes.Add(new(){{"entry_type","service"},{"pid","0"},{"name",name},{"path",""},{"started_utc",""}});
                }}catch{status="partial";note+=" Running services unavailable.";}
            }
        }catch{status="partial";note="Some network/process metadata could not be sampled.";}
        if(processes.Count>4096){processes=processes.Take(4096).ToList();status="partial";note+=" Process sample capped at 4096.";}
        if(networks.Count>128){networks=networks.Take(128).ToList();status="partial";}
        foreach(var row in networks.Concat(processes))foreach(string key in row.Keys.ToArray())if(row[key].Length>256){row[key]=row[key][..256];status="partial";note="Some metadata fields were truncated to 256 characters.";}
        string stamp=DateTimeOffset.UtcNow.ToString("o");
        Dictionary<string,string> Event(string type,object data)=>new(){{"EVENT_ID",Guid.NewGuid().ToString("D")},{"EVENT_TYPE",type},{"MONITORED_HOST",host},{"TIMESTAMP_UTC",stamp},{"SNAPSHOT_ID",snapshot},{"DEVICE_CATEGORY",category},{"CONTEXT_JSON",JsonSerializer.Serialize(data)}};
        var result=networks.Select(n=>Event("NETWORK_CONTEXT",n)).ToList();result.AddRange(processes.Select(p=>Event("PROCESS_CONTEXT",p)));
        result.Add(Event("CONTEXT_COMPLETE",new { networks=networks.Count,processes=processes.Count,status,note }));return result;
    }
    private static Dictionary<string,string> Read(JsonElement row)=>row.EnumerateObject().ToDictionary(p=>p.Name,p=>p.Value.ToString());
    private static string Execute(string file,string[] args,int timeout){
        var info=new ProcessStartInfo(file){UseShellExecute=false,RedirectStandardOutput=true,RedirectStandardError=true,CreateNoWindow=true};foreach(string arg in args)info.ArgumentList.Add(arg);
        using var process=Process.Start(info)!;var output=process.StandardOutput.ReadToEndAsync();var error=process.StandardError.ReadToEndAsync();
        if(!process.WaitForExit(timeout)){process.Kill(true);throw new IOException("Metadata command timed out");}
        if(process.ExitCode!=0)throw new IOException("Metadata command failed");return output.GetAwaiter().GetResult();
    }
}
