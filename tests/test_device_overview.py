"""Device chart and current-state integration with reordered/partial snapshots."""
from datetime import datetime,timedelta,timezone
import json,os,shutil,socket,sqlite3,subprocess,tempfile,time,unittest,urllib.request,urllib.error,uuid
from pathlib import Path
from test_dashboard import FIELDS,ROOT

class DeviceOverviewTests(unittest.TestCase):
    def setUp(self):
        self.temp=tempfile.TemporaryDirectory();self.db=Path(self.temp.name)/'events.db'
        self.now=datetime.now(timezone.utc);self.rows=[]
        with sqlite3.connect(self.db) as db:
            db.execute('CREATE TABLE events('+','.join(f'[{f}] TEXT' for f in FIELDS)+',host TEXT,date_utc TEXT,received_utc TEXT)')
        self.add('TCP_SNAPSHOT',-50,'baseline',protocol='TCP',local='10.0.0.5',port='50001',peer='198.51.100.10',pid='1')
        self.add('UDP_SNAPSHOT',-50,'baseline',protocol='UDP',local='0.0.0.0',port='5353',pid='2')
        self.add('HEARTBEAT',-49,'baseline',state='SNAPSHOT_COMPLETE:2')
        self.add('TCP_DISCONNECTED',-30,'poll-2',protocol='TCP',local='10.0.0.5',port='50001',peer='198.51.100.10',pid='1')
        self.add('TCP_CONNECTED',-20,'poll-3',protocol='TCP',local='10.0.0.5',port='50002',peer='2001:db8::5',pid='3')
        self.add('TCP_SNAPSHOT',-11,'partial',protocol='TCP',local='10.0.0.5',port='50003',peer='198.51.100.30',pid='4')
        self.add('HEARTBEAT',-10,'partial',state='SNAPSHOT_COMPLETE:2')
        # Receipt order differs from event order; replaying must still close the old socket.
        self.add('TCP_CONNECTED',-40,'poll-1',protocol='TCP',local='10.0.0.5',port='50001',peer='198.51.100.10',pid='1')
        with socket.socket() as s:s.bind(('127.0.0.1',0));port=s.getsockname()[1]
        self.url=f'http://127.0.0.1:{port}';self.log=(Path(self.temp.name)/'gui.log').open('w+')
        dotnet=os.environ.get('DOTNET_HOST_PATH') or shutil.which('dotnet')
        self.process=subprocess.Popen([dotnet,str(ROOT/'Listener_DB_GUI/bin/Release/net10.0/Listener_DB_GUI.dll'),'--urls',self.url],env={**os.environ,'WATCHER_DB_PATH':str(self.db)},stdout=self.log,stderr=subprocess.STDOUT)
        self.addCleanup(self.cleanup)
        for _ in range(100):
            try:urllib.request.urlopen(self.url,timeout=1).close();break
            except (urllib.error.URLError,TimeoutError):time.sleep(.05)
        else:self.log.seek(0);raise RuntimeError(self.log.read())
    def cleanup(self):
        self.process.terminate();self.process.wait(timeout=10);self.log.close();self.temp.cleanup()
    def add(self,kind,seconds,snapshot,protocol='',local='',port='',peer='',pid='',state='',host='Live-PC'):
        stamp=(self.now+timedelta(seconds=seconds)).isoformat()
        row=dict.fromkeys(FIELDS,'');row.update(EVENT_ID=str(uuid.uuid4()),EVENT_TYPE=kind,TIMESTAMP_UTC=stamp,MONITORED_HOST=host,SOURCE_IP=local,SOURCE_PORT=port,RECIEVER_IP=peer,PORT_USED='443' if peer else '',PROCESS_ID=pid,PROCESS_NAME='app<script>',PROTOCOL=protocol,STATE=state or ('BOUND' if protocol=='UDP' else 'ESTABLISHED'),SNAPSHOT_ID=snapshot,COLLECTOR_PEER_IP='192.0.2.5')
        with sqlite3.connect(self.db) as db:db.execute('INSERT INTO events VALUES('+','.join('?' for _ in range(len(FIELDS)+3))+')',[row[f] for f in FIELDS]+[host,stamp[:10],self.now.isoformat()])
    def get(self,host='Live-PC',suffix=''):
        with urllib.request.urlopen(self.url+'/api/device-overview?host='+host+suffix) as r:return json.load(r)
    def test_reconstruction_partial_snapshot_rates_and_validation(self):
        data=self.get();self.assertEqual('updating',data['status']);self.assertEqual(2,data['total']);self.assertEqual(1,data['tcp']);self.assertEqual(1,data['udp'])
        self.assertEqual(['10.0.0.5'],data['device_ips']);self.assertEqual('192.0.2.5',data['collector_peer_ip'])
        self.assertEqual({'50002','5353'},{r['SOURCE_PORT'] for r in data['connections']})
        self.assertEqual(60,len(data['buckets']));self.assertEqual(2,sum(b['tcp'] for b in data['buckets']))
        self.assertEqual(round(2/60,2),data['average_tcp_per_minute'])
        # A newly received old event cannot reopen a socket that closed later.
        self.assertNotIn('50001',{r['SOURCE_PORT'] for r in data['connections']})
        with self.assertRaises(urllib.error.HTTPError) as e:self.get(suffix='&page=0')
        self.assertEqual(400,e.exception.code)
        with self.assertRaises(urllib.error.HTTPError) as e:self.get('unknown')
        self.assertEqual(404,e.exception.code)
        # Restart to an empty authoritative snapshot must clear all previous sockets.
        self.add('HEARTBEAT',-1,'restart-empty',state='SNAPSHOT_COMPLETE:0')
        data=self.get();self.assertEqual('fresh',data['status']);self.assertEqual(0,data['total'])
    def test_stale_legacy_pagination_and_future_events(self):
        self.add('HEARTBEAT',-300,'old',state='SNAPSHOT_COMPLETE:0',host='Old-PC')
        self.assertEqual('stale',self.get('Old-PC')['status'])
        self.add('HEARTBEAT',-10,'legacy',host='Legacy-PC')
        self.assertEqual('snapshot_required',self.get('Legacy-PC')['status'])
        self.add('HEARTBEAT',600,'future',state='SNAPSHOT_COMPLETE:0',host='Future-PC')
        self.assertEqual('clock_skew',self.get('Future-PC')['status'])
        for n in range(55):self.add('TCP_SNAPSHOT',-5,'many',protocol='TCP',local='10.0.0.9',port=str(50000+n),peer='198.51.100.1',pid='9',host='Many-PC')
        self.add('HEARTBEAT',-4,'many',state='SNAPSHOT_COMPLETE:55',host='Many-PC')
        first=self.get('Many-PC');second=self.get('Many-PC','&page=2')
        self.assertEqual(55,first['total']);self.assertEqual(50,len(first['connections']));self.assertEqual(5,len(second['connections']))
        self.assertEqual(0,sum(b['tcp'] for b in first['buckets'])) # Snapshots are not new opens.
    def test_browser_chart_device_ips_and_peer_table(self):
        from playwright.sync_api import sync_playwright,expect
        with sync_playwright() as p:
            browser=p.chromium.launch(executable_path=shutil.which('chromium'),args=['--no-sandbox']);page=browser.new_page(viewport={'width':1440,'height':1000})
            errors=[];page.on('pageerror',lambda e:errors.append(str(e)))
            page.goto(self.url);page.get_by_role('button',name='Live-PC',exact=False).click()
            expect(page.locator('#deviceIPs')).to_contain_text('10.0.0.5');expect(page.locator('#activeTCP')).to_have_text('1')
            expect(page.locator('#activeRows')).to_contain_text('2001:db8::5');expect(page.locator('#activeRows')).to_contain_text('Peer not reported')
            self.assertEqual(60,page.locator('#minuteChart rect').count());self.assertEqual(0,page.locator('script:not([src])').count())
            page.get_by_text('View minute-by-minute counts',exact=True).click();expect(page.locator('#minuteData tr')).to_have_count(60)
            page.set_viewport_size({'width':390,'height':844});self.assertTrue(page.locator('#overviewHeading').is_visible())
            self.assertEqual([],errors);browser.close()

if __name__=='__main__':unittest.main(verbosity=2)
