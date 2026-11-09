"""Static regression checks for V6 Windows installer removal paths.
Does not replace compiling/running the solution on Windows.
"""
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[1]
AGENT = (ROOT / 'Agent/src/WatcherInstaller/Program.cs').read_text(encoding='utf-8')
COLLECTOR = (ROOT / 'Collector/src/WatcherCollectorInstaller/Program.cs').read_text(encoding='utf-8')

class UninstallSourceChecks(unittest.TestCase):
    def test_agent_has_button_and_confirmation(self):
        self.assertIn('_uninstall.Text = "Uninstall Agent"', AGENT)
        self.assertIn('_uninstall.Click += UninstallClicked;', AGENT)
        self.assertIn('MessageBoxDefaultButton.Button2', AGENT)
        self.assertIn('private static void Uninstall(bool deleteData', AGENT)

    def test_agent_stops_only_its_process_and_task(self):
        self.assertIn('"/Delete", "/TN", TaskName, "/F"', AGENT)
        self.assertIn('$_.ExecutablePath -ieq $target', AGENT)
        self.assertIn('Path.Combine(AppDir, "WatcherAgent.exe")', AGENT)
        self.assertIn('Directory.Delete(AppDir, recursive: true)', AGENT)

    def test_agent_preserves_spool_by_default(self):
        self.assertIn('_deleteData.Checked = false', AGENT)
        self.assertIn('if (deleteData)\n        {\n            if (Directory.Exists(DataDir))', AGENT)
        self.assertIn('buffered, possibly UNSENT events', AGENT)

    def test_collector_has_button_and_confirmation(self):
        self.assertIn('_uninstall.Text = "Uninstall Collector"', COLLECTOR)
        self.assertIn('Execute(Uninstall)', COLLECTOR)
        self.assertIn('MessageBoxDefaultButton.Button2', COLLECTOR)
        self.assertIn('Permanently delete collector data?', COLLECTOR)

    def test_collector_removes_service_and_specific_firewall(self):
        self.assertIn('Sc("delete", ServiceName);', COLLECTOR)
        self.assertIn('Watcher Collector (TCP|TLS)', COLLECTOR)
        self.assertIn('Remove-NetFirewallRule -ErrorAction Stop', COLLECTOR)
        self.assertIn('Directory.Delete(AppDir, recursive: true)', COLLECTOR)

    def test_collector_preserves_csv_and_db_by_default(self):
        self.assertIn('_deleteData.Checked = false', COLLECTOR)
        self.assertIn('All CSV exports preserved', COLLECTOR)
        self.assertNotIn('Directory.Delete(csvPath', COLLECTOR)
        self.assertNotIn('Directory.Delete(@"C:\\Watcher\\Feeds', COLLECTOR)

if __name__ == '__main__':
    unittest.main()
