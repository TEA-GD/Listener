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

internal sealed class AgentConfig
{
    public string agent_id { get; set; } = "";
    public string spool_dir { get; set; } = "";
}

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
    private readonly TextBox _log = new();
    private readonly Button _install = new();
    private readonly Button _uninstall = new();
    private readonly CheckBox _deleteData = new();

    public InstallerForm()
    {
        Text = "Watcher Connection Agent — Setup V6";
        ClientSize = new Size(670, 595);
        MinimumSize = new Size(670, 595);
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
            Text = $"Collector (compiled into agent): {CollectorSettings.Host}:{CollectorSettings.Port}",
            Location = new Point(20, 52), Size = new Size(620, 24)
        });
        Controls.Add(new Label {
            Text = "Computer identity (automatic hostname, not authenticated)",
            Location = new Point(20, 88), Size = new Size(620, 22)
        });
        _agentId.SetBounds(20, 110, 624, 28);
        _agentId.Text = DiscoverFqdn();
        Controls.Add(_agentId);

        Controls.Add(new Label {
            Text = "Plain TCP mode: no TLS, no certificates, no tokens. Restrict collector access.",
            Location = new Point(20, 160), Size = new Size(620, 44)
        });

        Controls.Add(new Label {
            Text = "Installation status and errors", Location = new Point(20, 226), Size = new Size(620, 22)
        });
        _log.SetBounds(20, 249, 624, 233);
        _log.Multiline = true;
        _log.ReadOnly = true;
        _log.ScrollBars = ScrollBars.Vertical;
        _log.Font = new Font("Consolas", 9F);
        _log.Text = "Ready. No keys or tokens. Collector must run the matching PLAIN TCP version and allow this PC IP.";
        Controls.Add(_log);

        _deleteData.Text = "Also delete local configuration and buffered unsent events (irreversible)";
        _deleteData.AutoSize = true;
        _deleteData.Location = new Point(20, 498);
        _deleteData.Checked = false; // Explicit opt-in; preserve evidence by default.
        Controls.Add(_deleteData);

        _install.Text = "Install / Update";
        _install.SetBounds(20, 537, 154, 36);
        _install.Click += InstallClicked;
        Controls.Add(_install);

        _uninstall.Text = "Uninstall Agent";
        _uninstall.SetBounds(190, 537, 156, 36);
        _uninstall.Click += UninstallClicked;
        Controls.Add(_uninstall);

        var close = new Button { Text = "Close", DialogResult = DialogResult.Cancel };
        close.SetBounds(553, 537, 91, 36);
        Controls.Add(close);
        CancelButton = close;
    }

    private async void InstallClicked(object? sender, EventArgs e)
    {
        string id = _agentId.Text.Trim();
        if (!Regex.IsMatch(id, @"^[A-Za-z0-9_.-]{1,120}$") || id is "." or "..")
        {
            MessageBox.Show(this, "Invalid Agent ID. Use letters, digits, periods, underscores or hyphens.",
                "Watcher Setup", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        _install.Enabled = false;
        _uninstall.Enabled = false;
        _deleteData.Enabled = false;
        _agentId.Enabled = false;
        Log("Installing agent for " + id + " ...");
        try
        {
            await Task.Run(() => Install(id, Log));
            Log("SUCCESS: Agent installed and startup task launched. No tokens are used.");
            MessageBox.Show(this,
                "Watcher Agent installed.\n\n" +
                "Windows task: " + TaskName + "\n" +
                "Collector: " + CollectorSettings.Host + ":" + CollectorSettings.Port + "\n\n" +
                "No token required. Verify the collector allows this machine IP and receives events.",
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
            _install.Enabled = true;
            _uninstall.Enabled = true;
            _deleteData.Enabled = true;
            _agentId.Enabled = true;
        }
    }

    private async void UninstallClicked(object? sender, EventArgs e)
    {
        bool deleteData = _deleteData.Checked;
        string warning = "This removes the Watcher Agent startup task and installed executable.\n\n" +
            (deleteData
                ? "WARNING: agent.json AND all locally buffered, possibly UNSENT events will be PERMANENTLY DELETED.\n\n"
                : "The local configuration and buffered events will be PRESERVED.\n\n") +
            "Proceed with uninstall?";
        if (MessageBox.Show(this, warning, "Confirm Watcher Agent uninstall",
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
            return;

        _install.Enabled = _uninstall.Enabled = _deleteData.Enabled = _agentId.Enabled = false;
        Log("Uninstalling agent..." + (deleteData ? " (including local data)" : " (preserving local data)"));
        try
        {
            await Task.Run(() => Uninstall(deleteData, Log));
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
            _install.Enabled = _uninstall.Enabled = _deleteData.Enabled = _agentId.Enabled = true;
        }
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

    private static void Install(string agentId, Action<string> log)
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
            throw new UnauthorizedAccessException("Administrator privileges are required. The installer manifest should trigger UAC.");

        using var embedded = Assembly.GetExecutingAssembly().GetManifestResourceStream("Watcher.Agent.Payload")
            ?? throw new IOException("The agent is not embedded in this installer. Build the WatcherInstaller project using build-windows.ps1; do not run the WatcherAgent executable directly.");

        log("Agent payload found. Preparing installation directories.");
        Directory.CreateDirectory(AppDir);
        Directory.CreateDirectory(DataDir);
        string spool = Path.Combine(DataDir, "spool");
        Directory.CreateDirectory(spool);

        // Protect configuration and spool (Administrator/SYSTEM only).
        Run("icacls.exe", DataDir, "/inheritance:r", "/grant:r",
            "*S-1-5-18:(OI)(CI)F", "*S-1-5-32-544:(OI)(CI)F", "/T");
        log("Protected ProgramData directory (Administrators and SYSTEM only).");

        string agentExe = Path.Combine(AppDir, "WatcherAgent.exe");
        // Stop the prior startup task before replacing an already-running agent.
        RunAllowFailure("schtasks.exe", "/End", "/TN", TaskName);
        bool copied = false;
        for (int i = 0; i < 15; i++)
        {
            try
            {
                embedded.Position = 0;
                using var output = new FileStream(agentExe, FileMode.Create, FileAccess.Write, FileShare.None);
                embedded.CopyTo(output);
                copied = true;
                break;
            }
            catch (IOException) when (i < 14)
            {
                Thread.Sleep(400);
            }
        }
        if (!copied) throw new IOException("Could not replace WatcherAgent.exe; verify the existing task/process is stopped.");
        log("Installed agent binary to " + agentExe);

        string config = Path.Combine(DataDir, "agent.json");
        var cfg = new AgentConfig { agent_id = agentId, spool_dir = spool };
        File.WriteAllText(config, JsonSerializer.Serialize(cfg, new JsonSerializerOptions { WriteIndented = true }),
            new UTF8Encoding(false));
        log("Wrote secured configuration: " + config);

        string taskCommand = $"\"{agentExe}\" --config \"{config}\"";
        Run("schtasks.exe", "/Create", "/TN", TaskName, "/SC", "ONSTART", "/RU", "SYSTEM",
            "/RL", "HIGHEST", "/TR", taskCommand, "/F");
        Run("schtasks.exe", "/Run", "/TN", TaskName);
        log("Created and started Windows task: " + TaskName);
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
