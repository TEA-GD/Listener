using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json.Nodes;

namespace WatcherInstaller;

// Kept independent of WinForms so config preservation and replacement can be tested.
internal static class InstallationFiles
{
    internal static void RefuseReparsePoint(string path)
    {
        if ((File.Exists(path) || Directory.Exists(path)) &&
            (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Installer cannot update a linked file or directory: " + path);
    }

    [SupportedOSPlatform("windows")]
    internal static void ProtectDirectory(string path, Action<string,string[]> run)
    {
        RefuseReparsePoint(path);
        if (Directory.Exists(path)) run("takeown.exe", new[]{"/F",path,"/A"});
        Directory.CreateDirectory(path);
        var security=new DirectorySecurity();
        security.SetOwner(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid,null));
        security.SetAccessRuleProtection(true,false);
        foreach(var sid in new[]{WellKnownSidType.LocalSystemSid,WellKnownSidType.BuiltinAdministratorsSid})
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid,null),FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit|InheritanceFlags.ObjectInherit,PropagationFlags.None,AccessControlType.Allow));
        new DirectoryInfo(path).SetAccessControl(security);
    }

    [SupportedOSPlatform("windows")]
    internal static void ProtectConfiguration(string path, Action<string,string[]> run)
    {
        RefuseReparsePoint(path);
        if (!File.Exists(path)) return;
        // Ownership gives the elevated installer WRITE_DAC even if a legacy DACL
        // grants only SYSTEM or contains an explicit deny for Administrators.
        run("takeown.exe",new[]{"/F",path,"/A"});
        var security=new FileSecurity();
        security.SetOwner(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid,null));
        security.SetAccessRuleProtection(true,false);
        foreach(var sid in new[]{WellKnownSidType.LocalSystemSid,WellKnownSidType.BuiltinAdministratorsSid})
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid,null),FileSystemRights.FullControl,AccessControlType.Allow));
        new FileInfo(path).SetAccessControl(security);
        File.SetAttributes(path,File.GetAttributes(path)&~FileAttributes.ReadOnly);
    }

    internal static string MergeConfiguration(string? existing,string agentId,string collectorHost,int collectorPort,bool collectUdp,string defaultSpool,string? category=null)
    {
        var settings=existing is null?new JsonObject():JsonNode.Parse(existing) as JsonObject
            ??throw new InvalidDataException("Existing agent.json must be a JSON object; it was not overwritten.");
        if(category is not null) settings["device_category"]=WatcherShared.DeviceCategories.Validate(category);
        settings["agent_id"]=agentId;
        settings["collector_host"]=collectorHost;
        settings["collector_port"]=collectorPort;
        settings["collect_udp"]=collectUdp;
        if(string.IsNullOrWhiteSpace(settings["spool_dir"]?.GetValue<string>()))settings["spool_dir"]=defaultSpool;
        return settings.ToJsonString(new System.Text.Json.JsonSerializerOptions{WriteIndented=true});
    }

    internal static void WriteAtomically(string path,Stream source,int attempts=20)
    {
        RefuseReparsePoint(path);
        string temporary=path+".update-"+Guid.NewGuid().ToString("N")+".tmp";
        FileAttributes? attributes=File.Exists(path)?File.GetAttributes(path):null;
        bool replaced=false;
        try {
            using(var output=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None)) {
                source.CopyTo(output);output.Flush(true);
            }
            if(attributes is not null && (attributes.Value&FileAttributes.ReadOnly)!=0)
                File.SetAttributes(path,attributes.Value&~FileAttributes.ReadOnly);
            for(int i=0;;i++) {
                try {
                    if(File.Exists(path))File.Replace(temporary,path,null);
                    else File.Move(temporary,path);
                    replaced=true;break;
                }
                catch(Exception ex) when((ex is IOException or UnauthorizedAccessException)&&i+1<attempts){Thread.Sleep(250);}
            }
        }
        finally {
            if(!replaced&&attributes is not null&&File.Exists(path)) {
                try{File.SetAttributes(path,attributes.Value);}catch(IOException){}catch(UnauthorizedAccessException){}
            }
            if(File.Exists(temporary))File.Delete(temporary);
        }
    }
}
