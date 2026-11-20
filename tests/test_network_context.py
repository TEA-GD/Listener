"""Real Collector context routing, incomplete replay, deduplication and GUI correlation."""
import json,os,pathlib,shutil,socket,sqlite3,subprocess,tempfile,time,unittest,urllib.request,uuid
from datetime import datetime,timezone
ROOT=pathlib.Path(__file__).resolve().parents[1]
class NetworkContextTests(unittest.TestCase):
 def test_delivery_completion_and_browser(self):
  dotnet=os.environ.get('DOTNET_HOST_PATH') or shutil.which('dotnet')
  with tempfile.TemporaryDirectory() as temp:
   root=pathlib.Path(temp);db=root/'events.db';processes=[];stamp=datetime.now(timezone.utc).isoformat();day=stamp[:10]
   def port():
    with socket.socket() as s:s.bind(('127.0.0.1',0));return s.getsockname()[1]
   tcp=port();http=port();config=root/'collector.json';config.write_text(json.dumps({'bind_host':'127.0.0.1','bind_port':tcp,'database_path':str(db),'csv_dir':str(root/'csv'),'log_file':str(root/'log'),'allowed_client_cidrs':[]}))
   with (root/'stdout').open('w+') as log:
    try:
     processes.append(subprocess.Popen([dotnet,str(ROOT/'tests/ServerAgentCollector/bin/Release/net10.0/ServerAgentCollector.dll'),'--config',str(config)],stdout=log,stderr=log))
     for _ in range(100):
      try:
       with socket.create_connection(('127.0.0.1',tcp),timeout=.2):break
      except OSError:time.sleep(.05)
     else:log.seek(0);self.fail(log.read())
     def event(kind,data=None,snapshot='context-1'):
      row={'EVENT_ID':str(uuid.uuid4()),'EVENT_TYPE':kind,'MONITORED_HOST':'Context-PC','TIMESTAMP_UTC':stamp,'SNAPSHOT_ID':snapshot,'DEVICE_CATEGORY':'user_pcs'}
      if data is not None:row['CONTEXT_JSON']=json.dumps(data)
      return row
     def send(row):
      with socket.create_connection(('127.0.0.1',tcp),timeout=5) as s:
       s.sendall((json.dumps({'event':row})+'\n').encode());return s.makefile().readline()
     hb=event('HEARTBEAT');self.assertEqual('ACK '+hb['EVENT_ID']+'\n',send(hb))
     marker=event('CONTEXT_COMPLETE',{'networks':1,'processes':2,'status':'ok','note':''});send(marker)
     processes.append(subprocess.Popen([dotnet,str(ROOT/'Listener_DB_GUI/bin/Release/net10.0/Listener_DB_GUI.dll'),'--urls',f'http://127.0.0.1:{http}'],env={**os.environ,'WATCHER_DB_PATH':str(db)},stdout=log,stderr=log))
     url=f'http://127.0.0.1:{http}'
     def get(query):
      with urllib.request.urlopen(url+'/api/network-context?host=Context-PC&'+query) as r:return json.load(r)
     for _ in range(100):
      try:data=get('day='+day);break
      except OSError:time.sleep(.05)
     else:log.seek(0);self.fail(log.read())
     self.assertEqual(0,data['total'],'Incomplete context displayed')
     net=event('NETWORK_CONTEXT',{'adapter_id':'wifi1','adapter_name':'Wi-Fi','network_type':'wifi','network_name':'Office <wifi>','connection_type':'802.11','state':'Up','addresses':'192.0.2.10','gateway':'192.0.2.1'})
     vpn=event('PROCESS_CONTEXT',{'entry_type':'process','pid':123,'name':'AmneziaVPN.exe','path':'C:\\VPN\\AmneziaVPN.exe','started_utc':stamp})
     service=event('PROCESS_CONTEXT',{'entry_type':'service','pid':124,'name':'OpenVPNService','path':'','started_utc':''})
     for row in [net,vpn,service,vpn,marker]:self.assertEqual('ACK '+row['EVENT_ID']+'\n',send(row))
     data=get('day='+day);self.assertEqual(1,data['total']);self.assertEqual(2,data['snapshots'][0]['vpn_candidates'])
     detail=get('snapshot=context-1');self.assertEqual('Office <wifi>',detail['networks'][0]['network_name']);self.assertEqual(2,len(detail['processes']))
     with sqlite3.connect(db) as c:
      self.assertEqual(1,c.execute('SELECT count(*) FROM events').fetchone()[0],'Context polluted connection history')
      self.assertEqual(2,c.execute('SELECT count(*) FROM device_process_snapshots').fetchone()[0])
     # Corrupt context is rejected and the reserved global ID must roll back.
     bad=event('PROCESS_CONTEXT',{'pid':-1});self.assertFalse(send(bad).startswith('ACK'))
     with sqlite3.connect(db) as c:self.assertEqual(0,c.execute('SELECT count(*) FROM event_registry WHERE event_id=?',(bad['EVENT_ID'],)).fetchone()[0])
     agentConfig=root/'agent.json';agentConfig.write_text(json.dumps({'agent_id':'Sample-PC','device_category':'user_pcs','collector_host':'localhost','collector_port':tcp,'spool_dir':str(root/'spool')}))
     sampler=subprocess.Popen([dotnet,str(ROOT/'Agent/src/WatcherAgent/bin/Release/net10.0/linux-x64/WatcherAgent.dll'),'--config',str(agentConfig)],stdout=log,stderr=log);processes.append(sampler)
     for _ in range(200):
      with sqlite3.connect(db) as c:complete=c.execute("SELECT count(*) FROM device_context_snapshots WHERE host='Sample-PC'").fetchone()[0]
      if complete:break
      if sampler.poll() is not None:log.seek(0);self.fail(log.read())
      time.sleep(.2)
     else:log.seek(0);self.fail('Context backlog did not finish: '+log.read())
     sampler.terminate();sampler.wait(timeout=10)
     with sqlite3.connect(db) as c:
      self.assertGreater(c.execute("SELECT count(*) FROM device_process_snapshots WHERE host='Sample-PC'").fetchone()[0],0)
      self.assertEqual(1,c.execute("SELECT count(*) FROM device_context_snapshots WHERE host='Sample-PC'").fetchone()[0])
     from playwright.sync_api import sync_playwright,expect
     with sync_playwright() as p:
      browser=p.chromium.launch(executable_path=shutil.which('chromium'),args=['--no-sandbox']);page=browser.new_page();errors=[];page.on('pageerror',lambda e:errors.append(str(e)))
      page.goto(url+'/online.html?host=Context-PC');page.get_by_role('button',name='Show networks & processes').click();expect(page.locator('#contextSamples')).to_contain_text('ok')
      page.get_by_role('button',name='Inspect sample').click();expect(page.locator('#contextNetworks')).to_contain_text('Office <wifi>');expect(page.locator('#contextProcesses')).to_contain_text('AmneziaVPN.exe');expect(page.locator('#contextProcesses')).to_contain_text('OpenVPNService');self.assertEqual([],errors);browser.close()
    finally:
     for process in reversed(processes):
      process.terminate()
      try:process.wait(timeout=10)
      except subprocess.TimeoutExpired:process.kill();process.wait()
if __name__=='__main__':unittest.main()
