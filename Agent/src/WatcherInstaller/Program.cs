using System.Diagnostics;
using System.Drawing;
using System.Net.NetworkInformation;
using System.Reflection;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using WatcherAgent;

namespace WatcherInstaller;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new InstallerForm());
    }
}

internal sealed class InstallerForm : Form
{
    private static readonly string AppDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Watcher", "Agent");
    private static readonly string DataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "WatcherAgent");
    private const string TaskName = "Watcher-Connection-Agent";

    private readonly TextBox _agentId = new();
    private readonly TextBox _collectorHost = new();
    private readonly NumericUpDown _collectorPort = new();
    private readonly TextBox _log = new();
    private readonly Button _install = new();
    private readonly Button _uninstall = new();
    private readonly Button _start = new();
    private readonly Button _stop = new();
    private readonly Button _close = new();
    private bool _canInstall = true;
    private bool _operationInProgress;
    private readonly CheckBox _deleteData = new();
    private readonly CheckBox _collectUdp = new();

    public InstallerForm()
    {
        Text = "Watcher Connection Agent — Setup V6";
        ClientSize = new Size(670, 635);
        MinimumSize = new Size(670, 635);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 9F);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;

        var headline = new Label {
            Text = "Install Watcher Connection Agent", Font = new Font(Font, FontStyle.Bold),
            AutoSize = true, Location = new Point(20, 18)
        };
        Controls.Add(headline);
        Controls.Add(new Label {
            Text = "Collector domain name (DNS hostname) and TCP port",
            Location = new Point(20, 52), Size = new Size(620, 24)
        });
        _collectorHost.SetBounds(20, 78, 460, 28);
        _collectorHost.Text = CollectorSettings.Host;
        _collectorHost.PlaceholderText = "collector.example.local";
        Controls.Add(_collectorHost);
        _collectorPort.SetBounds(494, 78, 150, 28);
        _collectorPort.Minimum = 1;
        _collectorPort.Maximum = 65535;
        _collectorPort.Value = CollectorSettings.Port;
        Controls.Add(_collectorPort);
        Controls.Add(new Label {
            Text = "Computer identity (automatic hostname, not authenticated)",
            Location = new Point(20, 116), Size = new Size(620, 22)
        });
        _agentId.SetBounds(20, 140, 624, 28);
        _agentId.Text = DiscoverFqdn();
        Controls.Add(_agentId);

        _collectUdp.Text = "Monitor UDP endpoints (remote peers may be unavailable)";
        _collectUdp.SetBounds(20, 180, 624, 24);
        _collectUdp.Checked = true;
        Controls.Add(_collectUdp);
        Controls.Add(new Label {
            Text = "Collector transport: plain TCP; no TLS, no certificates, no tokens. Restrict collector access.",
            Location = new Point(20, 212), Size = new Size(620, 44)
        });

        Controls.Add(new Label {
            Text = "Installation status and errors", Location = new Point(20, 264), Size = new Size(620, 22)
        });
        _log.SetBounds(20, 287, 624, 233);
        _log.Multiline = true;
        _log.ReadOnly = true;
        _log.ScrollBars = ScrollBars.Vertical;
        _log.Font = new Font("Consolas", 9F);
        _log.Text = "Ready. Upgrade the Collector first: this agent sends TCP_SNAPSHOT / UDP_SNAPSHOT records. Starting the task does not verify event delivery.";
        Controls.Add(_log);

        _deleteData.Text = "Also delete local configuration and buffered unsent events (irreversible)";
        _deleteData.AutoSize = true;
        _deleteData.Location = new Point(20, 536);
        _deleteData.Checked = false; // Explicit opt-in; preserve evidence by default.
        Controls.Add(_deleteData);

        _install.Text = "Install / Update";
        _install.SetBounds(20, 575, 140, 36);
        _install.Click += InstallClicked;
        Controls.Add(_install);

        _start.Text = "Launch Agent";
        _start.SetBounds(172, 575, 105, 36);
        _start.Click += (_, _) => ExecuteAgentControl(true);
        Controls.Add(_start);

        _stop.Text = "Stop Agent";
        _stop.SetBounds(289, 575, 100, 36);
        _stop.Click += (_, _) => ExecuteAgentControl(false);
        Controls.Add(_stop);

        _uninstall.Text = "Uninstall Agent";
        _uninstall.SetBounds(401, 575, 140, 36);
        _uninstall.Click += UninstallClicked;
        Controls.Add(_uninstall);

        _close.Text = "Close";
        _close.SetBounds(553, 575, 91, 36);
        _close.Click += (_, _) => Close();
        Controls.Add(_close);
        CancelButton = _close;
        FormClosing += (_, e) => { if (_operationInProgress) e.Cancel = true; };
        string config = Path.Combine(DataDir, "agent.json");
        try
        {
            string? existing=ReadExistingConfiguration(config,Log);
            if(existing is not null)
            {
                var saved=JsonSerializer.Deserialize<AgentConfig>(existing)
                    ??throw new InvalidDataException("Existing agent.json is empty or invalid.");
                saved.ValidateCollector();
                _collectorHost.Text=saved.collector_host;
                _collectorPort.Value=saved.collector_port;
                _agentId.Text=saved.agent_id;
                _collectUdp.Checked=saved.collect_udp;
            }
        }
        catch(Exception ex)
        {
            _canInstall=false;
            _install.Enabled=false;
            Log("Update blocked to preserve existing settings: "+ex.Message);
        }
    }

    private async void InstallClicked(object? sender, EventArgs e)
    {
        if (_operationInProgress) return;
        string id = _agentId.Text.Trim();
        if (!Regex.IsMatch(id, @"^[A-Za-z0-9_.-]{1,120}$") || id is "." or "..")
        {
            MessageBox.Show(this, "Invalid Agent ID. Use letters, digits, periods, underscores or hyphens.",
                "Watcher Setup", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        string collectorHost = _collectorHost.Text.Trim();
        int collectorPort = (int)_collectorPort.Value;
        bool collectUdp = _collectUdp.Checked;
        try { new AgentConfig { collector_host = collectorHost, collector_port = collectorPort }.ValidateCollector(); }
        catch (ArgumentException ex)
        {
            MessageBox.Show(this, ex.Message, "Watcher Setup", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        SetBusy(true);
        Log("Installing agent for " + id + " ...");
        try
        {
            await Task.Run(() => Install(id, collectorHost, collectorPort, collectUdp, Log));
            Log("SUCCESS: Agent installed and startup task launched. No tokens are used.");
            MessageBox.Show(this,
                "Watcher Agent installed.\n\n" +
                "Windows task: " + TaskName + "\n" +
                "Collector: " + collectorHost + ":" + collectorPort + "\n\n" +
                "No token required. Verify the collector receives events.",
                "Watcher Setup Complete", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            Log("FAILED: " + ex.Message);
            MessageBox.Show(this, ex.Message + "\n\nSee the setup window for details.",
                "Watcher Setup Failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void UninstallClicked(object? sender, EventArgs e)
    {
        if (_operationInProgress) return;
        bool deleteData = _deleteData.Checked;
        string warning = "This removes the Watcher Agent startup task and installed executable.\n\n" +
            (deleteData
                ? "WARNING: agent.json AND all locally buffered, possibly UNSENT events will be PERMANENTLY DELETED.\n\n"
                : "The local configuration and buffered events will be PRESERVED.\n\n") +
            "Proceed with uninstall?";
        if (MessageBox.Show(this, warning, "Confirm Watcher Agent uninstall",
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
            return;

        SetBusy(true);
        Log("Uninstalling agent..." + (deleteData ? " (including local data)" : " (preserving local data)"));
        try
        {
            await Task.Run(() => Uninstall(deleteData, Log));
            if (deleteData) _canInstall = true;
            Log("SUCCESS: Watcher Agent removed.");
            MessageBox.Show(this,
                "Watcher Agent has been uninstalled.\n\n" +
                (deleteData ? "Local configuration and unsent events were deleted."
                            : "Configuration and buffered events were preserved at:\n" + DataDir),
                "Watcher Agent Uninstalled", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            Log("UNINSTALL FAILED: " + ex);
            MessageBox.Show(this, ex.Message + "\n\nReview the installation log for details.",
                "Watcher Agent Uninstall Failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void SetBusy(bool busy)
    {
        _operationInProgress = busy;
        _install.Enabled = !busy && _canInstall;
        _start.Enabled = _stop.Enabled = _uninstall.Enabled = _close.Enabled = !busy;
        _deleteData.Enabled = _agentId.Enabled = _collectorHost.Enabled = _collectorPort.Enabled = _collectUdp.Enabled = !busy;
    }

    private async void ExecuteAgentControl(bool launch)
    {
        if (_operationInProgress) return;
        SetBusy(true);
        try
        {
            await Task.Run(() => {
                using var identity = WindowsIdentity.GetCurrent();
                if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
                    throw new UnauthorizedAccessException("Run the installer as administrator to control the installed agent.");
                var runtime = new InstalledAgentRuntime();
                if (launch) AgentLifecycle.Launch(runtime, Log);
                else AgentLifecycle.Stop(runtime, Log);
            });
        }
        catch (Exception ex)
        {
            Log((launch ? "LAUNCH FAILED: " : "STOP FAILED: ") + ex.Message);
            MessageBox.Show(this, ex.Message, "Watcher Agent control", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally { SetBusy(false); }
    }

    private sealed class InstalledAgentRuntime : IAgentRuntime
    {
        private readonly string _exe = Path.Combine(AppDir, "WatcherAgent.exe");
        public bool IsInstalled => File.Exists(_exe) && File.Exists(Path.Combine(DataDir, "agent.json"));
        public bool HasTask => TaskExists();
        public bool IsRunning
        {
            get
            {
                foreach (var process in Process.GetProcessesByName("WatcherAgent"))
                {
                    using (process)
                    {
                        try { if (string.Equals(process.MainModule?.FileName, _exe, StringComparison.OrdinalIgnoreCase)) return true; }
                        catch (InvalidOperationException) { /* Process exited during the query. */ }
                        catch (System.ComponentModel.Win32Exception ex) { throw new IOException("Cannot verify the WatcherAgent process state. Check administrator permissions and process access.", ex); }
                    }
                }
                return false;
            }
        }
        public void StartTask() => Run("schtasks.exe", "/Run", "/TN", TaskName);
        public void EndTask() => RunAllowFailure("schtasks.exe", "/End", "/TN", TaskName);
        public void StopInstalledProcess() => StopInstalledAgent(_exe);
        public void Pause(int milliseconds) => Thread.Sleep(milliseconds);
    }

    private static void Uninstall(bool deleteData, Action<string> log)
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
            throw new UnauthorizedAccessException("Administrator privileges required for uninstall.");

        if (TaskExists())
        {
            log("Stopping scheduled task: " + TaskName);
            RunAllowFailure("schtasks.exe", "/End", "/TN", TaskName);
            Run("schtasks.exe", "/Delete", "/TN", TaskName, "/F");
            log("Removed scheduled task.");
        }
        else log("Scheduled task not present.");

        // Only terminate processes whose executable path matches Watcher's installed agent.
        // Do not kill unrelated executables with the same process name.
        string installedExe = Path.Combine(AppDir, "WatcherAgent.exe");
        string safePath = installedExe.Replace("'", "''");
        string ps = "$ErrorActionPreference='Stop'; $target='" + safePath + "'; " +
            "Get-CimInstance Win32_Process -Filter \"Name = 'WatcherAgent.exe'\" | " +
            "Where-Object { $_.ExecutablePath -ieq $target } | " +
            "ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction Stop }";
        Run("powershell.exe", "-NoProfile", "-NonInteractive", "-Command", ps);
        log("Stopped installed agent process (if running).");

        if (Directory.Exists(AppDir))
        {
            bool removed = false;
            for (int attempt = 0; attempt < 20; attempt++)
            {
                try { Directory.Delete(AppDir, recursive: true); removed = true; break; }
                catch (IOException) when (attempt < 19) { Thread.Sleep(350); }
                catch (UnauthorizedAccessException) when (attempt < 19) { Thread.Sleep(350); }
            }
            if (!removed) throw new IOException("Unable to remove " + AppDir + ". Retry after the agent exits or restart Windows.");
            log("Deleted application directory: " + AppDir);
        }
        else log("Application directory already absent.");

        if (deleteData)
        {
            if (Directory.Exists(DataDir))
            {
                Directory.Delete(DataDir, recursive: true);
                log("Deleted local configuration and buffered events: " + DataDir);
            }
        }
        else log("Preserved local configuration and buffered events: " + DataDir);
    }

    private static bool TaskExists()
    {
        try { Run("schtasks.exe", "/Query", "/TN", TaskName); return true; }
        catch (IOException) { return false; }
    }

    private void Log(string message)
    {
        if (IsDisposed || !IsHandleCreated) return;
        if (InvokeRequired) { BeginInvoke(new Action<string>(Log), message); return; }
        _log.AppendText(Environment.NewLine + $"[{DateTime.Now:HH:mm:ss}] " + message);
    }

    private static void Install(string agentId, string collectorHost, int collectorPort, bool collectUdp, Action<string> log)
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
            throw new UnauthorizedAccessException("Administrator privileges are required. The installer manifest should trigger UAC.");

        using var embedded = Assembly.GetExecutingAssembly().GetManifestResourceStream("Watcher.Agent.Payload")
            ?? throw new IOException("The agent is not embedded in this installer. Build the WatcherInstaller project using build-windows.ps1; do not run the WatcherAgent executable directly.");

        log("Agent payload found. Repairing installation permissions.");
        Directory.CreateDirectory(AppDir);
        InstallationFiles.ProtectDirectory(DataDir,Run);
        string config=Path.Combine(DataDir,"agent.json");
        InstallationFiles.ProtectConfiguration(config,Run);
        string? existing=ReadExistingConfiguration(config,log);
        string defaultSpool=Path.Combine(DataDir,"spool");
        string newSettings=InstallationFiles.MergeConfiguration(existing,agentId,collectorHost,collectorPort,collectUdp,defaultSpool);
        var cfg=JsonSerializer.Deserialize<AgentConfig>(newSettings)!;
        // Preserve a configured custom spool; never enumerate/delete pending files during update.
        if(Path.GetFullPath(cfg.spool_dir).Equals(Path.GetFullPath(defaultSpool),StringComparison.OrdinalIgnoreCase))
            InstallationFiles.ProtectDirectory(defaultSpool,Run);
        else Directory.CreateDirectory(cfg.spool_dir);
        log("Preserved spool directory: "+cfg.spool_dir);

        string agentExe=Path.Combine(AppDir,"WatcherAgent.exe");
        bool previousTask=TaskExists(),installed=false;
        try
        {
            RunAllowFailure("schtasks.exe","/End","/TN",TaskName);
            StopInstalledAgent(agentExe);
            // Stage and flush complete files before replacement. A failed write leaves
            // the previous executable/config intact rather than truncating it.
            InstallationFiles.WriteAtomically(agentExe,embedded);
            log("Installed agent binary to "+agentExe);
            using var settingsStream=new MemoryStream(new UTF8Encoding(false).GetBytes(newSettings));
            InstallationFiles.WriteAtomically(config,settingsStream);
            log("Updated protected configuration: "+config);

            string taskCommand=$"\"{agentExe}\" --config \"{config}\"";
            Run("schtasks.exe","/Create","/TN",TaskName,"/SC","ONSTART","/RU","SYSTEM",
                "/RL","HIGHEST","/TR",taskCommand,"/F");
            Run("schtasks.exe","/Run","/TN",TaskName);
            installed=true;
            log("Created and started Windows task: "+TaskName);
        }
        finally
        {
            if(!installed&&previousTask)
            {
                log("Update did not finish. Preserved settings and queued events; attempting to restart the existing task.");
                RunAllowFailure("schtasks.exe","/Run","/TN",TaskName);
            }
        }
    }

    private static string? ReadExistingConfiguration(string config,Action<string> log)
    {
        try{return File.ReadAllText(config);}
        catch(FileNotFoundException){return null;}
        catch(DirectoryNotFoundException){return null;}
        catch(UnauthorizedAccessException)
        {
            log("Repairing access to existing agent.json before loading saved settings.");
            InstallationFiles.ProtectDirectory(DataDir,Run);
            InstallationFiles.ProtectConfiguration(config,Run);
            return File.ReadAllText(config);
        }
    }

    private static void StopInstalledAgent(string agentExe)
    {
        string safePath=agentExe.Replace("'","''");
        string ps="$ErrorActionPreference='Stop'; $target='"+safePath+"'; "+
            "Get-CimInstance Win32_Process -Filter \"Name = 'WatcherAgent.exe'\" | "+
            "Where-Object { $_.ExecutablePath -ieq $target } | "+
            "ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction Stop }";
        Run("powershell.exe","-NoProfile","-NonInteractive","-Command",ps);
    }

    private static void RunAllowFailure(string exe, params string[] args)
    {
        try { Run(exe, args); } catch (Exception) { /* Task might not exist on first install. */ }
    }

    private static void Run(string exe, params string[] args)
    {
        var info = new ProcessStartInfo(exe) {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (string arg in args) info.ArgumentList.Add(arg);
        using var process = Process.Start(info) ?? throw new IOException("Could not start " + exe);
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(30000)) {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException(exe + " timed out.");
        }
        Task.WaitAll(stdout, stderr);
        if (process.ExitCode != 0)
            throw new IOException($"{exe} failed (code {process.ExitCode}): {stderr.Result} {stdout.Result}");
    }

    private static string DiscoverFqdn()
    {
        string host = Environment.MachineName;
        string suffix = IPGlobalProperties.GetIPGlobalProperties().DomainName;
        return string.IsNullOrWhiteSpace(suffix) || host.EndsWith("." + suffix, StringComparison.OrdinalIgnoreCase)
            ? host : host + "." + suffix;
    }
}
