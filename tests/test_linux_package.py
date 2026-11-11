"""Build Agent/build/build-linux.sh first; tests extract the .deb without installing it."""
import concurrent.futures
import json
import os
from pathlib import Path
import socket
import subprocess
import tempfile
import unittest

ROOT=Path(__file__).resolve().parents[1]
PACKAGE=ROOT/'Agent/output/watcher-agent_1.0.1_amd64.deb'

class LinuxPackage(unittest.TestCase):
    def test_metadata_and_service_layout(self):
        with tempfile.TemporaryDirectory(prefix='watcher-package-') as temp:
            root=Path(temp)
            subprocess.run(['dpkg-deb','-R',str(PACKAGE),str(root/'extracted')],check=True,capture_output=True)
            tree=root/'extracted'
            self.assertEqual(0o755,(tree/'DEBIAN').stat().st_mode & 0o777)
            for relative in ['usr','usr/bin','lib','lib/systemd','lib/systemd/system']:
                self.assertEqual(0o755,(tree/relative).stat().st_mode & 0o777,relative)
            service=(tree/'lib/systemd/system/watcher-agent.service').read_text()
            self.assertIn('ExecStart=/usr/bin/watcher-agent --config /etc/watcher-agent/agent.json',service)
            self.assertTrue(os.access(tree/'usr/bin/watcher-agent',os.X_OK))
            for script in ['postinst','prerm','postrm']:
                subprocess.run(['sh','-n',str(tree/'DEBIAN'/script)],check=True)
                self.assertTrue(os.access(tree/'DEBIAN'/script,os.X_OK))

    def test_packaged_binary_delivers_heartbeat(self):
        with tempfile.TemporaryDirectory(prefix='watcher-package-smoke-') as temp:
            root=Path(temp)
            subprocess.run(['dpkg-deb','-x',str(PACKAGE),str(root/'extracted')],check=True)
            with socket.socket() as listener:
                listener.bind(('127.0.0.1',0));listener.listen();listener.settimeout(20)
                config=root/'agent.json'
                config.write_text(json.dumps({'agent_id':'linux-package-test','collector_host':'localhost',
                    'collector_port':listener.getsockname()[1],'spool_dir':str(root/'spool')}))
                received=[]
                def receive():
                    connection,_=listener.accept()
                    with connection:
                        connection.settimeout(20)
                        stream=connection.makefile('rb')
                        while True:
                            line=stream.readline()
                            if not line:break
                            event=json.loads(line)['event'];received.append(event)
                            connection.sendall(('ACK '+event['EVENT_ID']+'\n').encode())
                with concurrent.futures.ThreadPoolExecutor(1) as pool:
                    worker=pool.submit(receive)
                    env=dict(os.environ,DOTNET_BUNDLE_EXTRACT_BASE_DIR=str(root/'bundle'))
                    result=subprocess.run([str(root/'extracted/usr/bin/watcher-agent'),'--config',str(config),'--once'],
                        env=env,capture_output=True,text=True,timeout=30)
                    worker.result(timeout=25)
                self.assertEqual(0,result.returncode,result.stdout+result.stderr)
                self.assertIn('Acknowledged:',result.stdout)
                self.assertTrue(any(e['EVENT_TYPE']=='HEARTBEAT' and e['MONITORED_HOST']=='linux-package-test' for e in received))
                self.assertEqual([],list((root/'spool').glob('*.json')))

if __name__=='__main__':unittest.main(verbosity=2)
