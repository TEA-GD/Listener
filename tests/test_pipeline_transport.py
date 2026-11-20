"""Real durable Collector ACKs, legacy compatibility, rejection, and PowerShell pipeline windows."""
import json,os,pathlib,shutil,socket,sqlite3,subprocess,tempfile,threading,time,unittest,uuid
ROOT=pathlib.Path(__file__).resolve().parents[1]
class PipelineTests(unittest.TestCase):
 def test_collector_pipeline_and_legacy(self):
  with tempfile.TemporaryDirectory() as temp:
   root=pathlib.Path(temp)
   with socket.socket() as s:s.bind(('127.0.0.1',0));port=s.getsockname()[1]
   db=root/'events.db';cfg=root/'collector.json';cfg.write_text(json.dumps({'bind_host':'127.0.0.1','bind_port':port,'database_path':str(db),'csv_dir':str(root/'csv'),'log_file':str(root/'log'),'allowed_client_cidrs':[]}))
   dotnet=os.environ.get('DOTNET_HOST_PATH') or shutil.which('dotnet')
   with (root/'output').open('w+') as log:
    process=subprocess.Popen([dotnet,str(ROOT/'tests/ServerAgentCollector/bin/Release/net10.0/ServerAgentCollector.dll'),'--config',str(cfg)],stdout=log,stderr=log)
    try:
     for _ in range(100):
      try:
       with socket.create_connection(('127.0.0.1',port),timeout=.2):break
      except OSError:time.sleep(.05)
     else:log.seek(0);self.fail(log.read())
     def event():return {'EVENT_ID':str(uuid.uuid4()),'EVENT_TYPE':'HEARTBEAT','MONITORED_HOST':'VPN-PC','TIMESTAMP_UTC':'2026-11-20T10:00:00Z'}
     rows=[event() for _ in range(193)]
     with socket.create_connection(('127.0.0.1',port),timeout=10) as s:
      stream=s.makefile('rb');s.sendall((json.dumps({'event':rows[0],'request_pipeline':True})+'\n').encode())
      self.assertEqual('ACK '+rows[0]['EVENT_ID']+' PIPELINE64\n',stream.readline().decode())
      for start in range(1,193,64):
       group=rows[start:start+64];s.sendall(''.join(json.dumps({'event':row})+'\n' for row in group).encode())
       for row in group:
        self.assertEqual('ACK '+row['EVENT_ID']+'\n',stream.readline().decode())
        with sqlite3.connect(db) as c:self.assertEqual(1,c.execute('SELECT count(*) FROM events WHERE EVENT_ID=?',(row['EVENT_ID'],)).fetchone()[0])
      # Replayed/ACK-lost records remain idempotent.
      s.sendall(''.join(json.dumps({'event':row})+'\n' for row in rows[1:65]).encode())
      for row in rows[1:65]:self.assertEqual('ACK '+row['EVENT_ID']+'\n',stream.readline().decode())
     legacy=event()
     with socket.create_connection(('127.0.0.1',port),timeout=5) as s:
      s.sendall((json.dumps({'event':legacy})+'\n').encode());self.assertEqual('ACK '+legacy['EVENT_ID']+'\n',s.makefile('rb').readline().decode())
     good,bad,later=event(),event(),event();bad['EVENT_TYPE']='invalid'
     with socket.create_connection(('127.0.0.1',port),timeout=5) as s:
      s.sendall(''.join(json.dumps({'event':row})+'\n' for row in [good,bad,later]).encode());stream=s.makefile('rb')
      self.assertEqual('ACK '+good['EVENT_ID']+'\n',stream.readline().decode());self.assertTrue(stream.readline().startswith(b'ERR'))
     with sqlite3.connect(db) as c:
      self.assertEqual(195,c.execute('SELECT count(*) FROM events').fetchone()[0]);self.assertEqual(0,c.execute('SELECT count(*) FROM events WHERE EVENT_ID=?',(later['EVENT_ID'],)).fetchone()[0])
    finally:
     process.terminate()
     try:process.wait(timeout=10)
     except subprocess.TimeoutExpired:process.kill();process.wait()
 def test_powershell_pipeline_byte_cap(self):
  pwsh=os.environ.get('PWSH_PATH') or shutil.which('pwsh')
  self.assertTrue(pwsh,'Set PWSH_PATH')
  with tempfile.TemporaryDirectory() as temp,socket.socket() as listener:
   root=pathlib.Path(temp);spool=root/'spool';spool.mkdir();ids=[];stamp='2026-11-01T00:00:00Z';body_bytes=0
   for i in range(21):
    id=str(uuid.uuid4());ids.append(id);raw=json.dumps({'EVENT_ID':id,'TIMESTAMP_UTC':stamp,'padding':'x'*20000});(spool/f'{i:04}.json').write_text(raw);body_bytes=len(('{"event":'+raw+'}\n').encode())
   listener.bind(('127.0.0.1',0));listener.listen();listener.settimeout(15);errors=[];groups=[]
   def receive():
    try:
     connection,_=listener.accept()
     with connection,connection.makefile('rb') as stream:
      connection.settimeout(5);first=json.loads(stream.readline());self.assertTrue(first['request_pipeline']);connection.sendall(('ACK '+ids[0]+' PIPELINE64\n').encode());at=1
      while at<len(ids):
       count=min(64,131072//body_bytes,len(ids)-at);groups.append(count)
       for index in range(count):
        row=json.loads(stream.readline())['event'];self.assertEqual(ids[at+index],row['EVENT_ID']);self.assertEqual(stamp,row['TIMESTAMP_UTC'])
       time.sleep(.1);connection.sendall(''.join('ACK '+id+'\n' for id in ids[at:at+count]).encode());at+=count
    except BaseException as error:errors.append(error)
   worker=threading.Thread(target=receive,daemon=True);worker.start();cfg=root/'agent.json';cfg.write_text(json.dumps({'agent_id':'VPN-PC','collector_host':'localhost','collector_port':listener.getsockname()[1],'spool_dir':str(spool),'log_file':str(root/'log'),'poll_seconds':5,'heartbeat_seconds':60,'max_spool_events':50000,'send_batch_size':1000}))
   result=subprocess.run([pwsh,'-NoProfile','-File',str(ROOT/'tests/Test-ServerAgent.ps1'),'-AgentScript',str(ROOT/'ServerAgent/Watcher-ServerAgent.ps1'),'-ConfigPath',str(cfg),'-Mode','Send'],capture_output=True,text=True,timeout=25)
   worker.join(timeout=5);self.assertFalse(worker.is_alive());self.assertEqual([],errors);self.assertEqual(0,result.returncode,result.stdout+result.stderr);self.assertEqual([],list(spool.glob('*.json')));self.assertTrue(all(size<=6 for size in groups))
if __name__=='__main__':unittest.main()
