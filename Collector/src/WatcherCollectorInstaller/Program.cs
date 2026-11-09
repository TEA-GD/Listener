using System.Diagnostics;
using System.Drawing;
using System.Net;
using System.Reflection;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Windows.Forms;

namespace WatcherCollectorInstaller;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new SetupForm());
    }
}

internal sealed class SetupForm : Form
{
    private const string ServiceName = "WatcherCollector";
    private static readonly string AppDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Watcher", "Collector");
    private static readonly string DataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "WatcherCollector");
    private static readonly string Executable = Path.Combine(AppDir, "WatcherCollector.exe");
    private static readonly string ConfigPath = Path.Combine(DataDir, "collector.json");

    private readonly TextBox _host = new();
    private readonly TextBox _networks = new();
    private readonly NumericUpDown _port = new();
    private readonly TextBox _log = new();
    private readonly Button _install = new();
    private readonly Button _start = new();
    private readonly Button _stop = new();
    private readonly Button _uninstall = new();
    private readonly CheckBox _deleteData = new();

    public SetupForm()
    {
        Text = "Watcher Collector — Windows Setup V6";
        ClientSize = new Size(760, 690);
        MinimumSize = new Size(760, 690);
        Font = new Font("Segoe UI", 9);
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        AddLabel("Install Watcher Collector (plain TCP — no certificates or tokens)", 20, 20, bold: true);
        AddLabel("Collector DNS name (informational; set target in Agent/CollectorSettings.cs)", 20, 64);
        _host.SetBounds(20, 88, 716, 27);
        _host.Text = Environment.MachineName;
        _host.ReadOnly = true;
        Controls.Add(_host);
        AddLabel("Listening port", 20, 132);
        _port.Minimum = 1;
        _port.Maximum = 65535;
        _port.Value = 6515;
        _port.SetBounds(20, 155, 155, 27);
        Controls.Add(_port);
        AddLabel("Optional allowed agent networks (CIDRs; leave blank to accept any sender)", 20, 198);
        _networks.SetBounds(20, 224, 716, 27);
        _networks.PlaceholderText = "Blank = any sender; or e.g. 192.168.88.0/24";
        Controls.Add(_networks);
        AddLabel("WARNING: Plain TCP; no encryption or sender authentication. Use restricted VLAN/firewall.", 20, 269, bold: true);
        AddLabel("Installation messages", 20, 310);
        _log.SetBounds(20, 333, 716, 202);
        _log.Multiline = true;
        _log.ScrollBars = ScrollBars.Vertical;
        _log.ReadOnly = true;
        _log.Font = new Font("Consolas", 9);
        _log.Text = "No Python, certificate or token required. This installer embeds WatcherCollector.exe.\r\n" +
            $"Service: {ServiceName}\r\nData: {DataDir}\r\n" +
            "Listens on all IPv4 interfaces. Agent networks are optional.\r\n";
        Controls.Add(_log);
        _deleteData.Text = "Also delete collector configuration, SQLite database and logs in ProgramData";
        _deleteData.SetBounds(20, 547, 710, 25);
        _deleteData.Checked = false; // Preserve collector history by default.
        Controls.Add(_deleteData);
        AddLabel("CSV exports are always preserved, including any custom export location.", 20, 575);

        _install.Text = "Install / Update";
        _install.SetBounds(20, 625, 148, 36);
        _install.Click += (_, _) => Execute(Install);
        Controls.Add(_install);
        _start.Text = "Start Service";
        _start.SetBounds(179, 625, 127, 36);
        _start.Click += (_, _) => Execute(StartService);
        Controls.Add(_start);
        _stop.Text = "Stop Service";
        _stop.SetBounds(317, 625, 125, 36);
        _stop.Click += (_, _) => Execute(StopService);
        Controls.Add(_stop);
        _uninstall.Text = "Uninstall Collector";
        _uninstall.SetBounds(453, 625, 155, 36);
        _uninstall.Click += (_, _) => Execute(Uninstall);
        Controls.Add(_uninstall);
        var close = new Button { Text = "Close" };
        close.SetBounds(630, 625, 106, 36);
        close.Click += (_, _) => Close();
        Controls.Add(close);
        if (!new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator))
            Write("ERROR: Run this installer as Administrator. It should request UAC automatically.");
        if (File.Exists(ConfigPath)) LoadCurrentConfig();
    }

    private void AddLabel(string message, int x, int y, bool bold = false)
    {
        Controls.Add(new Label { Text = message, Location = new Point(x, y),
            Size = new Size(715, 24), Font = bold ? new Font(Font, FontStyle.Bold) : Font });
    }
    private void Write(string message)
    {
        _log.AppendText($"{DateTime.Now:HH:mm:ss} {message}\r\n");
        _log.SelectionStart = _log.Text.Length;
        _log.ScrollToCaret();
        Application.DoEvents();
    }
    private void LoadCurrentConfig()
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(ConfigPath));
            var root = doc.RootElement;
            if (root.TryGetProperty("allowed_client_cidrs", out var a))
                _networks.Text = string.Join(",", a.EnumerateArray().Select(v => v.GetString()));
            if (root.TryGetProperty("bind_port", out var p) && p.TryGetInt32(out int v) && v is >= 1 and <= 65535)
                _port.Value = v;
            Write("Existing configuration loaded. Database and CSV paths will be preserved.");
        }
        catch (Exception ex) { Write("Existing configuration unreadable: " + ex.Message); }
    }
    private void Execute(Action action)
    {
        _install.Enabled = _start.Enabled = _stop.Enabled = _uninstall.Enabled = _deleteData.Enabled = false;
        try { action(); }
        catch (Exception ex)
        {
            Write("ERROR: " + ex);
            MessageBox.Show(this, ex.Message, "Watcher Collector setup error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally { _install.Enabled = _start.Enabled = _stop.Enabled = _uninstall.Enabled = _deleteData.Enabled = true; }
    }
    private void Install()
    {
        if (!new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator))
            throw new UnauthorizedAccessException("Start the installer with administrator permissions.");
        string[] ranges = _networks.Text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        foreach (string range in ranges) ValidateCidr(range);
        Write("Stopping previous collector, if installed...");
        StopService();
        Directory.CreateDirectory(AppDir);
        Directory.CreateDirectory(DataDir);
        LimitDataPermissions();
        Write("Extracting embedded collector executable...");
        using (var input = Assembly.GetExecutingAssembly().GetManifestResourceStream("Watcher.Collector.Exe")
                          ?? throw new FileNotFoundException("Embedded WatcherCollector.exe missing. Use build\\build-windows.ps1."))
        using (var output = File.Create(Executable)) input.CopyTo(output);
        Write("No TLS certificates or keys will be created or loaded.");
        string dbPath = Path.Combine(DataDir, "watcher-events.db");
        string csvPath = @"C:\Watcher\Feeds\Connections";
        if (File.Exists(ConfigPath))
        {
            using var prior = JsonDocument.Parse(File.ReadAllText(ConfigPath));
            if (prior.RootElement.TryGetProperty("database_path", out var d) && d.ValueKind == JsonValueKind.String)
                dbPath = d.GetString() ?? dbPath;
            if (prior.RootElement.TryGetProperty("csv_dir", out var c) && c.ValueKind == JsonValueKind.String)
                csvPath = c.GetString() ?? csvPath;
            File.Copy(ConfigPath, ConfigPath + ".bak", overwrite: true);
        }
        else if (File.Exists(@"C:\Watcher\Collector\watcher-events.db"))
        {
            dbPath = @"C:\Watcher\Collector\watcher-events.db";
            Write("Detected existing Python collector SQLite database. Reusing it.");
        }
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        Directory.CreateDirectory(csvPath);
        var settings = new
        {
            bind_host = "0.0.0.0", bind_port = (int)_port.Value,
            database_path = dbPath, csv_dir = csvPath,
            log_file = Path.Combine(DataDir, "collector.log"),
            csv_export_seconds = 10, max_clients = 64,
            allowed_client_cidrs = ranges
        };
        File.WriteAllText(ConfigPath, JsonSerializer.Serialize(settings,
            new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
        Write("Checking collector configuration and SQLite access...");
        Run(Executable, new[] { "--config", ConfigPath, "--check-config" }, allowFailure: false);
        string commandLine = $"\"{Executable}\" --config \"{ConfigPath}\"";
        if (ServiceExists())
            Sc("config", ServiceName, "binPath=", commandLine, "start=", "auto");
        else
            Sc("create", ServiceName, "binPath=", commandLine, "start=", "auto", "DisplayName=", "Watcher Collector SIEM");
        Sc("description", ServiceName, "Watcher SIEM plain TCP collector; no certificates/tokens; JSONL/ACK to SQLite and CSV");
        Write("Creating inbound firewall rule for Domain/Private profiles...");
        // Remove old TLS-mode firewall rule for this port to avoid stale unrestricted configuration.
        string oldRule = "Watcher Collector TLS " + _port.Value;
        string safeOldRule = oldRule.Replace("'", "''");
        Run("powershell.exe", new[] { "-NoProfile", "-NonInteractive", "-Command",
            $"Get-NetFirewallRule -DisplayName '{safeOldRule}' -ErrorAction SilentlyContinue | Remove-NetFirewallRule" });
        string ruleName = "Watcher Collector TCP " + _port.Value;
        string safeName = ruleName.Replace("'", "''");
        string cidrs = ranges.Length == 0 ? "Any" : string.Join(",", ranges);
        string ps = $"Get-NetFirewallRule -DisplayName '{safeName}' -ErrorAction SilentlyContinue | Remove-NetFirewallRule; " +
            $"New-NetFirewallRule -DisplayName '{safeName}' -Direction Inbound -Protocol TCP " +
            $"-LocalPort {_port.Value} -RemoteAddress {cidrs} -Profile Domain,Private -Action Allow | Out-Null";
        Run("powershell.exe", new[] { "-NoProfile", "-NonInteractive", "-Command", ps });
        Write("Starting Windows service...");
        StartService();
        Write("SUCCESS. Executable: " + Executable);
        Write("CSV: " + csvPath);
        Write("Log: " + Path.Combine(DataDir, "collector.log"));
        MessageBox.Show(this, "Watcher Collector installed.\n\n" +
            "NO certificates or tokens. Install the matching PLAIN-TCP WatcherAgent build.\n" +
            "WARNING: Restrict access by firewall or management VLAN.",
            "Installation completed", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void Uninstall()
    {
        if (!new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator))
            throw new UnauthorizedAccessException("Run the installer as Administrator to uninstall the service.");
        bool deleteData = _deleteData.Checked;
        string warning = "Uninstall Watcher Collector?\n\n" +
            "This removes the Windows service, installed executable and Watcher TCP/TLS firewall rules.\n" +
            "This does not affect Watcher Agents on other computers.\n\n" +
            (deleteData
                ? "WARNING: ProgramData collector.json, local SQLite database and collector.log will be PERMANENTLY DELETED.\n"
                : "ProgramData configuration, SQLite database and logs will be PRESERVED.\n") +
            "Exported CSV history will always be preserved.\n\nContinue?";
        if (MessageBox.Show(this, warning, "Confirm Watcher Collector uninstall",
            MessageBoxButtons.YesNo, MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;

        if (deleteData)
        {
            // Separate destructive confirmation, never enabled by default.
            if (MessageBox.Show(this,
                "Delete the Watcher Collector files and any local SQLite database under:\n\n" +
                DataDir + "\n\nThis cannot be undone. Have you backed up the records?",
                "Permanently delete collector data?", MessageBoxButtons.YesNo,
                MessageBoxIcon.Stop, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
        }

        Write("Uninstalling Watcher Collector...");
        if (ServiceExists())
        {
            StopService();
            Sc("delete", ServiceName);
            Write("Windows service removed (or marked for deletion by Windows).");
        }
        else Write("Service already absent.");

        // Remove only firewall rules with the exact Watcher V4/V5 naming convention;
        // never remove arbitrary rules based on port or any other application's name.
        string removeRules = "$ErrorActionPreference='Stop'; " +
            "Get-NetFirewallRule -ErrorAction Stop | " +
            "Where-Object { $_.DisplayName -match '^Watcher Collector (TCP|TLS) [0-9]{1,5}$' } | " +
            "Remove-NetFirewallRule -ErrorAction Stop";
        Run("powershell.exe", new[] { "-NoProfile", "-NonInteractive", "-Command", removeRules });
        Write("Removed matching Watcher Collector firewall rules.");

        // Give SCM time to release its handle to the executable.
        if (Directory.Exists(AppDir))
        {
            bool removed = false;
            for (int attempt = 0; attempt < 20; attempt++)
            {
                try { Directory.Delete(AppDir, recursive: true); removed = true; break; }
                catch (IOException) when (attempt < 19) { Thread.Sleep(350); }
                catch (UnauthorizedAccessException) when (attempt < 19) { Thread.Sleep(350); }
            }
            if (!removed) throw new IOException("Unable to remove " + AppDir + ". Close Services console and retry, or restart Windows.");
            Write("Removed program files: " + AppDir);
        }
        else Write("Program files already absent.");

        if (deleteData)
        {
            if (Directory.Exists(DataDir))
            {
                Directory.Delete(DataDir, recursive: true);
                Write("Deleted ProgramData configuration, database and logs: " + DataDir);
            }
        }
        else Write("Preserved collector configuration, database and logs: " + DataDir);
        Write("All CSV exports preserved (including any custom export directory).");
        Write("UNINSTALL COMPLETED.");
        MessageBox.Show(this,
            "Watcher Collector has been uninstalled.\n\n" +
            (deleteData ? "Collector ProgramData was deleted.\n" : "Collector ProgramData was preserved.\n") +
            "CSV exports and any externally configured SQLite database were preserved.\n" +
            "No Watcher Agent installations were changed.",
            "Watcher Collector Uninstalled", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private static void ValidateCidr(string entry)
    {
        var p = entry.Split('/');
        if (p.Length != 2 || !IPAddress.TryParse(p[0], out IPAddress? address) ||
            !int.TryParse(p[1], out int bits)) throw new ArgumentException("Invalid CIDR: " + entry);
        if (bits <= 0 || bits > address.GetAddressBytes().Length * 8)
            throw new ArgumentException("CIDR must not be 0/0 and must fit its IP version: " + entry);
    }

    private static void LimitDataPermissions()
    {
        // Keep logs and database writable only to LocalSystem and Administrators.
        Run("icacls.exe", new[] { DataDir, "/inheritance:r", "/grant:r",
            "*S-1-5-18:(OI)(CI)F", "*S-1-5-32-544:(OI)(CI)F" });
    }

    private static bool ServiceExists()
    {
        try { Run("sc.exe", new[] { "query", ServiceName }, allowFailure: false); return true; }
        catch { return false; }
    }
    private static void Sc(params string[] args) => Run("sc.exe", args);
    private void StopService()
    {
        if (!ServiceExists()) { Write("No existing WatcherCollector service."); return; }
        string response = Run("sc.exe", new[] { "query", ServiceName }, allowFailure: true);
        if (response.Contains("STOPPED", StringComparison.OrdinalIgnoreCase))
        {
            Write("Service already stopped."); return;
        }
        Run("sc.exe", new[] { "stop", ServiceName }, allowFailure: true);
        for (int i = 0; i < 30; i++)
        {
            Thread.Sleep(500);
            if (Run("sc.exe", new[] { "query", ServiceName }, allowFailure: true)
                .Contains("STOPPED", StringComparison.OrdinalIgnoreCase))
            { Write("Service stopped."); return; }
        }
        throw new TimeoutException("Service failed to stop; refusing to overwrite an active executable.");
    }
    private void StartService()
    {
        if (!ServiceExists()) throw new InvalidOperationException("WatcherCollector service isn't installed.");
        string response = Run("sc.exe", new[] { "query", ServiceName });
        if (response.Contains("RUNNING", StringComparison.OrdinalIgnoreCase))
        { Write("Service is already running."); return; }
        Write(Run("sc.exe", new[] { "start", ServiceName }));
        for (int i = 0; i < 25; i++)
        {
            Thread.Sleep(400);
            if (Run("sc.exe", new[] { "query", ServiceName }, allowFailure: true)
                .Contains("RUNNING", StringComparison.OrdinalIgnoreCase))
            { Write("Service is running."); return; }
        }
        throw new InvalidOperationException("Service did not enter RUNNING state. Check " + Path.Combine(DataDir, "collector.log"));
    }

    private static string Run(string executable, IEnumerable<string> args, bool allowFailure = false)
    {
        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var a in args) info.ArgumentList.Add(a);
        using var process = Process.Start(info) ?? throw new IOException("Could not execute " + executable);
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(30000))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException(executable + " did not finish within 30 seconds");
        }
        Task.WaitAll(stdout, stderr);
        string result = stdout.Result + stderr.Result;
        if (process.ExitCode != 0 && !allowFailure)
            throw new IOException($"{executable} exited {process.ExitCode}: {result.Trim()}");
        return result;
    }
}
