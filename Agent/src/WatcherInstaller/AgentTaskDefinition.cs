using System.Xml.Linq;

namespace WatcherInstaller;

internal static class AgentTaskDefinition
{
    internal static string Create(string executable,string configuration,string workingDirectory)
    {
        XNamespace ns="http://schemas.microsoft.com/windows/2004/02/mit/task";
        XElement E(string name,object value)=>new(ns+name,value);
        return new XDocument(new XElement(ns+"Task",new XAttribute("version","1.3"),
            E("RegistrationInfo",E("Description","Watcher connection agent: starts at boot and runs on AC or battery power.")),
            E("Triggers",new XElement(ns+"BootTrigger",E("Enabled","true"),E("Delay","PT30S"))),
            E("Principals",new XElement(ns+"Principal",new XAttribute("id","AgentSystem"),
                E("UserId","S-1-5-18"),E("RunLevel","HighestAvailable"))),
            new XElement(ns+"Settings",
                E("MultipleInstancesPolicy","IgnoreNew"),
                E("DisallowStartIfOnBatteries","false"),E("StopIfGoingOnBatteries","false"),
                E("StartWhenAvailable","true"),E("RunOnlyIfNetworkAvailable","false"),
                E("AllowStartOnDemand","true"),E("Enabled","true"),E("RunOnlyIfIdle","false"),
                E("ExecutionTimeLimit","PT0S"),
                new XElement(ns+"RestartOnFailure",E("Interval","PT1M"),E("Count","255"))),
            new XElement(ns+"Actions",new XAttribute("Context","AgentSystem"),
                new XElement(ns+"Exec",E("Command",executable),E("Arguments","--config \""+configuration+"\""),E("WorkingDirectory",workingDirectory)))
        )).ToString();
    }
}
