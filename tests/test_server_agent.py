"""PowerShell agent checks + real collector/SQLite/CSV delivery; Windows cmdlets are mocked."""
import json, os, pathlib, shutil, socket, sqlite3, subprocess, tempfile, threading, time, unittest
ROOT=pathlib.Path(__file__).resolve().parents[1]

class ServerAgent(unittest.TestCase):
    def test_delivery_and_offline_retry(self):
        pwsh=os.environ.get('PWSH_PATH') or shutil.which('pwsh')
        dotnet=os.environ.get('DOTNET_HOST_PATH') or shutil.which('dotnet')
        self.assertTrue(pwsh and dotnet,'Set PWSH_PATH and put .NET on PATH')
        with tempfile.TemporaryDirectory(prefix='watcher-server-test-') as directory:
            root=pathlib.Path(directory)
            env=dict(os.environ,XDG_CACHE_HOME=str(root/'cache'),XDG_CONFIG_HOME=str(root/'config'))
            with socket.socket() as s:s.bind(('127.0.0.1',0));port=s.getsockname()[1]
            config={'agent_id':'DC-01.example.local','collector_host':'localhost','collector_port':port,'spool_dir':str(root/'spool'),'log_file':str(root/'agent.log'),'poll_seconds':5,'heartbeat_seconds':60,'max_spool_events':50000,'send_batch_size':1000}
            path=root/'agent.json';path.write_text(json.dumps(config))
            def run(mode):
                return subprocess.run([pwsh,'-NoLogo','-NoProfile','-File',str(ROOT/'tests/Test-ServerAgent.ps1'),'-AgentScript',str(ROOT/'ServerAgent/Watcher-ServerAgent.ps1'),'-ConfigPath',str(path),'-Mode',mode],env=env,capture_output=True,text=True,timeout=40)
            result=run('Checks');self.assertEqual(0,result.returncode,result.stdout+result.stderr)
            for f in (root/'spool').glob('*.json'):f.unlink()
            result=run('Collect');self.assertEqual(1,result.returncode,result.stdout+result.stderr)
            files=list((root/'spool').glob('*.json'));self.assertEqual(2,len(files),result.stdout+result.stderr)
            events=[json.loads(f.read_text()) for f in files]
            self.assertEqual({'ACTIVE_AT_START','HEARTBEAT'},{r['EVENT_TYPE'] for r in events})
            active=next(r for r in events if r['EVENT_TYPE']=='ACTIVE_AT_START')
            self.assertEqual('server-process.exe',active['PROCESS_NAME'])
            collector={'bind_host':'127.0.0.1','bind_port':port,'database_path':str(root/'events.db'),'csv_dir':str(root/'csv'),'log_file':str(root/'collector.log'),'csv_export_seconds':1,'allowed_client_cidrs':[]}
            cfg=root/'collector.json';cfg.write_text(json.dumps(collector))
            with (root/'collector-output.log').open('w+') as log:
                process=subprocess.Popen([dotnet,str(ROOT/'tests/ServerAgentCollector/bin/Release/net10.0/ServerAgentCollector.dll'),'--config',str(cfg)],env=env,stdout=log,stderr=subprocess.STDOUT)
                try:
                    for _ in range(100):
                        try:
                            with socket.create_connection(('127.0.0.1',port),timeout=.2):break
                        except OSError:time.sleep(.05)
                    result=run('Send');self.assertEqual(0,result.returncode,result.stdout+result.stderr)
                    self.assertEqual([],list((root/'spool').glob('*.json')))
                    with sqlite3.connect(root/'events.db') as db:
                        self.assertEqual(2,db.execute('SELECT count(*) FROM events').fetchone()[0])
                        self.assertEqual(('DC-01.example.local','192.0.2.10','203.0.113.20','443','127.0.0.1'),db.execute("SELECT MONITORED_HOST,SOURCE_IP,RECIEVER_IP,PORT_USED,COLLECTOR_PEER_IP FROM events WHERE EVENT_TYPE='ACTIVE_AT_START'").fetchone())
                    csv=root/'csv/DC-01.example.local'/f"{events[0]['TIMESTAMP_UTC'][:10]}.csv"
                    for _ in range(100):
                        if csv.exists() and 'server-process.exe' in csv.read_text(encoding='utf-8-sig'):break
                        time.sleep(.05)
                    self.assertIn('server-process.exe',csv.read_text(encoding='utf-8-sig'))
                    # Replaying a previously acknowledged event is deduplicated by the real collector.
                    (root/'spool/replay.json').write_text(json.dumps(active))
                    result=run('Send');self.assertEqual(0,result.returncode,result.stdout+result.stderr)
                    with sqlite3.connect(root/'events.db') as db:self.assertEqual(2,db.execute('SELECT count(*) FROM events').fetchone()[0])
                    config['poll_seconds']=1;path.write_text(json.dumps(config))
                    loop=subprocess.Popen([pwsh,'-NoLogo','-NoProfile','-File',str(ROOT/'tests/Test-ServerAgent.ps1'),'-AgentScript',str(ROOT/'ServerAgent/Watcher-ServerAgent.ps1'),'-ConfigPath',str(path),'-Mode','Continuous'],env=env,stdout=subprocess.DEVNULL,stderr=subprocess.DEVNULL)
                    try:
                        disconnected=0
                        for _ in range(100):
                            with sqlite3.connect(root/'events.db') as db:
                                disconnected=db.execute("SELECT count(*) FROM events WHERE EVENT_TYPE='TCP_DISCONNECTED'").fetchone()[0]
                            if disconnected==2:break
                            time.sleep(.1)
                        self.assertEqual(2,disconnected,'Continuous polling did not detect both disconnections')
                        with sqlite3.connect(root/'events.db') as db:
                            self.assertEqual(('8443',),db.execute("SELECT PORT_USED FROM events WHERE EVENT_TYPE='TCP_CONNECTED'").fetchone())
                    finally:
                        loop.terminate()
                        try:loop.wait(timeout=5)
                        except subprocess.TimeoutExpired:loop.kill();loop.wait()
                    with socket.socket() as reject:
                        reject.bind(('127.0.0.1',0));reject.listen();reject.settimeout(10)
                        config['collector_port']=reject.getsockname()[1];path.write_text(json.dumps(config))
                        (root/'spool/wrong-ack.json').write_text(json.dumps(active))
                        before={f.name for f in (root/'spool').glob('*.json')}
                        def wrong_ack():
                            connection,_=reject.accept()
                            with connection:
                                connection.settimeout(10)
                                data=b''
                                while not data.endswith(b'\n'):data+=connection.recv(4096)
                                connection.sendall(b'ACK wrong-id\n')
                        thread=threading.Thread(target=wrong_ack,daemon=True);thread.start()
                        result=run('Send');thread.join(timeout=12)
                        self.assertNotEqual(0,result.returncode,'Wrong ACK must fail delivery')
                        self.assertEqual(before,{f.name for f in (root/'spool').glob('*.json')},'Wrong ACK must preserve pending files')


                finally:
                    process.terminate()
                    try:process.wait(timeout=10)
                    except subprocess.TimeoutExpired:process.kill();process.wait()

if __name__=='__main__':unittest.main(verbosity=2)
