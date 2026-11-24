using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;

namespace WatcherCollectorInstaller;

internal sealed class AnalyzerInstallation(string app, string collectorData, Func<string,IEnumerable<string>,bool,string> run, Action<string> log)
{
    internal sealed record Entry(string Task, string Binary, string Config, string Data, bool Connection, string Resource);
    internal static string CommonData => Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
    internal Entry[] Entries => new[] {
        new Entry("Watcher-Connection-Analyzer", Path.Combine(app,"Analyzers","Connection","Listener_ConnectionAnalyzer.exe"), Path.Combine(CommonData,"WatcherConnectionAnalyzer","config.json"), Path.Combine(CommonData,"WatcherConnectionAnalyzer"), true,"Watcher.ConnectionAnalyzer.Exe"),
        new Entry("Watcher-Behavior-Analyzer", Path.Combine(app,"Analyzers","Behavior","Listener_Analyzer.exe"), Path.Combine(CommonData,"WatcherAnalyzer","config.json"), Path.Combine(CommonData,"WatcherAnalyzer"), false,"Watcher.BehaviorAnalyzer.Exe") };
    internal string Manifest => Path.Combine(collectorData,"analyzers.json");
    internal bool Enabled => SetupFiles.Read(Manifest)["enabled"]?.GetValue<bool>() ?? false;
    internal void Install(string database)
    {
        foreach (var e in Entries)
        {
            SetupFiles.RefuseLink(e.Data); SetupFiles.RefuseLink(Path.GetDirectoryName(e.Binary)!);
            Directory.CreateDirectory(e.Data); Directory.CreateDirectory(Path.GetDirectoryName(e.Binary)!);
            run("icacls.exe", new[] {e.Data,"/inheritance:r","/grant:r","*S-1-5-18:(OI)(CI)F","*S-1-5-32-544:(OI)(CI)F"},false);
            var config = SetupFiles.Analyzer(e.Config,database,e.Data,e.Connection);
            using (var payload = Assembly.GetExecutingAssembly().GetManifestResourceStream(e.Resource) ?? throw new FileNotFoundException("Missing embedded analyzer: "+e.Resource)) SetupFiles.Write(e.Binary,payload);
            SetupFiles.Save(e.Config,config);
            log("Checking analyzer configuration: "+e.Task);
            log(run(e.Binary,new[] {"--config",e.Config,"--check-config"},false).Trim());
            string xml = Path.Combine(e.Data,"startup-task.xml");
            try
            {
                File.WriteAllText(xml,SetupFiles.TaskXml(e.Binary,e.Config,Path.Combine(e.Data,"analyzer.log")),Encoding.Unicode);
                run("schtasks.exe",new[] {"/Create","/TN",e.Task,"/XML",xml,"/RU","SYSTEM","/F"},false);
            }
            finally { if(File.Exists(xml))File.Delete(xml); }
            log($"Installed {e.Task}; configuration: {e.Config}");
        }
    }
    internal void SetEnabled(bool enabled)
    {
        if(!enabled) foreach(var e in Entries)
        {
            if(TaskExists(e.Task))run("schtasks.exe",new[] {"/Change","/TN",e.Task,"/Disable"},false);
        }
        SetupFiles.Save(Manifest,new JsonObject { ["enabled"]=enabled });
    }
    private bool TaskExists(string task)
    {
        try { run("schtasks.exe",new[] {"/Query","/TN",task},false);return true; }
        catch(IOException){return false;}
    }
    internal void Start()
    {
        if(!Enabled)return;
        foreach(var e in Entries)
        {
            if(!File.Exists(e.Binary)||!File.Exists(e.Config)||!TaskExists(e.Task))throw new IOException("Analyzer installation incomplete; run Install / Update: "+e.Task);
            run("schtasks.exe",new[] {"/Run","/TN",e.Task},false);
            string path=e.Binary.Replace("'","''");
            string ps="$ErrorActionPreference='Stop'; $target='"+path+"'; $running=$false; "+
                "for($i=0;$i -lt 30;$i++){ $p=@(Get-CimInstance Win32_Process -Filter \"Name='"+Path.GetFileName(e.Binary)+"'\" | Where-Object {$_.ExecutablePath -ieq $target}); "+
                "if($p.Count){Start-Sleep -Milliseconds 500; $p=@(Get-CimInstance Win32_Process -Filter \"Name='"+Path.GetFileName(e.Binary)+"'\" | Where-Object {$_.ExecutablePath -ieq $target}); if($p.Count){$running=$true;break}}; Start-Sleep -Milliseconds 250 }; "+
                "if(-not $running){throw 'Analyzer task did not remain running. Check its saved analyzer.log and Task Scheduler Last Run Result.'}";
            run("powershell.exe",new[] {"-NoProfile","-NonInteractive","-Command",ps},false);
            log("Running: "+e.Task);
        }
    }
    internal void Stop()
    {
        foreach(var e in Entries)
        {
            if(TaskExists(e.Task))run("schtasks.exe",new[] {"/End","/TN",e.Task},true);
            string path=e.Binary.Replace("'","''");
            // Match the managed path, not every analyzer with the same filename.
            string ps="$ErrorActionPreference='Stop'; $target='"+path+"'; "+
                "Get-CimInstance Win32_Process -Filter \"Name='"+Path.GetFileName(e.Binary)+"'\" | Where-Object {$_.ExecutablePath -ieq $target} | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction Stop }";
            run("powershell.exe",new[] {"-NoProfile","-NonInteractive","-Command",ps},false);
        }
    }
    internal void RemoveTasks()
    {
        Stop();
        foreach(var e in Entries)if(TaskExists(e.Task))run("schtasks.exe",new[] {"/Delete","/TN",e.Task,"/F"},false);
        log("Analyzer startup tasks removed; analyzer configuration, history and alerts preserved.");
    }
}
