using System.Text.Json.Nodes;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Xml.Linq;
using WatcherCollectorInstaller;

static void Check(bool value, string message) { if (!value) throw new Exception(message); }
string root=Path.Combine(Path.GetTempPath(),"collector-setup-"+Guid.NewGuid());Directory.CreateDirectory(root);
try
{
    string file=Path.Combine(root,"collector.json"), source=Path.Combine(root,"custom.db");
    File.WriteAllText(file,new JsonObject { ["database_path"]=source,["csv_dir"]=Path.Combine(root,"custom-csv"),["max_clients"]=7,["csv_export_seconds"]=123,["custom_setting"]="keep" }.ToJsonString());
    var config=SetupFiles.Collector(file,Path.Combine(root,"default.db"),Path.Combine(root,"csv"),Path.Combine(root,"log"),7654,new[]{"10.0.0.0/24"});
    Check(config["database_path"]!.GetValue<string>()==source && config["max_clients"]!.GetValue<int>()==7 && config["csv_export_seconds"]!.GetValue<int>()==123,"Collector settings reset");
    Check(config["custom_setting"]!.GetValue<string>()=="keep" && config["bind_port"]!.GetValue<int>()==7654,"Configuration merge failed");
    SetupFiles.Save(file,config);Check(File.Exists(file+".bak"),"Missing backup");
    string analyzer=Path.Combine(root,"analyzer.json"), state=Path.Combine(root,"state-custom");
    File.WriteAllText(analyzer,new JsonObject { ["state_directory"]=state,["baseline_days"]=14,["alerts_file"]="custom-alerts.jsonl",["unknown"]="preserved" }.ToJsonString());
    var behavioral=SetupFiles.Analyzer(analyzer,source,root,false);
    Check(behavioral["database_path"]!.GetValue<string>()==source && behavioral["baseline_days"]!.GetValue<int>()==14 && behavioral["state_directory"]!.GetValue<string>()==state,"Analyzer settings reset");
    Check(behavioral["alerts_file"]!.GetValue<string>()=="custom-alerts.jsonl" && behavioral["unknown"]!.GetValue<string>()=="preserved","Custom alerts/settings lost");
    var connection=SetupFiles.Analyzer(Path.Combine(root,"new.json"),source,root,true);
    Check(connection["analysis_database"]!.GetValue<string>()==Path.Combine(root,"connection-analysis.db"),"GUI default path differs");
    File.WriteAllText(analyzer,"not JSON");bool rejected=false;try{SetupFiles.Analyzer(analyzer,source,root,true);}catch(System.Text.Json.JsonException){rejected=true;}
    Check(rejected && File.ReadAllText(analyzer)=="not JSON","Unreadable configuration was overwritten");
    XNamespace ns="http://schemas.microsoft.com/windows/2004/02/mit/task";
    var xml=XDocument.Parse(SetupFiles.TaskXml(Path.Combine(root,"tools & modules","analyzer.exe"),Path.Combine(root,"config.json"),Path.Combine(root,"analyzer.log")));
    string Value(string key)=>xml.Descendants(ns+key).Single().Value;
    Check(Value("UserId")=="S-1-5-18" && Value("RunLevel")=="HighestAvailable","Task principal wrong");
    Check(Value("DisallowStartIfOnBatteries")=="false" && Value("StopIfGoingOnBatteries")=="false" && Value("ExecutionTimeLimit")=="PT0S","Task battery/runtime restrictions returned");
    Check(Value("MultipleInstancesPolicy")=="IgnoreNew" && Value("Arguments").Contains("--log"),"Task duplicate/log settings missing");
    string log=Path.Combine(root,"persistent.log");
    using(AnalyzerLog.Open(new[]{"--log",log})){Console.WriteLine("stdout-record");Console.Error.WriteLine("stderr-record");}
    Check(File.ReadAllText(log).Contains("stdout-record") && File.ReadAllText(log).Contains("stderr-record"),"Background logs missing");
    if (args.Length > 0)
    {
        using var assembly = File.OpenRead(args[0]); using var pe = new PEReader(assembly);
        var metadata = pe.GetMetadataReader();
        var resources = metadata.ManifestResources.Select(h => metadata.GetManifestResource(h)).ToDictionary(r => metadata.GetString(r.Name));
        foreach (string name in new[] { "Watcher.Collector.Exe", "Watcher.ConnectionAnalyzer.Exe", "Watcher.BehaviorAnalyzer.Exe" })
        {
            Check(resources.TryGetValue(name, out var resource) && resource.Implementation.IsNil, "Missing embedded executable: " + name);
            var content = pe.GetSectionData(pe.PEHeaders.CorHeader!.ResourcesDirectory.RelativeVirtualAddress + (int)resource.Offset).GetContent(0, 6).ToArray();
            Check(BitConverter.ToInt32(content,0)>1000000 && content[4]=='M' && content[5]=='Z', "Embedded payload is not a complete executable: " + name);
        }
        Console.WriteLine("PASS: Windows installer embeds all three executable payloads");
    }
    Console.WriteLine("PASS: Collector/analyzer preservation, actual DB binding, backups, malformed JSON rejection, battery-safe task XML and persistent logging");
}
finally{Directory.Delete(root,true);}
