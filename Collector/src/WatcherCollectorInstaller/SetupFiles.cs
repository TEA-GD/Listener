using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;

namespace WatcherCollectorInstaller;

internal static class SetupFiles
{
    internal static JsonObject Read(string path) => File.Exists(path)
        ? JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? throw new InvalidDataException("Configuration must be a JSON object: " + path)
        : new JsonObject();
    internal static void Default(JsonObject settings, string name, object value)
    {
        if (settings[name] is null) settings[name] = JsonSerializer.SerializeToNode(value);
    }
    internal static string Text(JsonObject settings, string name) => settings[name]?.GetValue<string>() ?? throw new InvalidDataException("Missing " + name);
    internal static void RefuseLink(string path)
    {
        if ((File.Exists(path) || Directory.Exists(path)) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Setup does not replace a linked file/directory: " + path);
    }
    internal static void Write(string path, Stream source)
    {
        RefuseLink(path); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string staged = path + ".setup-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var output = new FileStream(staged, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { source.CopyTo(output); output.Flush(true); }
            if (File.Exists(path)) File.Replace(staged, path, null); else File.Move(staged, path);
        }
        finally { if (File.Exists(staged)) File.Delete(staged); }
    }
    internal static void Save(string path, JsonObject value)
    {
        RefuseLink(path);
        if (File.Exists(path)) File.Copy(path, path + ".bak", true);
        using var source = new MemoryStream(Encoding.UTF8.GetBytes(value.ToJsonString(new JsonSerializerOptions { WriteIndented = true })));
        Write(path, source);
    }
    internal static JsonObject Collector(string file, string database, string csv, string log, int port, string[] ranges)
    {
        var s = Read(file);
        Default(s, "database_path", database); Default(s, "csv_dir", csv); Default(s, "log_file", log);
        Default(s, "bind_host", "0.0.0.0"); Default(s, "csv_export_seconds", 10); Default(s, "max_clients", 64);
        s["bind_port"] = port; s["allowed_client_cidrs"] = JsonSerializer.SerializeToNode(ranges);
        foreach (string field in new[] { "database_path", "csv_dir", "log_file" })
            if (!Path.IsPathFullyQualified(Text(s, field))) throw new InvalidDataException(field + " must be an absolute path; configuration was not replaced.");
        return s;
    }
    internal static JsonObject Analyzer(string file, string source, string data, bool connection)
    {
        var s = Read(file); s["database_path"] = source;
        Default(s, "poll_seconds", connection ? 5 : 10); Default(s, "batch_size", connection ? 1000 : 500);
        if (connection) Default(s, "analysis_database", Path.Combine(data, "connection-analysis.db"));
        else { Default(s, "state_directory", data); Default(s, "alerts_file", Path.Combine(data, "alerts.jsonl")); }
        return s;
    }
    internal static string TaskXml(string executable, string config, string log)
    {
        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        XElement E(string name, object value) => new(ns + name, value);
        return new XDocument(new XElement(ns + "Task", new XAttribute("version", "1.3"),
            E("Triggers", new XElement(ns + "BootTrigger", E("Enabled", "true"), E("Delay", "PT30S"))),
            E("Principals", new XElement(ns + "Principal", new XAttribute("id", "AnalyzerSystem"), E("UserId", "S-1-5-18"), E("RunLevel", "HighestAvailable"))),
            E("Settings", new object[] { E("MultipleInstancesPolicy", "IgnoreNew"), E("DisallowStartIfOnBatteries", "false"), E("StopIfGoingOnBatteries", "false"),
                E("StartWhenAvailable", "true"), E("RunOnlyIfNetworkAvailable", "false"), E("AllowStartOnDemand", "true"), E("Enabled", "true"), E("RunOnlyIfIdle", "false"), E("ExecutionTimeLimit", "PT0S"),
                new XElement(ns + "RestartOnFailure", E("Interval", "PT1M"), E("Count", "255")) }),
            new XElement(ns + "Actions", new XAttribute("Context", "AnalyzerSystem"),
                E("Exec", new object[] { E("Command", executable), E("Arguments", $"--config \"{config}\" --log \"{log}\""), E("WorkingDirectory", Path.GetDirectoryName(executable)!) })))).ToString();
    }
}
