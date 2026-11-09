#!/usr/bin/env python3
"""Non-compiling structural/protocol checks. Does not replace .NET build or live smoke test."""
from pathlib import Path
import json
import re
import unittest

ROOT = Path(__file__).resolve().parents[1]

class Verify(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.collector = (ROOT/'Collector/src/WatcherCollector/Program.cs').read_text()
        cls.cinstaller = (ROOT/'Collector/src/WatcherCollectorInstaller/Program.cs').read_text()
        cls.agent = (ROOT/'Agent/src/WatcherAgent/Program.cs').read_text()
        cls.ainstaller = (ROOT/'Agent/src/WatcherInstaller/Program.cs').read_text()

    def test_no_tls_token_auth_in_sources(self):
        for source in [self.collector, self.cinstaller, self.agent, self.ainstaller]:
            for forbidden in ['SslStream','AuthenticateAsClient','AuthenticateAsServer','CreateTls(',
                              'X509CertificateLoader','X509KeyStorageFlags','tls_pfx_file', 'agent_token',
                              'ca_file', 'collector-ca.pem']:
                self.assertNotIn(forbidden, source)

    def test_no_tls_setup_config(self):
        conf = json.loads((ROOT/'Collector/src/WatcherCollector/collector.example.json').read_text())
        self.assertNotIn('tls_pfx_file', conf)
        self.assertEqual(6515, conf['bind_port'])
        self.assertEqual([], conf['allowed_client_cidrs'])

    def test_ack_protocol(self):
        self.assertIn('"ACK " + id + "\\n"', self.collector)
        self.assertIn('"ACK " + eventId', self.agent)
        self.assertIn('File.Delete(path);', self.agent)
        self.assertIn('transaction.Commit();', self.collector)
        self.assertLess(self.collector.find('transaction.Commit();'), self.collector.find('return eventId;'))
        self.assertIn('INSERT OR IGNORE INTO events', self.collector)
        self.assertIn('NetworkStream', 'NetworkStream')
        self.assertIn('tcp.GetStream()', self.agent)
        self.assertIn('client.GetStream()', self.collector)

    def test_csv_and_identity(self):
        for f in ['RECIEVER_IP','PORT_USED','PROCESS_NAME','MONITORED_HOST','COLLECTOR_PEER_IP']:
            self.assertIn(f, self.collector)
        self.assertIn('trusted["COLLECTOR_PEER_IP"] = peerIp', self.collector)
        self.assertIn('string.Join(";", CsvFields', self.collector)
        self.assertIn('Cidr.Parse', self.collector)

    def test_installer_removed_token_cert_prompts(self):
        self.assertNotIn('Browse...', self.ainstaller)
        self.assertNotIn('Regenerate TLS', self.cinstaller)
        self.assertIn('WatcherCollector', self.cinstaller)
        self.assertIn('Watcher-Connection-Agent', self.ainstaller)

if __name__ == '__main__':
    unittest.main()
