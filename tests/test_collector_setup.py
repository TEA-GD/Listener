"""Automatic deployment: staging/update/removal, service plan, and real analyzer preflight."""
import argparse,importlib.util,json,os,pathlib,shutil,socket,sqlite3,subprocess,tempfile,time,unittest,uuid
from unittest.mock import patch
ROOT=pathlib.Path(__file__).resolve().parents[1]
SCRIPT=ROOT/'Collector/linux/install.py'
spec=importlib.util.spec_from_file_location('collector_install',SCRIPT);module=importlib.util.module_from_spec(spec);spec.loader.exec_module(module)
DOTNET=os.environ.get('DOTNET_HOST_PATH') or shutil.which('dotnet')

class CollectorSetup(unittest.TestCase):
 def setUp(self):
  self.temp=tempfile.TemporaryDirectory();self.root=pathlib.Path(self.temp.name);self.bundle=self.root/'bundle';self.stage=self.root/'staged'
  for name in ['WatcherCollector','analyzers/connection/Listener_ConnectionAnalyzer','analyzers/behavior/Listener_Analyzer']:
   path=self.bundle/name;path.parent.mkdir(parents=True,exist_ok=True);path.write_bytes(b'published binary')
  for name in ['install.py','install-linux.sh','uninstall-linux.sh']:shutil.copyfile(ROOT/'Collector/linux'/name,self.bundle/name)
 def tearDown(self):self.temp.cleanup()
 def setup(self,*args,ok=True,operation='install'):
  result=subprocess.run(['python3',str(SCRIPT),operation,'--bundle',str(self.bundle),'--stage-root',str(self.stage),*args],capture_output=True,text=True,timeout=20)
  self.assertEqual(0 if ok else 1,result.returncode,result.stdout+result.stderr);return result
 def test_default_update_disable_and_uninstall(self):
  self.setup('--port','16515','--allow-cidr','10.8.0.0/24')
  main=self.stage/'etc/watcher-collector/collector.json';conn=self.stage/'etc/watcher-connection-analyzer/config.json';behavior=self.stage/'etc/watcher-analyzer/config.json'
  settings=json.loads(main.read_text());self.assertEqual(16515,settings['bind_port']);self.assertEqual(['10.8.0.0/24'],settings['allowed_client_cidrs'])
  self.assertEqual(settings['database_path'],json.loads(conn.read_text())['database_path']);self.assertEqual(settings['database_path'],json.loads(behavior.read_text())['database_path'])
  self.assertIn('PartOf=watcher-collector.service',(self.stage/'etc/systemd/system/listener-analyzer.service').read_text())
  settings['max_clients']=7;settings['csv_export_seconds']=123;settings['custom']='preserve';main.write_text(json.dumps(settings))
  b=json.loads(behavior.read_text());b['baseline_days']=17;b['alerts_file']=str(self.root/'external/alerts.jsonl');behavior.write_text(json.dumps(b))
  state=self.stage/'var/lib/watcher-analyzer/profiles.db';state.write_bytes(b'existing state must survive updates')
  self.setup();self.assertEqual(settings,json.loads(main.read_text()));self.assertEqual(b,json.loads(behavior.read_text()));self.assertEqual(b'existing state must survive updates',state.read_bytes());self.assertTrue(main.with_name(main.name+'.bak').exists())
  malformed='invalid analyzer JSON';behavior.write_text(malformed);self.setup('--without-analyzers');self.assertEqual(malformed,behavior.read_text())
  self.assertFalse(json.loads((self.stage/'etc/watcher-collector/analyzers.json').read_text())['enabled']);self.assertNotIn('Wants=listener-',(self.stage/'etc/systemd/system/watcher-collector.service').read_text())
  self.setup('--with-analyzers',ok=False);self.assertEqual(malformed,behavior.read_text())
  self.setup(operation='uninstall');self.assertFalse((self.stage/'opt/watcher-collector').exists());self.assertTrue(main.exists() and conn.exists() and state.exists());self.assertFalse((self.stage/'etc/systemd/system/watcher-collector.service').exists())
  self.setup(operation='uninstall')
 def test_production_service_order_with_fake_host(self):
  args=argparse.Namespace(stage_root=str(self.stage),bundle=str(self.bundle),analyzers=True,port=None,bind_host=None,allow_cidr=None,no_start=False)
  deploy=module.Deployment(args);deploy.staged=False;calls=[]
  def run(*args,check=True):
   calls.append(tuple(map(str,args)));return subprocess.CompletedProcess(args,0,stdout='loaded\n' if '--property=LoadState' in args else '',stderr='')
  deploy.run=run;deploy.account=lambda:None;deploy.uid=os.getuid();deploy.gid=os.getgid()
  with patch.object(module.os,'chown'): deploy.install()
  stopped=[c for c in calls if c[:2]==('systemctl','stop')];self.assertEqual([('systemctl','stop',u) for u in reversed(module.UNITS)],stopped)
  checks=[c for c in calls if c[0]=='runuser'];self.assertEqual(3,len(checks));self.assertTrue(all(c[-1]=='--check-config' for c in checks))
  self.assertIn(('systemctl','enable',*module.UNITS),calls);self.assertIn(('systemctl','start',*module.UNITS),calls)
  self.assertLess(calls.index(checks[-1]),calls.index(('systemctl','start',*module.UNITS)))
 @unittest.skipUnless((ROOT/'Collector/output/linux-collector/WatcherCollector').exists(),'Build the Linux Collector bundle first')
 def test_native_bundle_generated_configs_and_preserved_state(self):
  self.bundle=ROOT/'Collector/output/linux-collector'
  with socket.socket() as reserve:reserve.bind(('127.0.0.1',0));port=reserve.getsockname()[1]
  self.setup('--port',str(port),'--bind-host','127.0.0.1')
  app=self.stage/'opt/watcher-collector';main_cfg=self.stage/'etc/watcher-collector/collector.json';connection_cfg=self.stage/'etc/watcher-connection-analyzer/config.json';behavior_cfg=self.stage/'etc/watcher-analyzer/config.json'
  cache=self.root/'bundle-cache';cache.mkdir();env={**os.environ,'DOTNET_BUNDLE_EXTRACT_BASE_DIR':str(cache)}
  commands=[(app/'WatcherCollector',main_cfg),(app/'analyzers/connection/Listener_ConnectionAnalyzer',connection_cfg),(app/'analyzers/behavior/Listener_Analyzer',behavior_cfg)]
  for binary,config in commands:
   result=subprocess.run([str(binary),'--config',str(config),'--check-config'],env=env,capture_output=True,text=True,timeout=30);self.assertEqual(0,result.returncode,result.stdout+result.stderr)
  with (self.root/'native-collector.log').open('w+') as log:
   collector=subprocess.Popen([str(commands[0][0]),'--config',str(main_cfg)],env=env,stdout=log,stderr=log)
   try:
    for _ in range(100):
     try:connection=socket.create_connection(('127.0.0.1',port),timeout=.5);break
     except OSError:time.sleep(.05)
    else:log.seek(0);self.fail(log.read())
    event={'MONITORED_HOST':'AUTO-PC','EVENT_ID':str(uuid.uuid4()),'EVENT_TYPE':'TCP_CONNECTED','TIMESTAMP_UTC':'2026-11-24T07:00:00Z','PROTOCOL':'TCP','SOURCE_IP':'192.0.2.10','SOURCE_PORT':'50001','RECIEVER_IP':'203.0.113.8','PORT_USED':'443','PROCESS_NAME':'native-app','CONNECTION_DIRECTION':'outgoing','DIRECTION_BASIS':'no_local_listener','DEVICE_CATEGORY':'user_pcs'}
    with connection,connection.makefile('rb') as stream:
     connection.sendall((json.dumps({'event':event})+'\n').encode());self.assertEqual('ACK '+event['EVENT_ID'],stream.readline().decode().strip())
    for binary,config in commands[1:]:
     saved_log=self.root/(binary.name+'.log');result=subprocess.run([str(binary),'--config',str(config),'--once','--log',str(saved_log)],env=env,capture_output=True,text=True,timeout=30);self.assertEqual(0,result.returncode,result.stdout+result.stderr);self.assertTrue(saved_log.stat().st_size>0)
   finally:collector.terminate();collector.wait(timeout=15)
  analysis=self.stage/'var/lib/watcher-connection-analyzer/connection-analysis.db';profiles=self.stage/'var/lib/watcher-analyzer/profiles.db'
  with sqlite3.connect(analysis) as c:self.assertEqual(('AUTO-PC','outgoing','203.0.113.8'),c.execute('SELECT host,direction,peer_ip FROM analysis_connections').fetchone())
  with sqlite3.connect(profiles) as c:self.assertEqual('AUTO-PC',c.execute('SELECT host FROM devices').fetchone()[0])
  before=(analysis.read_bytes(),profiles.read_bytes());self.setup();self.assertEqual(before,(analysis.read_bytes(),profiles.read_bytes()))
  self.assertIn(str(analysis),(self.stage/'etc/watcher-collector/gui.env').read_text())
  self.setup(operation='uninstall');self.assertEqual(before,(analysis.read_bytes(),profiles.read_bytes()))
 def test_analyzer_check_config_and_persistent_failures(self):
  main=self.root/'source.db'
  fields='EVENT_ID EVENT_TYPE TIMESTAMP_UTC DATE_AND_TIME SOURCE_IP SOURCE_DOMAIN_NAME PROCESS_NAME PROCESS_ID RECIEVER_IP RECIEVER_DOMAINNAME PORT_USED SOFTWARE_THAT_USED SOURCE_PORT PROTOCOL STATE OPERATING_SYSTEM MONITORED_HOST SNAPSHOT_ID COLLECTOR_PEER_IP'.split()
  with sqlite3.connect(main) as c:
   c.execute('CREATE TABLE events('+','.join(f+' TEXT' for f in fields)+')')
   row=dict.fromkeys(fields,'');row.update(EVENT_ID=str(uuid.uuid4()),EVENT_TYPE='TCP_CONNECTED',TIMESTAMP_UTC='2026-11-24T07:00:00Z',MONITORED_HOST='SETUP-PC',RECIEVER_IP='203.0.113.8',PORT_USED='443',PROTOCOL='TCP');c.execute('INSERT INTO events VALUES('+','.join('?' for _ in fields)+')',[row[f] for f in fields])
  for project in ['Listener_Analyzer','Listener_ConnectionAnalyzer']:
   config=self.root/(project+'.json');state=self.root/project
   value={'database_path':str(main),'state_directory':str(state),'alerts_file':str(state/'alerts.jsonl')} if project=='Listener_Analyzer' else {'database_path':str(main),'analysis_database':str(state/'analysis.db')}
   config.write_text(json.dumps(value));dll=ROOT/project/'bin/Release/net10.0'/(project+'.dll')
   result=subprocess.run([DOTNET,str(dll),'--config',str(config),'--check-config'],capture_output=True,text=True,timeout=20);self.assertEqual(0,result.returncode,result.stdout+result.stderr)
   db=state/('profiles.db' if project=='Listener_Analyzer' else 'analysis.db');table='devices' if project=='Listener_Analyzer' else 'analysis_connections'
   with sqlite3.connect(db) as c:self.assertEqual(0,c.execute('SELECT count(*) FROM '+table).fetchone()[0],'Preflight processed history')
   self.assertFalse((state/'alerts.jsonl').exists())
   config.write_text('bad JSON');log=self.root/(project+'.log')
   result=subprocess.run([DOTNET,str(dll),'--config',str(config),'--log',str(log)],capture_output=True,text=True,timeout=20);self.assertEqual(1,result.returncode);self.assertIn('stopped',log.read_text())
if __name__=='__main__':unittest.main(verbosity=2)
