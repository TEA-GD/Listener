"""Independent profiles, event-time novelty, replay, GUI and legacy direction handling."""
import hashlib,json,os,pathlib,shutil,socket,sqlite3,subprocess,tempfile,time,unittest,urllib.request,urllib.error,uuid
ROOT=pathlib.Path(__file__).resolve().parents[1]
DOTNET=os.environ.get('DOTNET_HOST_PATH') or shutil.which('dotnet')
ANALYZER=ROOT/'Listener_ConnectionAnalyzer/bin/Release/net10.0/Listener_ConnectionAnalyzer.dll'
GUI=ROOT/'Listener_DB_GUI/bin/Release/net10.0/Listener_DB_GUI.dll'
COLUMNS='EVENT_ID EVENT_TYPE TIMESTAMP_UTC DATE_AND_TIME SOURCE_IP SOURCE_DOMAIN_NAME PROCESS_NAME PROCESS_ID RECIEVER_IP RECIEVER_DOMAINNAME PORT_USED SOFTWARE_THAT_USED SOURCE_PORT PROTOCOL STATE OPERATING_SYSTEM MONITORED_HOST SNAPSHOT_ID COLLECTOR_PEER_IP CONNECTION_DIRECTION DIRECTION_BASIS DEVICE_CATEGORY host date_utc received_utc'.split()
class ConnectionAnalysisTests(unittest.TestCase):
 def setUp(self):
  self.temp=tempfile.TemporaryDirectory();self.root=pathlib.Path(self.temp.name);self.source=self.root/'events.db';self.state=self.root/'analysis.db';self.cfg=self.root/'config.json'
  with sqlite3.connect(self.source) as c:c.execute('CREATE TABLE events('+','.join('['+f+"] TEXT NOT NULL DEFAULT ''" for f in COLUMNS)+')')
  self.cfg.write_text(json.dumps({'database_path':str(self.source),'analysis_database':str(self.state),'batch_size':2,'poll_seconds':1}))
 def tearDown(self):self.temp.cleanup()
 def add(self,ip,stamp='2026-11-20T10:00:00Z',direction='outgoing',port='443',local_port='50000',host='PC-01',process='app',kind='TCP_CONNECTED',protocol='TCP'):
  row=dict.fromkeys(COLUMNS,'');row.update(EVENT_ID=str(uuid.uuid4()),EVENT_TYPE=kind,TIMESTAMP_UTC=stamp,SOURCE_IP='192.0.2.5',SOURCE_PORT=local_port,RECIEVER_IP=ip,PORT_USED=port,PROTOCOL=protocol,MONITORED_HOST=host,PROCESS_NAME=process,SOFTWARE_THAT_USED='/app',CONNECTION_DIRECTION=direction,DIRECTION_BASIS={'incoming':'local_listener','outgoing':'no_local_listener'}.get(direction,''),DEVICE_CATEGORY='user_pcs',host=host,date_utc=stamp[:10],received_utc='2026-11-24T06:00:00Z')
  with sqlite3.connect(self.source) as c:c.execute('INSERT INTO events VALUES('+','.join('?' for _ in COLUMNS)+')',[row[f] for f in COLUMNS])
 def run_analyzer(self,ok=True):
  result=subprocess.run([DOTNET,str(ANALYZER),'--config',str(self.cfg),'--once'],capture_output=True,text=True,timeout=25)
  self.assertEqual(0 if ok else 1,result.returncode,result.stdout+result.stderr);return result
 def start_gui(self,ready_path='/api/analysis/devices'):
  with socket.socket() as s:s.bind(('127.0.0.1',0));port=s.getsockname()[1]
  self.url='http://127.0.0.1:'+str(port);self.log=(self.root/'gui.log').open('w+')
  self.gui=subprocess.Popen([DOTNET,str(GUI),'--urls',self.url],env={**os.environ,'WATCHER_DB_PATH':str(self.source),'WATCHER_ANALYSIS_DB_PATH':str(self.state)},stdout=self.log,stderr=self.log)
  for _ in range(100):
   try:self.get(ready_path);return
   except OSError:time.sleep(.05)
  self.log.seek(0);self.fail(self.log.read())
 def stop_gui(self):
  self.gui.terminate();self.gui.wait(timeout=10);self.log.close()
 def get(self,path):
  with urllib.request.urlopen(self.url+path) as r:return json.load(r)
 def test_history_late_replay_and_browser(self):
  self.add('203.0.113.1','2026-11-18T10:00:00Z');self.add('203.0.113.1',local_port='50001')
  self.add('203.0.113.2',direction='incoming',port='50002',local_port='443')
  self.add('203.0.113.3','2026-11-18T10:00:00Z',direction='incoming',port='50003',local_port='443');self.add('203.0.113.3')
  self.add('203.0.113.4',kind='TCP_SNAPSHOT',process='<img src=x onerror=alert(1)>')
  self.add('203.0.113.5','2026-11-18T10:00:00Z',direction='');self.add('203.0.113.5')
  self.add('',direction='',protocol='UDP',kind='UDP_ENDPOINT_OPENED',port='')
  self.add('203.0.113.1',host='PC-02')
  self.add('203.0.113.6','2026-11-19T22:30:00Z') # Nov 20 in Moscow, Nov 19 in UTC.
  before=hashlib.sha256(self.source.read_bytes()).hexdigest();self.run_analyzer()
  self.assertEqual(before,hashlib.sha256(self.source.read_bytes()).hexdigest(),'Analyzer wrote to the main database')
  with sqlite3.connect(self.state) as c:
   rows=c.execute('SELECT host,peer_ip,direction,observations FROM analysis_connections').fetchall();self.assertEqual(9,len(rows));self.assertIn(('PC-01','203.0.113.1','outgoing',2),rows)
  self.run_analyzer()
  with sqlite3.connect(self.state) as c:self.assertEqual(10,c.execute('SELECT SUM(observations) FROM analysis_connections').fetchone()[0])
  self.start_gui()
  try:
   base='/api/analysis?host=PC-01&day=2026-11-20&tz=UTC&direction=outgoing'
   data=self.get(base+'&novelty=connections');self.assertEqual(['203.0.113.4'],[r['peer_ip'] for r in data['rows']]);self.assertFalse(data['status']['catching_up'])
   data=self.get(base+'&novelty=peers');self.assertEqual(['203.0.113.4'],[r['peer_ip'] for r in data['rows']])
   incoming=self.get(base.replace('outgoing','incoming')+'&novelty=connections');self.assertEqual('203.0.113.2',incoming['rows'][0]['peer_ip']);self.assertEqual('443',incoming['rows'][0]['service_port'])
   self.assertEqual(1,len(self.get(base.replace('outgoing','unknown'))['rows']))
   moscow=self.get(base.replace('tz=UTC','tz=Europe%2FMoscow')+'&novelty=peers');self.assertEqual({'203.0.113.4','203.0.113.6'},{r['peer_ip'] for r in moscow['rows']})
   pc2=self.get(base.replace('PC-01','PC-02')+'&novelty=peers');self.assertEqual(1,pc2['total'],'Novelty leaked between devices')
   for invalid in ['&page=0','&direction=invalid','&tz=not-a-zone','&day=bad']:
    with self.assertRaises(urllib.error.HTTPError) as e:self.get('/api/analysis?host=PC-01'+invalid)
    self.assertEqual(400,e.exception.code)
   from playwright.sync_api import sync_playwright,expect
   with sync_playwright() as p:
    browser=p.chromium.launch(executable_path=shutil.which('chromium'),args=['--no-sandbox']);page=browser.new_page();errors=[];page.on('pageerror',lambda e:errors.append(str(e)))
    page.goto(self.url+'/analyze.html?host=PC-01&day=2026-11-20&tz=UTC');expect(page.locator('#rows')).to_contain_text('203.0.113.4');self.assertEqual(0,page.locator('#rows img').count())
    page.get_by_role('button',name='Incoming',exact=True).click();expect(page.locator('#rows')).to_contain_text('203.0.113.2');expect(page.locator('#rows')).to_contain_text('Estimated')
    page.locator('#novelty').select_option('peers');page.get_by_role('button',name='Apply',exact=True).click();expect(page.locator('#rows')).not_to_contain_text('203.0.113.3')
    page.get_by_role('button',name='Unknown',exact=True).click();expect(page.locator('#empty')).to_be_visible();self.assertEqual([],errors)
    if os.environ.get('ANALYSIS_SCREENSHOT_PATH'):
     page.get_by_role('button',name='Outgoing',exact=True).click();page.locator('#novelty').select_option('');page.get_by_role('button',name='Apply',exact=True).click();expect(page.locator('#rows')).to_contain_text('203.0.113.4');page.set_viewport_size({'width':1440,'height':1000});page.screenshot(path=os.environ['ANALYSIS_SCREENSHOT_PATH'],full_page=True)
    page.set_viewport_size({'width':390,'height':844});page.screenshot(path=str(self.root/'analysis-mobile.png'),full_page=True)
    browser.close()
   # A historical observation delivered later corrects first-seen time, without
   # resetting the cursor or pretending an old peer is new on its receipt date.
   self.add('203.0.113.4','2026-11-17T10:00:00Z',process='<img src=x onerror=alert(1)>');self.run_analyzer()
   self.assertEqual(0,self.get(base+'&novelty=peers')['total'])
  finally:self.stop_gui()
 def test_old_schema_and_source_guard(self):
  self.add('203.0.113.8')
  with sqlite3.connect(self.source) as c:
   c.execute('ALTER TABLE events DROP COLUMN CONNECTION_DIRECTION');c.execute('ALTER TABLE events DROP COLUMN DIRECTION_BASIS')
  self.run_analyzer()
  with sqlite3.connect(self.state) as c:self.assertEqual('unknown',c.execute('SELECT direction FROM analysis_connections').fetchone()[0])
  with sqlite3.connect(self.source) as c:c.execute('UPDATE events SET EVENT_ID=?',(str(uuid.uuid4()),))
  self.assertIn('cursor changed',self.run_analyzer(False).stderr)
  self.cfg.write_text(json.dumps({'database_path':str(self.source),'analysis_database':str(self.source)}));self.assertIn('separate',self.run_analyzer(False).stderr)
  alias=self.root/'source-alias.db';os.link(self.source,alias);before=self.source.read_bytes();self.cfg.write_text(json.dumps({'database_path':str(self.source),'analysis_database':str(alias)}));self.assertIn('Collector database',self.run_analyzer(False).stderr);self.assertEqual(before,self.source.read_bytes())
 def test_registry_gaps_and_missing_analysis_setup(self):
  self.add('203.0.113.1')
  self.start_gui('/api/pcs')
  try:
   with self.assertRaises(urllib.error.HTTPError) as error:self.get('/api/analysis')
   self.assertEqual(503,error.exception.code);self.assertIn('Listener_ConnectionAnalyzer',error.exception.read().decode());self.assertFalse(self.state.exists())
   self.assertEqual(1,len(self.get('/api/pcs')))
  finally:self.stop_gui()
  with sqlite3.connect(self.source) as c:
   c.execute('CREATE TABLE event_registry(sequence INTEGER PRIMARY KEY)');c.execute('INSERT INTO event_registry VALUES(1000)')
  self.run_analyzer()
  self.start_gui()
  try:
   data=self.get('/api/analysis');self.assertFalse(data['status']['catching_up']);self.assertEqual('1000',data['status']['source_max_rowid']);self.assertEqual(1,data['total'])
  finally:self.stop_gui()
 def test_continuous_worker(self):
  self.add('203.0.113.1')
  with (self.root/'worker.log').open('w+') as log:
   worker=subprocess.Popen([DOTNET,str(ANALYZER),'--config',str(self.cfg)],stdout=log,stderr=log)
   try:
    def wait_count(n):
     for _ in range(120):
      try:
       with sqlite3.connect(self.state) as c:
        if c.execute('SELECT COUNT(*) FROM analysis_connections').fetchone()[0]==n:return
      except sqlite3.Error:pass
      if worker.poll() is not None:log.seek(0);self.fail(log.read())
      time.sleep(.05)
     self.fail('Independent analyzer did not catch up')
    wait_count(1);self.add('203.0.113.2');wait_count(2)
    worker.terminate();self.assertEqual(0,worker.wait(timeout=5))
   finally:
    if worker.poll() is None:worker.kill();worker.wait()
if __name__=='__main__':unittest.main(verbosity=2)
