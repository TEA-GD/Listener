"""Real Collector migration, partition delivery and existing GUI/Analyzer readers."""
from datetime import datetime, timedelta, timezone
import json, os, shutil, socket, sqlite3, subprocess, tempfile, time, unittest, urllib.request, uuid
from pathlib import Path
from test_dashboard import FIELDS, ROOT

class DeviceCategoryTests(unittest.TestCase):
    def test_migration_delivery_gui_and_analyzer_checkpoint(self):
        dotnet=os.environ.get('DOTNET_HOST_PATH') or shutil.which('dotnet')
        with tempfile.TemporaryDirectory(prefix='listener-categories-') as temp:
            root=Path(temp);database=root/'events.db';now=datetime.now(timezone.utc)
            def event(host,kind='HEARTBEAT',category=None):
                row=dict.fromkeys(FIELDS,'')
                row.update(EVENT_ID=str(uuid.uuid4()),MONITORED_HOST=host,EVENT_TYPE=kind,TIMESTAMP_UTC=(now-timedelta(seconds=10)).isoformat(),SNAPSHOT_ID='snapshot',STATE='SNAPSHOT_COMPLETE:0',COLLECTOR_PEER_IP='127.0.0.1')
                if category:row['DEVICE_CATEGORY']=category
                return row
            old=event('old-device')
            with sqlite3.connect(database) as db:
                db.execute('CREATE TABLE events('+','.join(f'[{f}] TEXT NOT NULL' for f in FIELDS)+',host TEXT,date_utc TEXT,received_utc TEXT)')
                db.execute('INSERT INTO events(rowid,'+','.join(FIELDS)+',host,date_utc,received_utc) VALUES('+','.join('?' for _ in range(len(FIELDS)+4))+')',[42]+[old[f] for f in FIELDS]+['old-device',old['TIMESTAMP_UTC'][:10],old['TIMESTAMP_UTC']])
            analyzer_config=root/'analyzer.json'
            analyzer_config.write_text(json.dumps({'database_path':str(database),'state_directory':str(root/'state'),'alerts_file':str(root/'alerts.jsonl')}))
            def analyze():
                result=subprocess.run([dotnet,str(ROOT/'Listener_Analyzer/bin/Release/net10.0/Listener_Analyzer.dll'),'--config',str(analyzer_config),'--once'],capture_output=True,text=True,timeout=40)
                self.assertEqual(0,result.returncode,result.stdout+result.stderr)
            analyze()
            def port():
                with socket.socket() as s:s.bind(('127.0.0.1',0));return s.getsockname()[1]
            collector_port=port();gui_port=port();config=root/'collector.json'
            config.write_text(json.dumps({'bind_host':'127.0.0.1','bind_port':collector_port,'database_path':str(database),'csv_dir':str(root/'csv'),'log_file':str(root/'collector.log'),'csv_export_seconds':1,'allowed_client_cidrs':[]}))
            processes=[]
            with (root/'processes.log').open('w+') as log:
                try:
                    processes.append(subprocess.Popen([dotnet,str(ROOT/'tests/ServerAgentCollector/bin/Release/net10.0/ServerAgentCollector.dll'),'--config',str(config)],stdout=log,stderr=log))
                    for _ in range(100):
                        try:
                            with socket.create_connection(('127.0.0.1',collector_port),timeout=.2):break
                        except OSError:time.sleep(.05)
                    else:log.seek(0);self.fail(log.read())
                    def send(row):
                        with socket.create_connection(('127.0.0.1',collector_port),timeout=5) as s:
                            s.sendall((json.dumps({'event':row})+'\n').encode())
                            self.assertEqual('ACK '+row['EVENT_ID']+'\n',s.makefile().readline())
                    categories=['linux_servers','windows_servers','user_pcs','project_stands']
                    for category in categories:send(event(category,category=category))
                    # A single host can change roles without splitting its history in readers.
                    send(event('old-device',category='project_stands'))
                    with sqlite3.connect(database) as db:
                        self.assertEqual((42,old['EVENT_ID']),db.execute('SELECT rowid,EVENT_ID FROM events_legacy').fetchone())
                        self.assertEqual(42,db.execute('SELECT rowid FROM events WHERE EVENT_ID=?',(old['EVENT_ID'],)).fetchone()[0])
                        self.assertEqual(6,db.execute('SELECT count(*) FROM events').fetchone()[0])
                        for category in categories:self.assertGreater(db.execute('SELECT count(*) FROM events_'+category).fetchone()[0],0)
                    analyze() # Must resume from the exact pre-migration anchor.
                    with sqlite3.connect(root/'state/profiles.db') as db:
                        self.assertGreater(int(db.execute("SELECT value FROM meta WHERE key='cursor'").fetchone()[0]),42)
                        self.assertEqual(5,db.execute('SELECT count(*) FROM devices').fetchone()[0])
                    url=f'http://127.0.0.1:{gui_port}'
                    processes.append(subprocess.Popen([dotnet,str(ROOT/'Listener_DB_GUI/bin/Release/net10.0/Listener_DB_GUI.dll'),'--urls',url],env={**os.environ,'WATCHER_DB_PATH':str(database)},stdout=log,stderr=log))
                    def get(path):
                        with urllib.request.urlopen(url+path,timeout=5) as response:return json.load(response)
                    for _ in range(100):
                        try:get('/api/pcs');break
                        except OSError:time.sleep(.05)
                    else:log.seek(0);self.fail(log.read())
                    def assert_summary():
                        pcs=get('/api/pcs')
                        with sqlite3.connect(database) as db:
                            raw=db.execute("SELECT MONITORED_HOST,count(*),max(TIMESTAMP_UTC),max(received_utc),count(DISTINCT NULLIF(PROCESS_NAME,'')),count(DISTINCT NULLIF(RECIEVER_IP,'')) FROM events GROUP BY MONITORED_HOST ORDER BY max(received_utc) DESC,MONITORED_HOST").fetchall()
                        self.assertEqual(raw,[(p['host'],p['events'],p['lastEvent'],p['lastReceived'],p['processes'],p['destinations']) for p in pcs])
                    assert_summary()
                    overview=get('/api/device-overview?host=old-device')
                    self.assertEqual('online',overview['availability']['status'])
                    capture=get('/api/last-minute?host=old-device');self.assertGreater(capture['watermark'],42)
                    self.assertEqual('fresh',capture['status'])
                    get('/api/availability?host=old-device')
                    # Cached sessions must invalidate for delayed heartbeats that bridge a gap.
                    def heartbeat(seconds):
                        row=event('old-device',category='project_stands');row['TIMESTAMP_UTC']=(now+timedelta(seconds=seconds)).isoformat();send(row);return row
                    heartbeat(-200)
                    before=get('/api/device-overview?host=old-device')['availability']
                    heartbeat(-110)
                    after=get('/api/device-overview?host=old-device')['availability']
                    self.assertNotEqual(before['online_since_utc'],after['online_since_utc'])
                    self.assertEqual(190,(datetime.fromisoformat(before['online_since_utc'])-datetime.fromisoformat(after['online_since_utc'])).total_seconds())
                    future=heartbeat(600)
                    self.assertEqual(after['last_heartbeat_utc'],get('/api/device-overview?host=old-device')['availability']['last_heartbeat_utc'])
                    # Cross-category dimensions are counted once, and replay has no effect.
                    row=event('old-device','TCP_CONNECTED','linux_servers');row.update(PROCESS_NAME='worker',RECIEVER_IP='203.0.113.1')
                    send(row);send(row);row['EVENT_ID']=str(uuid.uuid4());row['DEVICE_CATEGORY']='user_pcs';send(row)
                    assert_summary()
                    pcs=get('/api/pcs');self.assertIn('project_stands',json.dumps(pcs));self.assertIn('old-device',json.dumps(pcs))
                finally:
                    for process in reversed(processes):
                        process.terminate()
                        try:process.wait(timeout=10)
                        except subprocess.TimeoutExpired:process.kill();process.wait()

if __name__=='__main__':unittest.main()
