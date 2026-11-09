using System.Net;

namespace WatcherAgent;

internal sealed class AgentConfig
{
    public string agent_id { get; set; } = "";
    public string spool_dir { get; set; } = "";
    public string collector_host { get; set; } = CollectorSettings.Host;
    public int collector_port { get; set; } = CollectorSettings.Port;

    public void ValidateCollector()
    {
        collector_host = collector_host?.Trim() ?? "";
        if (string.IsNullOrEmpty(collector_host) || Uri.CheckHostName(collector_host) == UriHostNameType.Unknown)
            throw new ArgumentException("Enter a collector DNS hostname, such as collector.example.local (no http://, path or port). IP addresses are also supported.");
        if (collector_port is < 1 or > 65535)
            throw new ArgumentException("Collector port must be between 1 and 65535.");
    }
}
