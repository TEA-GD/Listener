using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json.Nodes;
using WatcherInstaller;

static void Check(bool condition,string message){if(!condition)throw new Exception(message);}
static MemoryStream Bytes(string text)=>new(Encoding.UTF8.GetBytes(text));
string root=Path.Combine(Path.GetTempPath(),"watcher-update-test-"+Guid.NewGuid());
Directory.CreateDirectory(root);
try
{
    string config=Path.Combine(root,"agent.json"),spool=Path.Combine(root,"custom-spool");
    Directory.CreateDirectory(spool);File.WriteAllText(Path.Combine(spool,"pending.json"),"unsent-evidence");
    string original="""{"agent_id":"PC-01","collector_host":"old.example","collector_port":6515,"collect_udp":false,"spool_dir":"custom-spool","custom_setting":{"keep":true}}""";
    File.WriteAllText(config,original);
    string merged=InstallationFiles.MergeConfiguration(original,"PC-01","new.example",16515,true,"default-spool","project_stands");
    var parsed=JsonNode.Parse(merged)!;
    Check((string?)parsed["device_category"]=="project_stands","Selected device category missing");
    Check((string?)JsonNode.Parse(InstallationFiles.MergeConfiguration(merged,"PC","server",6515,true,spool))!["device_category"]=="project_stands","Saved category lost on update");
    Check((string?)parsed["spool_dir"]=="custom-spool","Custom spool was discarded");
    Check((bool?)parsed["custom_setting"]?["keep"]==true,"Unknown existing settings were discarded");
    Check((string?)parsed["collector_host"]=="new.example"&&(int?)parsed["collector_port"]==16515,"Selected settings not updated");
    File.SetAttributes(config,File.GetAttributes(config)|FileAttributes.ReadOnly);
    using(var stream=Bytes(merged))InstallationFiles.WriteAtomically(config,stream);
    Check(File.ReadAllText(config)==merged,"Read-only config replacement failed");
    Check(File.ReadAllText(Path.Combine(spool,"pending.json"))=="unsent-evidence","Unsent event changed");
    try{using var broken=new FailingStream();InstallationFiles.WriteAtomically(config,broken);throw new Exception("Expected staging failure");}
    catch(IOException){}
    Check(File.ReadAllText(config)==merged,"Failed staging damaged the previous config");
    Check(!Directory.EnumerateFiles(root,"*.tmp").Any(),"Temporary files were left behind");
    bool rejected=false;try{InstallationFiles.MergeConfiguration("not-json","PC","server",6515,true,spool);}catch(System.Text.Json.JsonException){rejected=true;}
    Check(rejected,"Invalid config was silently replaced");
    Check((string?)JsonNode.Parse(InstallationFiles.MergeConfiguration(null,"PC","server",6515,true,spool))!["spool_dir"]==spool,"Fresh install default spool missing");
    if(!OperatingSystem.IsWindows())
    {
        string linked=Path.Combine(root,"linked.json");File.CreateSymbolicLink(linked,config);
        bool blocked=false;try{using var stream=Bytes("changed");InstallationFiles.WriteAtomically(linked,stream);}catch(IOException){blocked=true;}
        Check(blocked&&File.ReadAllText(config)==merged,"Linked configuration target was modified");
    }
    if(OperatingSystem.IsWindows())WindowsTests(root,config,merged);
    else Console.WriteLine("NTFS ACL and sharing-lock tests require Windows; portable preservation/replacement tests passed.");
    Console.WriteLine("PASS: config merge, custom spool preservation, read-only replacement, failed-write preservation, temp cleanup and invalid JSON handling");
}
finally{Directory.Delete(root,true);}

[SupportedOSPlatform("windows")]
static void WindowsTests(string root,string config,string expected)
{
    using var identity=WindowsIdentity.GetCurrent();
    if(!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))throw new Exception("Run Windows InstallerUpdate tests elevated to exercise ACL repair.");
    // Reproduce a legacy protected config: SYSTEM can write; Administrators cannot.
    var deny=new FileSecurity();deny.SetAccessRuleProtection(true,false);
    deny.SetOwner(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid,null));
    deny.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid,null),FileSystemRights.FullControl,AccessControlType.Allow));
    deny.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid,null),FileSystemRights.Read,AccessControlType.Allow));
    deny.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid,null),FileSystemRights.Write,AccessControlType.Deny));
    new FileInfo(config).SetAccessControl(deny);
    InstallationFiles.ProtectDirectory(root,Run);
    InstallationFiles.ProtectConfiguration(config,Run);
    using(var content=Bytes(expected))InstallationFiles.WriteAtomically(config,content);
    Check(File.ReadAllText(config)==expected,"Legacy protected config could not be updated");
    var acl=new FileInfo(config).GetAccessControl();
    Check(acl.AreAccessRulesProtected,"Config ACL unexpectedly inherits broad access");
    foreach(FileSystemAccessRule rule in acl.GetAccessRules(true,false,typeof(SecurityIdentifier)))
        Check(rule.AccessControlType==AccessControlType.Allow&&(rule.IdentityReference.Value=="S-1-5-18"||rule.IdentityReference.Value=="S-1-5-32-544"),"Unexpected config ACL");
    using(var locked=new FileStream(config,FileMode.Open,FileAccess.Read,FileShare.Read))
    {
        bool failed=false;try{using var content=Bytes("must-not-replace");InstallationFiles.WriteAtomically(config,content,1);}catch(IOException){failed=true;}
        Check(failed&&File.ReadAllText(config)==expected,"Locked config was overwritten or damaged");
    }
    Console.WriteLine("PASS: Windows explicit-deny ACL recovery, restricted permissions and locked-file preservation");
}
static void Run(string executable,string[] args)
{
    var info=new ProcessStartInfo(executable){UseShellExecute=false,CreateNoWindow=true};
    foreach(string arg in args)info.ArgumentList.Add(arg);
    using var process=Process.Start(info)!;process.WaitForExit();
    if(process.ExitCode!=0)throw new IOException(executable+" failed");
}
sealed class FailingStream:MemoryStream
{
    public override void CopyTo(Stream destination,int bufferSize){destination.WriteByte(42);throw new IOException("Simulated staging failure");}
}
