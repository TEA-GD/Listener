"""Mail receiver + dashboard integration; all messages and keys are synthetic."""
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

ROOT=Path(__file__).resolve().parents[1]
FIELDS='EVENT_ID EVENT_TYPE TIMESTAMP_UTC DATE_AND_TIME SOURCE_IP SOURCE_DOMAIN_NAME PROCESS_NAME PROCESS_ID RECIEVER_IP RECIEVER_DOMAINNAME PORT_USED SOFTWARE_THAT_USED SOURCE_PORT PROTOCOL STATE OPERATING_SYSTEM MONITORED_HOST SNAPSHOT_ID COLLECTOR_PEER_IP'.split()

class MailTests(unittest.TestCase):
    def setUp(self):
        self.temp=tempfile.TemporaryDirectory(prefix='listener-mail-');self.root=Path(self.temp.name);self.db=self.root/'events.db';self.processes=[]
        with sqlite3.connect(self.db) as db:
            db.execute('CREATE TABLE events('+','.join(f'[{f}] TEXT' for f in FIELDS)+',host TEXT,date_utc TEXT,received_utc TEXT)')
            row=dict.fromkeys(FIELDS,'');row.update(EVENT_ID=str(uuid.uuid4()),EVENT_TYPE='HEARTBEAT',TIMESTAMP_UTC='2026-11-17T09:00:00Z',MONITORED_HOST='PC-test')
            db.execute('INSERT INTO events VALUES('+','.join('?' for _ in range(len(FIELDS)+3))+')',[row[f] for f in FIELDS]+['PC-test','2026-11-17','2026-11-17T09:00:01Z'])
        self.token='synthetic-'+uuid.uuid4().hex+uuid.uuid4().hex
        self.receiver=self.start('Listener_MailReceiver',['--DatabasePath',str(self.db),'--InternalDomains','example.com','--MaximumRequestBytes','20000'],{'LISTENER_MAIL_TOKEN':self.token},'/health')
        self.view_token=self.token+'-view'
        self.gui=self.start('Listener_DB_GUI',[],{'WATCHER_DB_PATH':str(self.db),'LISTENER_MAIL_VIEW_TOKEN':self.view_token},'/')
    def tearDown(self):
        for process,log in self.processes:
            process.terminate()
            try:process.wait(timeout=10)
            except subprocess.TimeoutExpired:process.kill();process.wait()
            log.close()
        self.temp.cleanup()
    def start(self,project,arguments,variables,path):
        with socket.socket() as s:s.bind(('127.0.0.1',0));port=s.getsockname()[1]
        url=f'http://127.0.0.1:{port}';log=(self.root/f'{project}.log').open('w+')
        exe=os.environ.get('DOTNET_HOST_PATH') or shutil.which('dotnet')
        process=subprocess.Popen([exe,str(ROOT/project/'bin/Release/net10.0'/f'{project}.dll'),'--urls',url,*arguments],env=dict(os.environ,**variables),stdout=log,stderr=subprocess.STDOUT)
        self.processes.append((process,log))
        for _ in range(100):
            if process.poll() is not None:log.seek(0);raise RuntimeError(log.read())
            try:urllib.request.urlopen(url+path,timeout=1).close();return url
            except (urllib.error.URLError,TimeoutError):time.sleep(.05)
        raise RuntimeError('Service startup timeout')
    def sample(self,sender='Outside <sender@outside.example>'):
        return {'alert_id':str(uuid.uuid4()),'client_id':str(uuid.uuid4()),'client_label':'PC-mail','account_id':'account1','message_key':uuid.uuid4().hex,'message_id':'mail@example.test','sender':sender,'recipients':['User <user@example.com>'],'cc':['Audit <audit@example.com>'],'bcc':[],'mailbox_addresses':['user@example.com'],'delivered_to':['user@example.com'],'subject':'External synthetic message','body_text':'Full text: Привет 🌍\nSecond line <script>window.mailXss=true</script>','text_parts':[{'content_type':'text/html','text':'<img src="https://never-load.invalid/a" onerror="window.mailXss=true"><script>window.mailXss=true</script>','part_name':'1.1'}],'attachment_names':['report.docx','notes.txt'],'body_status':'available','message_date_utc':'2026-11-17T09:00:00Z'}
    def post(self,payload,token=None):
        request=urllib.request.Request(self.receiver+'/api/mail-alerts',data=json.dumps(payload).encode(),headers={'Content-Type':'application/json','Authorization':'Bearer '+(self.token if token is None else token)})
        with urllib.request.urlopen(request) as response:return json.load(response)
    def get(self,path):
        with urllib.request.urlopen(urllib.request.Request(self.gui+path,headers={'Authorization':'Bearer '+self.view_token})) as response:return json.load(response)
    def test_storage_auth_domain_boundaries_and_deduplication(self):
        with self.assertRaises(urllib.error.HTTPError) as denied:urllib.request.urlopen(self.gui+'/api/mail')
        self.assertEqual(401,denied.exception.code)
        with self.assertRaises(urllib.error.HTTPError) as e:self.post(self.sample(),token='incorrect')
        self.assertEqual(401,e.exception.code)
        for sender in ['Internal <user@example.com>','USER@DEPT.EXAMPLE.COM']:
            self.assertEqual('ignored_internal',self.post(self.sample(sender))['status'])
        mail=self.sample('CEO@example.com <attacker@example.com.evil.test>')
        result=self.post(mail);self.assertEqual('stored',result['status']);self.assertEqual(mail['message_key'],result['message_key'])
        self.assertEqual('duplicate',self.post(mail)['status'])
        listing=self.get('/api/mail');self.assertEqual(1,listing['total'])
        details=self.get('/api/mail/'+mail['alert_id']);self.assertEqual(mail['body_text'],details['body_text'])
        self.assertEqual(mail['attachment_names'],json.loads(details['attachment_names_json']))
        self.assertEqual('example.com.evil.test',details['sender_domain'])
        self.assertEqual(mail['recipients'],json.loads(details['recipients_json']))
        malformed=self.sample('malformed sender')
        self.assertEqual('stored',self.post(malformed)['status'])
        self.assertEqual('',self.get('/api/mail/'+malformed['alert_id'])['sender_domain'])
        with sqlite3.connect(self.db) as db:self.assertEqual(1,db.execute('SELECT COUNT(*) FROM events').fetchone()[0])
    def test_shared_schema_metadata_dashboard_and_receiver_restart(self):
        from playwright.sync_api import sync_playwright,expect
        full=self.sample();self.post(full)
        metadata_url=self.start('Listener_MailReceiver_Metadata',['--DatabasePath',str(self.db),'--InternalDomains','example.com'],{'LISTENER_MAIL_TOKEN':self.token},'/health')
        metadata={'alert_id':str(uuid.uuid4()),'client_id':str(uuid.uuid4()),'client_label':'PC-metadata','account_id':'work','message_key':uuid.uuid4().hex,'sender':'Outside <sender@outside.test>','recipients':['to@example.com','cc@example.com'],'has_attachments':True,'attachment_names':['report-Привет.txt','<img src=x onerror=alert(1)>.txt'],'message_date_utc':'2026-11-18T10:00:00Z'}
        request=urllib.request.Request(metadata_url+'/api/mail-metadata',data=json.dumps(metadata).encode(),headers={'Content-Type':'application/json','Authorization':'Bearer '+self.token})
        with urllib.request.urlopen(request) as response:self.assertEqual('stored',json.load(response)['status'])
        self.assertEqual(1,self.get('/api/mail?mode=full')['total'])
        self.assertEqual(1,self.get('/api/mail?mode=metadata')['total'])
        self.assertEqual(1,self.get('/api/mail?mode=metadata&q=report')['total'])
        details=self.get('/api/mail/'+metadata['alert_id']+'?mode=metadata')
        self.assertEqual(1,details['has_attachments']);self.assertNotIn('body_text',details);self.assertNotIn('subject',details)
        with self.assertRaises(urllib.error.HTTPError) as e:self.get('/api/mail/'+full['alert_id']+'?mode=metadata')
        self.assertEqual(404,e.exception.code)
        with self.assertRaises(urllib.error.HTTPError) as e:self.get('/api/mail?mode=invalid')
        self.assertEqual(400,e.exception.code)
        with self.assertRaises(urllib.error.HTTPError) as e:urllib.request.urlopen(self.gui+'/api/mail?mode=metadata')
        self.assertEqual(401,e.exception.code)
        # Reinitializing either receiver must preserve rows in both mail tables and TCP events.
        self.start('Listener_MailReceiver',['--DatabasePath',str(self.db),'--InternalDomains','example.com'],{'LISTENER_MAIL_TOKEN':self.token},'/health')
        self.assertEqual(1,self.get('/api/mail?mode=metadata')['total']);self.assertEqual(1,self.get('/api/mail')['total'])
        with sync_playwright() as p:
            browser=p.chromium.launch(headless=True,executable_path=shutil.which('chromium'),args=['--no-sandbox'])
            page=browser.new_page();page.goto(self.gui+'/mail.html');page.locator('#mailKey').fill(self.view_token)
            page.get_by_role('button',name='Load / search mail').click()
            page.get_by_role('button',name='Read message '+full['subject'],exact=True).click()
            expect(page.locator('#mailBody')).to_have_text(full['body_text'])
            page.locator('#mailMode').select_option('metadata')
            page.get_by_role('button',name='View metadata '+metadata['sender'],exact=True).click()
            expect(page.locator('#mailFields')).to_contain_text('report-Привет.txt')
            expect(page.locator('#mailFields')).to_contain_text('Has attachments')
            expect(page.locator('#mailTextSection')).to_be_hidden()
            self.assertEqual('',page.locator('#mailBody').text_content())
            self.assertEqual(0,page.locator('img').count())
            browser.close()
        # A mail receiver can create its DB before the Collector exists, and the viewer can read mail there.
        fresh=self.root/'mail-first.db'
        self.start('Listener_MailReceiver',['--DatabasePath',str(fresh),'--InternalDomains','example.com'],{'LISTENER_MAIL_TOKEN':self.token},'/health')
        viewer=self.start('Listener_DB_GUI',[],{'WATCHER_DB_PATH':str(fresh),'LISTENER_MAIL_VIEW_TOKEN':self.view_token},'/')
        with urllib.request.urlopen(urllib.request.Request(viewer+'/api/mail?mode=metadata',headers={'Authorization':'Bearer '+self.view_token})) as response:
            self.assertTrue(json.load(response)['available'])
        with sqlite3.connect(fresh) as db:
            self.assertEqual(2,db.execute("SELECT COUNT(*) FROM sqlite_master WHERE name IN ('email_alerts','email_metadata_alerts')").fetchone()[0])

    def test_invalid_and_oversized_requests_and_cors(self):
        mail=self.sample();mail['recipients']=None
        with self.assertRaises(urllib.error.HTTPError) as e:self.post(mail)
        self.assertEqual(400,e.exception.code)
        mail=self.sample();mail['body_text']='x'*30000
        with self.assertRaises(urllib.error.HTTPError) as e:self.post(mail)
        self.assertEqual(413,e.exception.code)
        request=urllib.request.Request(self.receiver+'/api/mail-alerts',method='OPTIONS',headers={'Origin':'moz-extension://synthetic-addon','Access-Control-Request-Method':'POST','Access-Control-Request-Headers':'authorization,content-type'})
        with urllib.request.urlopen(request) as response:self.assertEqual('moz-extension://synthetic-addon',response.headers['Access-Control-Allow-Origin'])
        self.assertEqual(0,self.get('/api/mail')['total'])
    def test_mail_dashboard_safe_text_rendering(self):
        from playwright.sync_api import sync_playwright,expect
        mail=self.sample();self.post(mail)
        with sync_playwright() as p:
            browser=p.chromium.launch(headless=True,executable_path=shutil.which('chromium'),args=['--no-sandbox'])
            page=browser.new_page();errors=[];page.on('pageerror',lambda e:errors.append(str(e)))
            page.goto(self.gui+'/mail.html');page.locator('#mailKey').fill(self.view_token);page.get_by_role('button',name='Load / search mail').click();page.get_by_role('button',name='Read message '+mail['subject'],exact=True).click()
            expect(page.locator('#mailBody')).to_have_text(mail['body_text'])
            expect(page.locator('#mailFields')).to_contain_text('report.docx')
            self.assertEqual(0,page.locator('img').count());self.assertIsNone(page.evaluate('window.mailXss'))
            page.locator('summary').click();expect(page.locator('#mailParts')).to_contain_text('<img src=')
            self.assertEqual([],errors);browser.close()
    def test_extension_options_capture_queue_retry_and_restart(self):
        from playwright.sync_api import sync_playwright,expect
        server=http.server.ThreadingHTTPServer(('127.0.0.1',0),functools.partial(http.server.SimpleHTTPRequestHandler,directory=str(ROOT/'Thunderbird_Addon/extension')))
        thread=threading.Thread(target=server.serve_forever,daemon=True);thread.start();url=f'http://127.0.0.1:{server.server_port}'
        mock=r'''
        const initial={settings:{enabled:false,internalDomains:[],accountIds:[],clientLabel:'',receiverUrl:RECEIVER,receiverToken:'',includeSubdomains:true,allowHttp:false}};
        const saved=()=>JSON.parse(localStorage.getItem('mockStorage')||JSON.stringify(initial));
        window.fullCalls=[];window.mailListeners={};
        window.mailHeader=id=>({id,author:id===2?'Inside <inside@example.com>':'Outside <sender@outside.example>',recipients:['User <user@example.com>'],ccList:['Audit <audit@example.com>'],bccList:[],subject:'Queued '+id,date:'2026-11-17T10:00:00.000Z',headerMessageId:'message-'+id+'@outside.example',external:false,headersOnly:false,folder:{accountId:'account1'}});
        window.browser={
            storage:{local:{get:async keys=>{const value=saved();return Object.fromEntries((Array.isArray(keys)?keys:[keys]).filter(k=>k in value).map(k=>[k,value[k]]));},set:async values=>{const before=saved();localStorage.setItem('mockStorage',JSON.stringify({...before,...values}));if(values.settings)window.mailListeners.storage?.({settings:{newValue:values.settings}});}},onChanged:{addListener:fn=>window.mailListeners.storage=fn}},
            runtime:{sendMessage:async m=>{if(!window.mailListeners.runtime)throw new Error('Background not started');return window.mailListeners.runtime(m);},onMessage:{addListener:fn=>window.mailListeners.runtime=fn},openOptionsPage:async()=>{}},
            permissions:{request:async()=>true},alarms:{create:()=>{},onAlarm:{addListener:fn=>window.mailListeners.alarm=fn}},browserAction:{onClicked:{addListener:()=>{}}},
            accounts:{list:async()=>[{id:'account1',name:'Work',identities:[{email:'user@example.com'}]}],get:async()=>({identities:[{email:'user@example.com'}]})},
            messengerUtilities:{parseMailboxString:async value=>[{email:value.match(/<([^>]+)>/)?.[1]||value}]},
            messages:{onNewMailReceived:{addListener:fn=>window.mailListeners.newMail=fn},get:async id=>window.mailHeader(id),query:async()=>({messages:[]}),abortList:async()=>{},continueList:async()=>({messages:[window.mailHeader(3)]}),
                listAttachments:async()=>[{name:'notes.txt',partName:'1.2'},{name:'attached.eml',partName:'1.3'}],
                getFull:async id=>{window.fullCalls.push(id);return {contentType:'message/rfc822',headers:{'delivered-to':['user@example.com']},parts:[{contentType:'multipart/alternative',parts:[{contentType:'text/plain',partName:'1.1.1',body:'Full Unicode: Привет 🌍\nSecond line'},{contentType:'text/html',partName:'1.1.2',body:'<p>Full Unicode</p><img src="https://never-load.invalid/mail"><script>window.mailXss=true</script>'}]},{name:'notes.txt',partName:'1.2',contentType:'text/plain',body:'DO NOT SEND ATTACHMENT CONTENT'},{name:'attached.eml',partName:'1.3',contentType:'message/rfc822',parts:[{contentType:'text/plain',body:'DO NOT SEND ATTACHED MESSAGE BODY'}]}]};}}
        };
        '''.replace('RECEIVER',json.dumps(self.receiver))
        offline={'value':False}
        try:
            with sync_playwright() as p:
                browser=p.chromium.launch(headless=True,executable_path=shutil.which('chromium'),args=['--no-sandbox']);context=browser.new_context();context.add_init_script(mock)
                def transport(route):
                    headers={'access-control-allow-origin':'*','access-control-allow-headers':'authorization,content-type','access-control-allow-methods':'POST'}
                    if route.request.method=='OPTIONS':route.fulfill(status=204,headers=headers);return
                    if offline['value']:route.fulfill(status=503,headers=headers,body='{}');return
                    result=route.fetch();route.fulfill(response=result,headers={**result.headers,**headers})
                context.route(self.receiver+'/**',transport)
                page=context.new_page();errors=[];requests=[];page.on('pageerror',lambda e:errors.append(str(e)));page.on('request',lambda r:requests.append(r.url));page.goto(url+'/options.html')
                page.locator('#domains').fill('example.com');page.locator('#clientLabel').fill('PC-Thunderbird');page.locator('#token').fill(self.token)
                page.locator('#accounts input').check();page.locator('#enabled').check();page.get_by_role('button',name='Save settings and grant receiver access').click()
                expect(page.locator('#status')).to_contain_text('Settings saved')
                page.add_script_tag(url=url+'/background.js')
                page.wait_for_function("typeof window.mailListeners.newMail==='function'")
                page.evaluate("window.mailListeners.newMail({accountId:'account1',type:'inbox'},{messages:[window.mailHeader(1),window.mailHeader(2)],id:'page2'})")
                for _ in range(100):
                    if self.get('/api/mail')['total']==2:break
                    page.wait_for_timeout(50)
                self.assertEqual(2,self.get('/api/mail')['total'])
                self.assertNotIn(2,page.evaluate('window.fullCalls'),'Internal message body was read')
                first=self.get('/api/mail')['alerts'][0];body=self.get('/api/mail/'+first['alert_id'])
                self.assertIn('Привет 🌍',body['body_text']);self.assertNotIn('DO NOT SEND',body['body_text']);self.assertNotIn('DO NOT SEND',body['text_parts_json'])
                self.assertEqual(['notes.txt','attached.eml'],json.loads(body['attachment_names_json']))
                page.evaluate("window.mailListeners.newMail({accountId:'account1',type:'inbox'},{messages:[window.mailHeader(1)]})")
                page.wait_for_timeout(200);self.assertEqual(2,self.get('/api/mail')['total'])
                offline['value']=True
                page.evaluate("window.mailListeners.newMail({accountId:'account1',type:'inbox'},{messages:[window.mailHeader(4)]})")
                for _ in range(100):
                    status=page.evaluate("browser.storage.local.get('lastError')")
                    if '503' in status.get('lastError',''):break
                    page.wait_for_timeout(50)
                self.assertIn('503',status.get('lastError',''))
                page.reload();page.add_script_tag(url=url+'/background.js');page.wait_for_function("typeof window.mailListeners.runtime==='function'")
                offline['value']=False;page.get_by_role('button',name='Retry pending alerts').click()
                for _ in range(100):
                    if self.get('/api/mail')['total']==3:break
                    page.wait_for_timeout(50)
                self.assertEqual(3,self.get('/api/mail')['total']);self.assertEqual([],page.evaluate('window.fullCalls'),'Queued body was not preserved over restart')
                page.locator('#enabled').uncheck();page.get_by_role('button',name='Save settings and grant receiver access').click()
                page.evaluate("window.mailListeners.newMail({accountId:'account1',type:'inbox'},{messages:[window.mailHeader(5)]})")
                page.wait_for_timeout(200);self.assertEqual(3,self.get('/api/mail')['total'])
                self.assertEqual([],errors);self.assertFalse(any('never-load.invalid' in request for request in requests));browser.close()
        finally:server.shutdown();server.server_close();thread.join(timeout=5)

if __name__=='__main__':unittest.main(verbosity=2)
