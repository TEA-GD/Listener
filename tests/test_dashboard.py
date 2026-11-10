"""Run after dotnet build Listener_DB_GUI -c Release. Uses temporary synthetic data only."""
import hashlib
import json
import os
from pathlib import Path
import shutil
import socket
import sqlite3
import subprocess
import tempfile
import time
import unittest
import urllib.error
import urllib.parse
import urllib.request

ROOT = Path(__file__).resolve().parents[1]
FIELDS = 'EVENT_ID EVENT_TYPE TIMESTAMP_UTC DATE_AND_TIME SOURCE_IP SOURCE_DOMAIN_NAME PROCESS_NAME PROCESS_ID RECIEVER_IP RECIEVER_DOMAINNAME PORT_USED SOFTWARE_THAT_USED SOURCE_PORT PROTOCOL STATE OPERATING_SYSTEM MONITORED_HOST SNAPSHOT_ID COLLECTOR_PEER_IP'.split()

class Dashboard(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.temp = tempfile.TemporaryDirectory(prefix='watcher-dashboard-')
        cls.db = Path(cls.temp.name) / 'events.db'
        with sqlite3.connect(cls.db) as db:
            db.execute('CREATE TABLE events (' + ','.join(f'[{f}] TEXT' for f in FIELDS) + ',host TEXT,date_utc TEXT,received_utc TEXT)')
            for i in range(121):
                row = dict.fromkeys(FIELDS, '')
                row.update(EVENT_ID=f'event-{i:03}', EVENT_TYPE='TCP_CONNECTED' if i % 2 else 'HEARTBEAT', TIMESTAMP_UTC='2026-11-09T09:00:00Z', PROCESS_NAME='browser.exe', SOURCE_IP='192.168.1.5', RECIEVER_IP='203.0.113.1', PORT_USED='443', MONITORED_HOST='PC-01', COLLECTOR_PEER_IP='192.168.1.5')
                db.execute('INSERT INTO events VALUES(' + ','.join('?' for _ in range(len(FIELDS)+3)) + ')', [row[f] for f in FIELDS] + ['PC-01','2026-11-09','2026-11-09T09:00:01Z'])
            row['EVENT_ID']='other'; row['MONITORED_HOST']='PC-02<script>'
            db.execute('INSERT INTO events VALUES(' + ','.join('?' for _ in range(len(FIELDS)+3)) + ')', [row[f] for f in FIELDS] + ['PC-02<script>','2026-11-09','2026-11-09T09:00:01Z'])
        with sqlite3.connect(cls.db) as db:
            counter=0
            def add(ip,stamp,kind='TCP_CONNECTED',pc='Rare-PC',port='443',process='worker.exe'):
                nonlocal counter
                counter+=1
                row=dict.fromkeys(FIELDS,'')
                row.update(EVENT_ID=f'rare-{counter}',EVENT_TYPE=kind,TIMESTAMP_UTC=stamp,
                    MONITORED_HOST=pc,RECIEVER_IP=ip,PORT_USED=port,PROCESS_NAME=process)
                db.execute('INSERT INTO events VALUES('+','.join('?' for _ in range(len(FIELDS)+3))+')',
                    [row[f] for f in FIELDS]+[pc,stamp[:10],stamp])
            add('203.0.113.20','2026-11-09T09:00:00Z')
            add('203.0.113.20','2026-11-09T10:00:00Z','ACTIVE_AT_START',port='8443',process='second.exe')
            add('203.0.113.20','2026-11-10T10:00:00Z') # future cannot influence the selected day
            add('203.0.113.20','2026-01-01T10:00:00Z',pc='PC-02<script>') # history is per PC
            add('203.0.113.21','2026-11-09T09:00:00Z')
            add('203.0.113.21','2026-09-01T09:00:00Z')
            add('203.0.113.21','2026-10-01T09:00:00Z','TCP_DISCONNECTED')
            add('203.0.113.22','2026-11-08T09:00:00Z')
            add('203.0.113.22','2026-11-09T09:00:00Z')
            add('203.0.113.23','2026-10-10T09:00:00+00:00')
            add('203.0.113.23','2026-11-09T09:00:00Z') # exactly 30 days
            add('203.0.113.24','2026-11-09T09:00:00Z','TCP_DISCONNECTED')
            add('203.0.113.25','2026-11-09T09:00:00Z','HEARTBEAT')
            add('','2026-11-09T09:00:00Z')
            add('203.0.113.26','2026-11-09T08:00:00Z','TCP_DISCONNECTED')
            add('203.0.113.26','2026-11-09T09:00:00Z')
            add('2001:db8::1','2026-11-09T09:00:00Z')
            for n in range(55):add(f'10.20.0.{n+1}','2026-11-09T09:00:00Z')
        cls.original = hashlib.sha256(cls.db.read_bytes()).hexdigest()
        cls.processes=[]
        cls.url=cls.start(cls.db)

    @classmethod
    def start(cls, path):
        with socket.socket() as s:
            s.bind(('127.0.0.1',0)); port=s.getsockname()[1]
        url=f'http://127.0.0.1:{port}'
        env=dict(os.environ, WATCHER_DB_PATH=str(path))
        dotnet=os.environ.get('DOTNET_HOST_PATH') or shutil.which('dotnet')
        if not dotnet: raise RuntimeError('Put .NET 10 SDK on PATH first')
        log=open(Path(cls.temp.name)/f'{port}.log','w+')
        process=subprocess.Popen([dotnet,str(ROOT/'Listener_DB_GUI/bin/Release/net10.0/Listener_DB_GUI.dll'),'--urls',url],env=env,stdout=log,stderr=subprocess.STDOUT)
        cls.processes.append((process,log))
        for _ in range(100):
            if process.poll() is not None:
                log.seek(0); raise RuntimeError(log.read())
            try:
                urllib.request.urlopen(url,timeout=1).close(); return url
            except (urllib.error.URLError, TimeoutError): time.sleep(.05)
        raise RuntimeError('Dashboard did not start')

    @classmethod
    def tearDownClass(cls):
        for process,log in cls.processes:
            process.terminate()
            try: process.wait(timeout=10)
            except subprocess.TimeoutExpired: process.kill(); process.wait()
            log.close()
        cls.temp.cleanup()

    def request(self,path):
        with urllib.request.urlopen(self.url+path) as r: return json.load(r)

    def test_queries_filters_pagination(self):
        self.assertEqual(3,len(self.request('/api/pcs')))
        d=self.request('/api/pc?host=PC-01')
        self.assertEqual(121,d['stats']['events']); self.assertEqual(60,d['stats']['connections'])
        self.assertEqual(50,len(d['events'])); self.assertTrue(set(FIELDS)<=set(d['events'][0]))
        p=self.request('/api/pc?host=PC-01&page=3')
        self.assertEqual(21,len(p['events']))
        self.assertTrue({r['EVENT_ID'] for r in d['events']}.isdisjoint({r['EVENT_ID'] for r in p['events']}))
        self.assertEqual(60,self.request('/api/pc?host=PC-01&type=TCP_CONNECTED&q=browser&from=2026-11-09&to=2026-11-09')['stats']['events'])
        self.assertEqual(0,self.request('/api/pc?host=PC-01&from=2026-11-10')['stats']['events'])
        self.assertEqual(0,self.request('/api/pc?host='+urllib.parse.quote("x' OR 1=1--"))['stats']['events'])
        with self.assertRaises(urllib.error.HTTPError) as e: self.request('/api/pc?host=PC-01&page=0')
        self.assertEqual(400,e.exception.code)
        with self.assertRaises(urllib.error.HTTPError) as e: self.request('/api/pc?host=PC-01&from=2026-12-01&to=2026-11-01')
        self.assertEqual(400,e.exception.code)

    def test_missing_and_legacy_database(self):
        missing=Path(self.temp.name)/'missing.db'; url=self.start(missing)
        with self.assertRaises(urllib.error.HTTPError) as e: urllib.request.urlopen(url+'/api/pcs')
        self.assertEqual(503,e.exception.code); self.assertFalse(missing.exists())
        legacy=Path(self.temp.name)/'legacy.db'
        with sqlite3.connect(legacy) as db: db.execute('CREATE TABLE events(payload_json TEXT)')
        url=self.start(legacy)
        with self.assertRaises(urllib.error.HTTPError) as e: urllib.request.urlopen(url+'/api/pcs')
        self.assertIn('schema',json.load(e.exception)['error'])

    def test_read_only(self):
        self.request('/api/pcs'); self.request('/api/pc?host=PC-01')
        with self.assertRaises(urllib.error.HTTPError) as e:
            urllib.request.urlopen(urllib.request.Request(self.url+'/api/pcs',data=b'{}',method='POST'))
        self.assertEqual(405,e.exception.code)
        self.assertEqual(self.original,hashlib.sha256(self.db.read_bytes()).hexdigest())

    def test_new_and_rare_connections(self):
        query='/api/connections?host=Rare-PC&day=2026-11-09'
        first=self.request(query);second=self.request(query+'&page=2')
        self.assertEqual({'total':59,'newDestinations':57,'returnedDestinations':2},first['summary'])
        self.assertEqual(50,len(first['destinations']));self.assertEqual(9,len(second['destinations']))
        rows={r['ip']:r for r in first['destinations']+second['destinations']}
        self.assertEqual(59,len(rows))
        self.assertEqual('new',rows['203.0.113.20']['classification'])
        self.assertIsNone(rows['203.0.113.20']['previousSeen'])
        self.assertEqual(2,rows['203.0.113.20']['observations'])
        self.assertEqual({'443','8443'},set(rows['203.0.113.20']['ports'].split(',')))
        self.assertEqual(39,rows['203.0.113.21']['gapDays'])
        self.assertEqual('2026-10-01T09:00:00.000Z',rows['203.0.113.21']['previousSeen'])
        self.assertEqual(30,rows['203.0.113.23']['gapDays'])
        for ip in ['203.0.113.22','203.0.113.24','203.0.113.25','203.0.113.26','']:
            self.assertNotIn(ip,rows)
        self.assertEqual(0,self.request(query+'&gapDays=60')['summary']['returnedDestinations'])
        self.assertEqual(0,self.request('/api/connections?host=Rare-PC&day=2026-12-01')['summary']['total'])
        self.assertEqual(0,self.request('/api/connections?host='+urllib.parse.quote("x' OR 1=1--")+'&day=2026-11-09')['summary']['total'])
        for suffix in ['&day=bad','&gapDays=0','&gapDays=36501','&page=-1']:
            with self.assertRaises(urllib.error.HTTPError) as e:self.request('/api/connections?host=Rare-PC'+suffix)
            self.assertEqual(400,e.exception.code)
        with self.assertRaises(urllib.error.HTTPError) as e:self.request('/api/connections?day=2026-11-09')
        self.assertEqual(400,e.exception.code)
        self.assertEqual(2,self.request('/api/pc?host=Rare-PC&destination=203.0.113.20&from=2026-11-09&to=2026-11-09')['stats']['events'])
        self.assertEqual(self.original,hashlib.sha256(self.db.read_bytes()).hexdigest())

    def test_browser(self):
        from playwright.sync_api import sync_playwright, expect
        with sync_playwright() as p:
            browser=p.chromium.launch(headless=True,args=['--no-sandbox'],executable_path=os.environ.get('PLAYWRIGHT_CHROMIUM_EXECUTABLE') or shutil.which('chromium'))
            page=browser.new_page(viewport={'width':1440,'height':1000})
            errors=[]; page.on('pageerror',lambda e:errors.append(str(e)))
            page.goto(self.url)
            page.get_by_role('button',name='PC-01',exact=False).click()
            expect(page.locator("#resultCount")).to_have_text("121 matching events")
            expect(page.locator('#connectionSummary')).to_have_text('1 new · 0 returned')
            page.get_by_role('button',name='Show events for 203.0.113.1',exact=True).click()
            expect(page.locator('#destination')).to_have_value('203.0.113.1')

            page.get_by_role('button',name='Next',exact=True).click()
            expect(page.locator("#page")).to_have_text("Page 2 of 3")
            page.get_by_role('button',name='View event event-',exact=False).first.click()
            self.assertTrue(page.get_by_role('dialog').is_visible())
            self.assertIn('COLLECTOR_PEER_IP',page.locator('#eventFields').inner_text())
            page.get_by_role('button',name='Close event details').click()
            page.locator('#type').select_option('TCP_CONNECTED')
            page.get_by_role('button',name='Apply filters').click()
            expect(page.locator("#resultCount")).to_have_text("60 matching events")
            page.screenshot(path='/tmp/listener-db-gui.png',full_page=True)
            page.get_by_role('button',name='PC-02<script>',exact=False).click()
            expect(page.locator("#title")).to_have_text("PC-02<script>")
            self.assertEqual([],errors)
            page.set_viewport_size({'width':390,'height':844})
            self.assertTrue(page.locator('#pcSearch').is_visible())

            page.get_by_role('button',name='Rare-PC',exact=False).click()
            page.locator('#connectionDay').fill('2026-11-09')
            page.get_by_role('button',name='Check connections',exact=True).click()
            expect(page.locator('#connectionSummary')).to_have_text('57 new · 2 returned')
            page.get_by_role('button',name='Next connections',exact=True).click()
            expect(page.locator('#connectionPage')).to_have_text('Page 2 of 2')
            page.locator('#connectionGap').fill('60')
            page.get_by_role('button',name='Check connections',exact=True).click()
            expect(page.locator('#connectionSummary')).to_have_text('57 new · 0 returned')
            self.assertEqual([],errors)
            browser.close()

if __name__=='__main__': unittest.main(verbosity=2)
