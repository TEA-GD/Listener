"""Build Collector/build/build-linux.sh first; uses temporary data, no service installation."""
import json,os,pathlib,socket,sqlite3,subprocess,tempfile,time,unittest,uuid
ROOT=pathlib.Path(__file__).resolve().parents[1]
class LinuxCollector(unittest.TestCase):
 def test_published_native_collector(self):
  executable=ROOT/'Collector/output/linux-collector/WatcherCollector'
  self.assertTrue(executable.exists(),'Build the Linux Collector first')
  with tempfile.TemporaryDirectory(prefix='linux-collector-') as directory:
   root=pathlib.Path(directory)
   with socket.socket() as s:s.bind(('127.0.0.1',0));port=s.getsockname()[1]
   cfg=root/'collector.json';db=root/'events.db';cfg.write_text(json.dumps({'bind_host':'127.0.0.1','bind_port':port,'database_path':str(db),'csv_dir':str(root/'csv'),'log_file':str(root/'collector.log'),'allowed_client_cidrs':['127.0.0.1/32']}))
   env={**os.environ,'DOTNET_BUNDLE_EXTRACT_BASE_DIR':str(root/'bundle')}
   check=subprocess.run([str(executable),'--config',str(cfg),'--check-config'],env=env,capture_output=True,text=True,timeout=30)
   self.assertEqual(0,check.returncode,check.stdout+check.stderr);self.assertIn('CONFIG OK',check.stdout)
   with (root/'console.log').open('w+') as log:
    process=subprocess.Popen([str(executable),'--config',str(cfg)],env=env,stdout=log,stderr=log)
    try:
     for _ in range(100):
      try:
       with socket.create_connection(('127.0.0.1',port),timeout=.2):break
      except OSError:time.sleep(.05)
     else:log.seek(0);self.fail(log.read())
     id=str(uuid.uuid4());row={'EVENT_ID':id,'EVENT_TYPE':'HEARTBEAT','TIMESTAMP_UTC':'2026-11-23T10:00:00Z','MONITORED_HOST':'linux-native-smoke','DEVICE_CATEGORY':'linux_servers'}
     with socket.create_connection(('127.0.0.1',port),timeout=5) as s:
      s.sendall((json.dumps({'event':row,'request_pipeline':True})+'\n').encode());self.assertEqual('ACK '+id+' PIPELINE64\n',s.makefile('rb').readline().decode())
     with sqlite3.connect(db) as c:
      self.assertEqual(1,c.execute('SELECT count(*) FROM events_linux_servers WHERE EVENT_ID=?',(id,)).fetchone()[0])
      self.assertEqual(2,c.execute("SELECT count(*) FROM sqlite_master WHERE name IN ('device_network_communications','device_process_snapshots')").fetchone()[0])
    finally:
     process.terminate()
     try:process.wait(timeout=30)
     except subprocess.TimeoutExpired:process.kill();process.wait()
if __name__=='__main__':unittest.main()
