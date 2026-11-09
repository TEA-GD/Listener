namespace WatcherAgent;

/// <summary>
/// Default endpoint for older configuration files. The installer writes
/// collector_host and collector_port to agent.json to override these defaults.
/// No authentication tokens are used. All data is sent in cleartext. Restrict collector access by firewall and IP allowlist.
/// </summary>
internal static class CollectorSettings
{
    public const string Host = "watcher-collector.domain.local";
    public const int Port = 6515;

    public const int PollSeconds = 5;
    public const int HeartbeatSeconds = 60;
    public const int MaxSpoolEvents = 50000;
    public const int SendBatchSize = 1000;
}
