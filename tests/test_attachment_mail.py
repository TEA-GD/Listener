"""Real Agent local endpoint → durable spool → Collector, migration and mail GUI access."""
import json,os,pathlib,shutil,socket,sqlite3,subprocess,tempfile,time,unittest,urllib.request,urllib.error,uuid
ROOT=pathlib.Path(__file__).resolve().parents[1]
class AttachmentMailTests(unittest.TestCase):
 def test_migration_rolls_back_invalid_legacy_metadata(self):
  dotnet=os.environ.get('DOTNET_HOST_PATH') or shutil.which('dotnet')
  with tempfile.TemporaryDirectory() as temp:
   root=pathlib.Path(temp);db=root/'mail.db';cfg=root/'collector.json'
   with sqlite3.connect(db) as c:
    for table in ['email_alerts','email_metadata_alerts']:c.execute('CREATE TABLE '+table+'(alert_id TEXT,client_label TEXT,sender TEXT,recipients_json TEXT,attachment_names_json TEXT,received_utc TEXT)')
    c.execute('INSERT INTO email_alerts VALUES(?,?,?,?,?,?)',('a','old','from','[]','["a.pdf"]','2026-11-01T00:00:00Z'))
    c.execute('INSERT INTO email_metadata_alerts VALUES(?,?,?,?,?,?)',('b','old','from','[]','invalid json','2026-11-01T00:00:00Z'))
   cfg.write_text(json.dumps({'database_path':str(db),'csv_dir':str(root/'csv'),'log_file':str(root/'log')}))
   cmd=[dotnet,str(ROOT/'tests/ServerAgentCollector/bin/Release/net10.0/ServerAgentCollector.dll'),'--config',str(cfg),'--check-config']
   result=subprocess.run(cmd,capture_output=True,text=True,timeout=20);self.assertNotEqual(0,result.returncode)
   with sqlite3.connect(db) as c:
    self.assertEqual(2,c.execute("SELECT count(*) FROM sqlite_master WHERE name IN ('email_alerts','email_metadata_alerts')").fetchone()[0])
    self.assertEqual(1,c.execute('SELECT count(*) FROM email_alerts').fetchone()[0]);c.execute("UPDATE email_metadata_alerts SET attachment_names_json='[]'")
   result=subprocess.run(cmd,capture_output=True,text=True,timeout=20);self.assertEqual(0,result.returncode,result.stdout+result.stderr)
   with sqlite3.connect(db) as c:self.assertEqual(1,c.execute('SELECT count(*) FROM mail_information').fetchone()[0])
 def test_route_migration_auth_and_duplicates(self):
  dotnet=os.environ.get('DOTNET_HOST_PATH') or shutil.which('dotnet')
  with tempfile.TemporaryDirectory() as temp:
   root=pathlib.Path(temp);db=root/'events.db';spool=root/'spool';processes=[]
   def port():
    with socket.socket() as s:s.bind(('127.0.0.1',0));return s.getsockname()[1]
   tcp,local,http=port(),port(),port();token='x'*40;view='v'*40
   with sqlite3.connect(db) as c:
    c.executescript('CREATE TABLE email_alerts(alert_id TEXT,client_label TEXT,sender TEXT,recipients_json TEXT,attachment_names_json TEXT,received_utc TEXT,subject TEXT,body_text TEXT);CREATE TABLE email_metadata_alerts(alert_id TEXT,client_label TEXT,sender TEXT,recipients_json TEXT,attachment_names_json TEXT,received_utc TEXT);')
    c.execute('INSERT INTO email_alerts VALUES(?,?,?,?,?,?,?,?)',('old-id','Old-PC','old@example.test','["to@example.test"]','["old.xlsx"]','2026-11-01T00:00:00Z','OLD SUBJECT','OLD BODY'))
    c.execute('INSERT INTO email_metadata_alerts VALUES(?,?,?,?,?,?)',('no-attachment','Old-PC','old@example.test','[]','[]','2026-11-01T00:00:00Z'))
   config=root/'agent.json';config.write_text(json.dumps({'agent_id':'Mail-PC','device_category':'user_pcs','collector_host':'localhost','collector_port':tcp,'spool_dir':str(spool),'mail_token':token,'mail_port':local}))
   with (root/'logs').open('w+') as log:
    try:
     agent=subprocess.Popen([dotnet,str(ROOT/'Agent/src/WatcherAgent/bin/Release/net10.0/linux-x64/WatcherAgent.dll'),'--config',str(config)],stdout=log,stderr=log);processes.append(agent)
     url=f'http://127.0.0.1:{local}/api/mail-attachments'
     for _ in range(100):
      try:
       urllib.request.urlopen(f'http://127.0.0.1:{local}/');break
      except urllib.error.HTTPError:break
      except OSError:time.sleep(.05)
     mail={'alert_id':str(uuid.uuid4()),'date_received_utc':'2026-11-23T10:00:00Z','sender':'From <from@example.test>','recipients':['to@example.test'],'attachments':[{'name':'file.pdf','format':'application/pdf','size_bytes':12345},{'name':'unknown.zip','format':'zip','size_bytes':None}]}
     def post(data,key=token):
      req=urllib.request.Request(url,data=json.dumps(data).encode(),headers={'Content-Type':'application/json','Authorization':'Bearer '+key})
      with urllib.request.urlopen(req,timeout=30) as response:return json.load(response)
     for data,key,code in [(mail,'wrong',401),({**mail,'attachments':[]},token,400),({**mail,'subject':'forbidden'},token,400)]:
      with self.assertRaises(urllib.error.HTTPError) as error:post(data,key)
      self.assertEqual(code,error.exception.code)
     self.assertEqual({'status':'queued','alert_id':mail['alert_id']},post(mail))
     queued=[json.loads(p.read_text()) for p in spool.glob('*.json') if json.loads(p.read_text()).get('EVENT_TYPE')=='MAIL_ATTACHMENT'];self.assertEqual(1,len(queued));self.assertEqual(mail['alert_id'],queued[0]['EVENT_ID'])
     collector=root/'collector.json';collector.write_text(json.dumps({'bind_host':'127.0.0.1','bind_port':tcp,'database_path':str(db),'csv_dir':str(root/'csv'),'log_file':str(root/'collector.log'),'allowed_client_cidrs':[]}))
     processes.append(subprocess.Popen([dotnet,str(ROOT/'tests/ServerAgentCollector/bin/Release/net10.0/ServerAgentCollector.dll'),'--config',str(collector)],stdout=log,stderr=log))
     for _ in range(300):
      try:
       with sqlite3.connect(db) as c:count=c.execute('SELECT count(*) FROM mail_information WHERE alert_id=?',(mail['alert_id'],)).fetchone()[0]
       if count:break
      except sqlite3.Error:pass
      time.sleep(.2)
     else:log.seek(0);self.fail(log.read())
     with sqlite3.connect(db) as c:
      self.assertEqual(0,c.execute("SELECT count(*) FROM sqlite_master WHERE name IN ('email_alerts','email_metadata_alerts')").fetchone()[0])
      self.assertEqual(2,c.execute('SELECT count(*) FROM mail_information').fetchone()[0]);old=c.execute("SELECT date_received_utc,attachments_json FROM mail_information WHERE host='Old-PC'").fetchone();self.assertIsNone(old[0]);self.assertIsNone(json.loads(old[1])[0]['size_bytes'])
      row=c.execute('SELECT attachment_count,attachments_json FROM mail_information WHERE alert_id=?',(mail['alert_id'],)).fetchone();self.assertEqual(2,row[0]);self.assertEqual(12345,json.loads(row[1])[0]['size_bytes'])
      self.assertEqual(0,c.execute("SELECT count(*) FROM events WHERE EVENT_TYPE='MAIL_ATTACHMENT'").fetchone()[0])
     post(mail);time.sleep(6)
     with sqlite3.connect(db) as c:self.assertEqual(1,c.execute('SELECT count(*) FROM mail_information WHERE alert_id=?',(mail['alert_id'],)).fetchone()[0])
     base=f'http://127.0.0.1:{http}'
     processes.append(subprocess.Popen([dotnet,str(ROOT/'Listener_DB_GUI/bin/Release/net10.0/Listener_DB_GUI.dll'),'--urls',base],env={**os.environ,'WATCHER_DB_PATH':str(db),'LISTENER_MAIL_VIEW_TOKEN':view},stdout=log,stderr=log))
     for _ in range(100):
      try:urllib.request.urlopen(base).close();break
      except OSError:time.sleep(.05)
     with self.assertRaises(urllib.error.HTTPError) as error:urllib.request.urlopen(base+'/api/mail')
     self.assertEqual(401,error.exception.code)
     with urllib.request.urlopen(urllib.request.Request(base+'/api/mail',headers={'Authorization':'Bearer '+view})) as response:data=json.load(response)
     self.assertEqual(2,data['total']);self.assertNotIn('body_text',str(data));self.assertNotIn('OLD SUBJECT',str(data))
     from playwright.sync_api import sync_playwright,expect
     with sync_playwright() as p:
      browser=p.chromium.launch(executable_path=shutil.which('chromium'),args=['--no-sandbox']);page=browser.new_page();page.goto(base+'/mail.html');page.locator('#mailKey').fill(view);page.get_by_role('button',name='Load / search mail').click();expect(page.locator('#mailRows')).to_contain_text('12345 bytes');expect(page.locator('#mailRows')).to_contain_text('unknown size')
      # Exercise the actual extension scripts with Thunderbird APIs mocked and real IndexedDB.
      page.goto(base+'/analyze.html')
      page.evaluate('''() => {
       window.sent=[];window.values={settings:{enabled:true,port:58766,token:'x'.repeat(40)}};
       const messages=[{id:1,headerMessageId:'no-files',author:'a@example.test',recipients:['b@example.test'],folder:{accountId:'account'}},{id:2,headerMessageId:'with-files',author:'a@example.test',recipients:['b@example.test'],folder:{accountId:'account'}}];
       for(const message of messages)Object.defineProperty(message,'subject',{get(){throw Error('Subject must not be read')}});
       window.messages=messages;
       window.browser={storage:{local:{get:async key=>{if(Array.isArray(key))return Object.fromEntries(key.map(k=>[k,values[k]]));return {[key]:values[key]};},set:async obj=>Object.assign(values,obj)},onChanged:{addListener:()=>{}}},
        messages:{onNewMailReceived:{addListener:f=>window.receive=f},get:async id=>messages.find(m=>m.id===id),listAttachments:async id=>id===1?[]:[{name:'file.pdf',contentType:'application/pdf',size:42}]},
        alarms:{create:()=>{},onAlarm:{addListener:f=>window.alarm=f}},browserAction:{onClicked:{addListener:()=>{}}},runtime:{openOptionsPage:()=>{}}};
       window.fetch=async (url,options)=>{const payload=JSON.parse(options.body);sent.push(payload);return sent.length===1?{ok:false,status:503}:{ok:true,json:async()=>({status:'queued',alert_id:payload.alert_id})};};
      }''')
      page.evaluate((ROOT/'Thunderbird_Attachments/extension/core.js').read_text());page.evaluate((ROOT/'Thunderbird_Attachments/extension/background.js').read_text())
      page.evaluate("receive({accountId:'account',specialUse:['inbox']},{messages})")
      def wait_expression(expression):
       for _ in range(100):
        if page.evaluate(expression):return
        page.wait_for_timeout(50)
       self.fail('Extension queue stalled: '+str(page.evaluate('({sent,values})')))
      wait_expression('sent.length===1 && values.pending===1');first=page.evaluate('sent[0]');self.assertEqual(1,len(first['attachments']));self.assertEqual(42,first['attachments'][0]['size_bytes']);self.assertNotIn('subject',first)
      page.evaluate('''async()=>{const db=await new Promise(resolve=>{const r=indexedDB.open('listener-attachment-mail',1);r.onsuccess=()=>resolve(r.result)});await new Promise(resolve=>{const tx=db.transaction('jobs','readwrite');const store=tx.objectStore('jobs'),r=store.getAll();r.onsuccess=()=>{for(const job of r.result)if(job.state==='pending'){job.due=0;store.put(job)}};tx.oncomplete=resolve});alarm({name:'attachment-mail-retry'});}''')
      wait_expression('sent.length===2 && values.pending===0');self.assertEqual(first,page.evaluate('sent[1]'));browser.close()
    finally:
     for process in reversed(processes):
      process.terminate()
      try:process.wait(timeout=15)
      except subprocess.TimeoutExpired:process.kill();process.wait()
if __name__=='__main__':unittest.main()
