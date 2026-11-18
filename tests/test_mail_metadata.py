"""Metadata-only receiver and add-on integration; synthetic mail only."""
import functools
import http.server
import json
import os
from pathlib import Path
import shutil
import socket
import sqlite3
import subprocess
import tempfile
import threading
import time
import unittest
import urllib.error
import urllib.request
import uuid
import zipfile

ROOT = Path(__file__).resolve().parents[1]
PROJECT = 'Listener_MailReceiver_Metadata'

class MetadataTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.db = Path(self.temp.name) / 'metadata.db'
        self.token = 'synthetic-' + uuid.uuid4().hex
        with socket.socket() as sock:
            sock.bind(('127.0.0.1', 0)); port = sock.getsockname()[1]
        self.url = f'http://127.0.0.1:{port}'
        self.log = (Path(self.temp.name) / 'receiver.log').open('w+')
        dotnet = os.environ.get('DOTNET_HOST_PATH') or shutil.which('dotnet')
        self.process = subprocess.Popen([dotnet, str(ROOT / PROJECT / f'bin/Release/net10.0/{PROJECT}.dll'),
            '--urls', self.url, '--DatabasePath', str(self.db), '--InternalDomains', 'example.com',
            '--MaximumRequestBytes', '20000'], env={**os.environ, 'LISTENER_MAIL_TOKEN': self.token},
            stdout=self.log, stderr=subprocess.STDOUT)
        self.addCleanup(self.cleanup)
        for _ in range(100):
            if self.process.poll() is not None:
                self.log.seek(0); raise RuntimeError(self.log.read())
            try:
                urllib.request.urlopen(self.url + '/health', timeout=1).close(); break
            except (urllib.error.URLError, TimeoutError): time.sleep(.05)
        else: raise RuntimeError('Receiver startup timed out')
    def cleanup(self):
        self.process.terminate()
        try: self.process.wait(timeout=10)
        except subprocess.TimeoutExpired: self.process.kill(); self.process.wait()
        self.log.close(); self.temp.cleanup()
    def payload(self):
        return dict(alert_id=str(uuid.uuid4()), client_id=str(uuid.uuid4()), client_label='PC-test',
            account_id='work', message_key=uuid.uuid4().hex, sender='Outside <sender@outside.example>',
            recipients=['recipient@example.com'], has_attachments=True,
            attachment_names=['report.csv', 'Привет.txt'], message_date_utc='2026-11-18T10:00:00Z')
    def post(self, payload, token=None):
        request = urllib.request.Request(self.url + '/api/mail-metadata', data=json.dumps(payload).encode(),
            headers={'Authorization': 'Bearer ' + (self.token if token is None else token), 'Content-Type': 'application/json'})
        with urllib.request.urlopen(request, timeout=10) as response: return json.load(response)
    def rows(self):
        with sqlite3.connect(self.db) as db:
            db.row_factory = sqlite3.Row
            return [dict(r) for r in db.execute('SELECT * FROM email_metadata_alerts')]
    def test_storage_validation_auth_and_deduplication(self):
        sample = self.payload()
        with self.assertRaises(urllib.error.HTTPError) as e: self.post(sample, token='wrong')
        self.assertEqual(401, e.exception.code)
        self.assertEqual('stored', self.post(sample)['status'])
        self.assertEqual('duplicate', self.post(sample)['status'])
        row, = self.rows()
        self.assertEqual(sample['sender'], row['sender'])
        self.assertEqual(sample['recipients'], json.loads(row['recipients_json']))
        self.assertEqual(sample['attachment_names'], json.loads(row['attachment_names_json']))
        self.assertEqual(1, row['has_attachments'])
        self.assertNotIn('subject', row); self.assertNotIn('body_text', row)
        for forbidden in ['body_text', 'subject', 'text_parts', 'attachment_content']:
            mail = self.payload(); mail[forbidden] = 'MUST_NOT_STORE_THIS'
            with self.assertRaises(urllib.error.HTTPError) as e: self.post(mail)
            self.assertEqual(400, e.exception.code)
        for field, value in [('has_attachments', False), ('has_attachments', None), ('attachment_names', None), ('message_date_utc', 'bad')]:
            mail = self.payload(); mail[field] = value
            with self.assertRaises(urllib.error.HTTPError) as e: self.post(mail)
            self.assertEqual(400, e.exception.code)
        internal = self.payload(); internal['sender'] = 'Inside <inside@example.com>'
        self.assertEqual('ignored_internal', self.post(internal)['status'])
        empty = self.payload(); empty.update(has_attachments=False, attachment_names=[])
        self.assertEqual('stored', self.post(empty)['status'])
        self.assertEqual(2, len(self.rows()))
        with sqlite3.connect(self.db) as db:
            self.assertEqual(0, db.execute('SELECT COUNT(*) FROM email_alerts').fetchone()[0])
    def test_extension_metadata_queue_restart_and_no_body_apis(self):
        from playwright.sync_api import sync_playwright
        handler = functools.partial(http.server.SimpleHTTPRequestHandler, directory=str(ROOT / 'Thunderbird_Addon_Metadata/extension'))
        server = http.server.ThreadingHTTPServer(('127.0.0.1', 0), handler)
        thread = threading.Thread(target=server.serve_forever, daemon=True); thread.start()
        mock = r'''
        const initial={settings:{enabled:true,internalDomains:['example.com'],accountIds:['work'],clientLabel:'PC-test',receiverUrl:RECEIVER,receiverToken:TOKEN,includeSubdomains:true,allowHttp:false}};
        const saved=()=>JSON.parse(localStorage.getItem('mockStorage')||JSON.stringify(initial));
        window.listeners={}; window.bodyCalls=0; window.failAttachments=false;
        window.mailHeader=id=>({id,author:id===2?'Inside <inside@example.com>':'Outside <sender@outside.example>',recipients:['to@example.com'],ccList:['cc@example.com'],bccList:['bcc@example.com'],subject:'SECRET_SUBJECT_DO_NOT_COLLECT',date:'2026-11-18T10:00:00.000Z',headerMessageId:'message-'+id+'@outside.example',external:false,folder:{accountId:'work'}});
        window.browser={
            storage:{local:{get:async keys=>{const value=saved();return Object.fromEntries((Array.isArray(keys)?keys:[keys]).filter(k=>k in value).map(k=>[k,value[k]]));},set:async values=>localStorage.setItem('mockStorage',JSON.stringify({...saved(),...values}))},onChanged:{addListener:fn=>listeners.storage=fn}},
            runtime:{onMessage:{addListener:fn=>listeners.runtime=fn},openOptionsPage:async()=>{}},
            alarms:{create:()=>{},onAlarm:{addListener:fn=>listeners.alarm=fn}},browserAction:{onClicked:{addListener:()=>{}}},
            messengerUtilities:{parseMailboxString:async value=>[{email:value.match(/<([^>]+)>/)?.[1]||value}]},
            messages:{onNewMailReceived:{addListener:fn=>listeners.newMail=fn},get:async id=>mailHeader(id),query:async()=>({messages:[]}),abortList:async()=>{},continueList:async()=>({messages:[mailHeader(3)]}),
                listAttachments:async id=>{if(window.failAttachments)throw Error('Attachments unavailable');return id===3?[]:[{name:'report.csv'},{name:'Привет.txt'}];},
                getFull:async()=>{window.bodyCalls++;throw Error('Body API forbidden');},getRaw:async()=>{window.bodyCalls++;throw Error('Raw API forbidden');},getAttachmentFile:async()=>{window.bodyCalls++;throw Error('Attachment content API forbidden');}}
        };
        '''.replace('RECEIVER', json.dumps(self.url)).replace('TOKEN', json.dumps(self.token))
        offline = [True]
        try:
            with sync_playwright() as p:
                browser = p.chromium.launch(executable_path=shutil.which('chromium'), args=['--no-sandbox'])
                context = browser.new_context(); context.add_init_script(mock)
                requests = []
                def transport(route):
                    headers = {'access-control-allow-origin':'*','access-control-allow-headers':'authorization,content-type','access-control-allow-methods':'POST'}
                    if route.request.method == 'OPTIONS': route.fulfill(status=204, headers=headers); return
                    requests.append(json.loads(route.request.post_data))
                    if offline[0]: route.fulfill(status=503, headers=headers, body='{}'); return
                    result = route.fetch(); route.fulfill(response=result, headers={**result.headers, **headers})
                context.route(self.url + '/**', transport)
                page = context.new_page(); page.goto(f'http://127.0.0.1:{server.server_port}/options.html')
                page.add_script_tag(path=str(ROOT / 'Thunderbird_Addon_Metadata/extension/background.js'))
                page.wait_for_function("typeof listeners.newMail==='function'")
                page.evaluate("listeners.newMail({accountId:'work',type:'inbox'},{messages:[mailHeader(1),mailHeader(2)],id:'next-page'})")
                page.wait_for_function("JSON.parse(localStorage.getItem('mockStorage')).lastError?.includes('503')")
                queued = page.evaluate("async()=>JSON.stringify(await store('readonly',s=>s.getAll()))")
                self.assertNotIn('SECRET_SUBJECT', queued); self.assertNotIn('body_text', queued)
                self.assertEqual(0, page.evaluate('bodyCalls'))
                page.reload(); page.add_script_tag(path=str(ROOT / 'Thunderbird_Addon_Metadata/extension/background.js'))
                page.wait_for_function("typeof listeners.runtime==='function'")
                offline[0] = False; page.evaluate("listeners.runtime({type:'retry'})")
                page.wait_for_function("JSON.parse(localStorage.getItem('mockStorage')).pending===0")
                rows = self.rows(); self.assertEqual(2, len(rows))
                self.assertEqual([0, 1], sorted(row['has_attachments'] for row in rows))
                self.assertEqual(['to@example.com','cc@example.com','bcc@example.com'], json.loads(rows[0]['recipients_json']))
                for payload in requests:
                    self.assertEqual(set(self.payload()), set(payload))
                    self.assertNotIn('SECRET_SUBJECT', json.dumps(payload))
                # Attachment lookup errors must not be reported as "no attachments".
                page.evaluate("window.failAttachments=true;listeners.newMail({accountId:'work',type:'inbox'},{messages:[mailHeader(4)]})")
                page.wait_for_function("JSON.parse(localStorage.getItem('mockStorage')).lastError?.includes('Attachments unavailable')")
                self.assertEqual(2, len(self.rows()))
                page.evaluate("window.failAttachments=false;listeners.runtime({type:'retry'})")
                page.wait_for_function("JSON.parse(localStorage.getItem('mockStorage')).pending===0")
                self.assertEqual(3, len(self.rows())); self.assertEqual(0, page.evaluate('bodyCalls'))
                browser.close()
        finally: server.shutdown(); server.server_close(); thread.join(timeout=5)

class PackageTests(unittest.TestCase):
    def test_packaged_source_matches_and_contains_no_body_extraction(self):
        extension = ROOT / 'Thunderbird_Addon_Metadata/extension'
        version = json.loads((extension / 'manifest.json').read_text())['version']
        with zipfile.ZipFile(ROOT / f'Thunderbird_Addon_Metadata/packages/listener-mail-metadata-{version}.xpi') as archive:
            for path in extension.iterdir(): self.assertEqual(path.read_bytes(), archive.read(path.name))
        script = (extension / 'background.js').read_text()
        for api in ['getFull(', 'getRaw(', 'getAttachmentFile(', 'body_text:', 'text_parts:', 'subject:']:
            self.assertNotIn(api, script)
        manifest = json.loads((extension / 'manifest.json').read_text())
        self.assertEqual('listener-mail-metadata@tea-gd.local', manifest['browser_specific_settings']['gecko']['id'])

if __name__ == '__main__': unittest.main(verbosity=2)
