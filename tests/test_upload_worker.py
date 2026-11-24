"""Run the compiled Linux Agent with a stalled receiver; monitoring must continue."""
import json
import os
import pathlib
import shutil
import socket
import subprocess
import tempfile
import threading
import time
import unittest

ROOT = pathlib.Path(__file__).resolve().parents[1]
AGENT = ROOT / 'Agent/src/WatcherAgent/bin/Release/net10.0/linux-x64/WatcherAgent.dll'


class UploadWorkerTests(unittest.TestCase):
    def test_monitoring_survives_stalled_upload_and_second_instance_is_refused(self):
        dotnet = os.environ.get('DOTNET_HOST_PATH') or shutil.which('dotnet')
        self.assertTrue(dotnet and AGENT.exists(), 'Publish the Linux Agent first')
        with tempfile.TemporaryDirectory() as temp, socket.socket() as listener:
            root = pathlib.Path(temp)
            spool = root / 'spool'
            spool.mkdir()
            event_id = '11111111-2222-4333-8444-555555555555'
            payload = {'EVENT_ID': event_id, 'TIMESTAMP_UTC': '2026-11-01T00:00:00Z'}
            pending = spool / '0000.json'
            pending.write_text(json.dumps(payload))
            # Uploading must ignore unfinished writes even while scanning the spool.
            unfinished = spool / '0001.tmp'
            unfinished.write_text('unfinished')
            listener.bind(('127.0.0.1', 0))
            listener.listen()
            listener.settimeout(15)
            config = root / 'agent.json'
            config.write_text(json.dumps({
                'agent_id': 'Independent-PC', 'device_category': 'user_pcs',
                'collector_host': '127.0.0.1', 'collector_port': listener.getsockname()[1],
                'spool_dir': str(spool), 'collect_udp': False,
            }))
            command = [dotnet, str(AGENT), '--config', str(config)]
            received = threading.Event()
            disconnected = threading.Event()
            errors = []

            def hold_ack():
                try:
                    connection, _ = listener.accept()
                    with connection, connection.makefile('rb') as stream:
                        connection.settimeout(15)
                        first = json.loads(stream.readline())
                        self.assertEqual(payload, first['event'])
                        received.set()
                        # Never acknowledge this event: stopping the Agent must close
                        # this connection promptly and keep the original queue file.
                        self.assertEqual(b'', stream.readline())
                        disconnected.set()
                except BaseException as error:
                    errors.append(error)

            server = threading.Thread(target=hold_ack, daemon=True)
            server.start()
            with (root / 'agent.log').open('w+') as log:
                agent = subprocess.Popen(command, stdout=log, stderr=log)
                try:
                    self.assertTrue(received.wait(10), errors)
                    second = subprocess.run(command + ['--once'], capture_output=True, text=True, timeout=10)
                    self.assertNotEqual(0, second.returncode, 'Second process shared the upload queue')
                    # The old sequential loop could only log one collection before
                    # blocking on the unacknowledged event for ten seconds.
                    deadline = time.monotonic() + 8
                    while time.monotonic() < deadline:
                        log.flush()
                        text = (root / 'agent.log').read_text()
                        if text.count('sockets=') >= 2:
                            break
                        self.assertIsNone(agent.poll(), text)
                        time.sleep(.1)
                    else:
                        self.fail('Sampling stalled behind the upload:\n' + text)
                    self.assertEqual(payload, json.loads(pending.read_text()))
                    before_stop = time.monotonic()
                    agent.terminate()
                    self.assertEqual(0, agent.wait(timeout=6), text)
                    self.assertLess(time.monotonic() - before_stop, 6)
                    self.assertTrue(disconnected.wait(2), errors)
                    self.assertEqual(payload, json.loads(pending.read_text()))
                    self.assertTrue(unfinished.exists())
                finally:
                    if agent.poll() is None:
                        agent.kill()
                        agent.wait()
                    server.join(timeout=2)
            self.assertFalse(server.is_alive())
            self.assertEqual([], errors)


if __name__ == '__main__':
    unittest.main()
