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
        self.assertEqual(2,len(self.request('/api/pcs')))
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

    def test_browser(self):
        from playwright.sync_api import sync_playwright, expect
        with sync_playwright() as p:
            browser=p.chromium.launch(headless=True,args=['--no-sandbox'],executable_path=os.environ.get('PLAYWRIGHT_CHROMIUM_EXECUTABLE') or shutil.which('chromium'))
            page=browser.new_page(viewport={'width':1440,'height':1000})
            errors=[]; page.on('pageerror',lambda e:errors.append(str(e)))
            page.goto(self.url)
            page.get_by_role('button',name='PC-01',exact=False).click()
            expect(page.locator("#resultCount")).to_have_text("121 matching events")
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
            browser.close()

if __name__=='__main__': unittest.main(verbosity=2)
