"""Buffered heartbeat colors, historical backfill and real offline agent replay."""
from datetime import datetime,timedelta,timezone
import json,os,pathlib,shutil,socket,sqlite3,subprocess,time,unittest,urllib.request,uuid
import test_availability as fixtures
from test_dashboard import ROOT

class BufferedAvailabilityTests(unittest.TestCase):
    setUp=fixtures.AvailabilityTests.setUp
    cleanup=fixtures.AvailabilityTests.cleanup
    add=fixtures.AvailabilityTests.add
    heartbeat=fixtures.AvailabilityTests.heartbeat
    get=fixtures.AvailabilityTests.get
    def test_late_reports_recolor_only_proven_working_periods(self):
        day='2026-11-10';host='Buffered-PC';params={'from':day,'to':day,'tz':'UTC'}
        self.heartbeat(host,day+'T10:00:00Z');self.heartbeat(host,day+'T10:03:00Z')
        before=self.get(host,**params)
        self.assertTrue(any(s['status']=='offline' and s['start_utc'].startswith(day+'T10:02:00') for s in before['days'][0]['segments']))
        for minute in ['01','02']:self.heartbeat(host,day+'T10:'+minute+':00Z',day+'T10:10:00Z')
        data=self.get(host,**params)
        self.assertEqual(300,data['total_online_seconds']);self.assertEqual(120,data['total_buffered_seconds']);self.assertEqual(180,data['total_connected_seconds'])
        self.assertEqual('offline',data['summary']['status'])
        orange=[s for s in data['days'][0]['segments'] if s['status']=='buffered'];self.assertEqual(120,sum(s['duration_seconds'] for s in orange))
        # A timely copy of the same stamp proves that stamp was not only delivered late.
        self.heartbeat(host,day+'T10:01:00Z',day+'T10:01:01Z')
        self.assertEqual(60,self.get(host,**params)['total_buffered_seconds'])
    def test_cross_midnight_threshold_and_unknown_receipt(self):
        self.heartbeat('Midnight','2026-11-10T23:59:00Z','2026-11-11T01:00:00Z')
        data=self.get('Midnight',**{'from':'2026-11-10','to':'2026-11-11','tz':'UTC'})
        self.assertEqual([60,60],[d['buffered_seconds'] for d in data['days']])
        self.heartbeat('Threshold','2026-11-10T12:00:00Z','2026-11-10T12:02:00Z')
        self.assertEqual(0,self.get('Threshold',**{'from':'2026-11-10','to':'2026-11-10'})['total_buffered_seconds'])
        self.heartbeat('Unknown-receipt','2026-11-10T12:00:00Z','invalid-receipt')
        self.assertEqual(0,self.get('Unknown-receipt',**{'from':'2026-11-10','to':'2026-11-10'})['total_buffered_seconds'])
    def test_browser_orange_legend_and_period_details(self):
        from playwright.sync_api import sync_playwright,expect
        self.heartbeat('Browser-buffer',self.now-timedelta(minutes=10),self.now.isoformat())
        with sync_playwright() as p:
            browser=p.chromium.launch(executable_path=shutil.which('chromium'),args=['--no-sandbox']);page=browser.new_page()
            page.goto(self.url+'/online.html?host=Browser-buffer')
            expect(page.locator('.availability-segment.buffered')).not_to_have_count(0)
            expect(page.locator('.availability-segment.buffered').first).to_have_css('fill','rgb(245, 158, 11)')
            expect(page.locator('#bufferedRangeTotal')).to_have_text('2m 0s')
            page.get_by_text('Show exact online periods',exact=True).click();expect(page.locator('#onlinePeriods')).to_contain_text('Buffered / delayed')
            self.assertEqual(5,page.locator('#onlinePeriods tr').first.locator('td').count());browser.close()
    def test_real_agent_retains_and_replays_offline_data(self):
        root=pathlib.Path(self.temp.name);repo=ROOT;dotnet=os.environ.get('DOTNET_HOST_PATH') or shutil.which('dotnet')
        self.heartbeat('Replay-PC',self.now-timedelta(seconds=660))
        spool=root/'spool';spool.mkdir();originals=[]
        for i,seconds in enumerate(range(600,0,-60)):
            row={'EVENT_ID':str(uuid.uuid4()),'EVENT_TYPE':'HEARTBEAT','TIMESTAMP_UTC':(self.now-timedelta(seconds=seconds)).isoformat(),'MONITORED_HOST':'Replay-PC','DEVICE_CATEGORY':'user_pcs','STATE':'SNAPSHOT_COMPLETE:0','SNAPSHOT_ID':str(i)}
            originals.append(row);(spool/f'000{i:03}.json').write_text(json.dumps(row))
        with socket.socket() as s:s.bind(('127.0.0.1',0));port=s.getsockname()[1]
        cfg=root/'agent.json';cfg.write_text(json.dumps({'agent_id':'Replay-PC','device_category':'user_pcs','collector_host':'localhost','collector_port':port,'spool_dir':str(spool)}))
        def run_agent():
            result=subprocess.run([dotnet,str(repo/'Agent/src/WatcherAgent/bin/Release/net10.0/linux-x64/WatcherAgent.dll'),'--config',str(cfg),'--once'],capture_output=True,text=True,timeout=40)
            self.assertEqual(0,result.returncode,result.stdout+result.stderr);return result
        run_agent();self.assertTrue((spool/'000000.json').exists(),'Offline delivery deleted evidence')
        collector=root/'collector.json';collector.write_text(json.dumps({'bind_host':'127.0.0.1','bind_port':port,'database_path':str(self.db),'csv_dir':str(root/'csv'),'log_file':str(root/'collector.log'),'csv_export_seconds':3600,'allowed_client_cidrs':[]}))
        with (root/'collector.stdout').open('w+') as log:
            process=subprocess.Popen([dotnet,str(repo/'tests/ServerAgentCollector/bin/Release/net10.0/ServerAgentCollector.dll'),'--config',str(collector)],stdout=log,stderr=log)
            try:
                for _ in range(100):
                    try:
                        with socket.create_connection(('127.0.0.1',port),timeout=.2):break
                    except OSError:time.sleep(.05)
                else:log.seek(0);self.fail(log.read())
                self.assertEqual(0,self.get('Replay-PC')['total_buffered_seconds']) # Populate the v2 history cache before replay.
                run_agent();self.assertFalse((spool/'000000.json').exists())
                with sqlite3.connect(self.db) as db:
                    stored=db.execute('SELECT TIMESTAMP_UTC FROM events WHERE EVENT_ID=?',(originals[0]['EVENT_ID'],)).fetchone()
                    self.assertEqual(originals[0]['TIMESTAMP_UTC'],stored[0])
                    self.assertEqual(2,db.execute('SELECT MAX(version) FROM gui_schema').fetchone()[0])
                graph=self.get('Replay-PC');self.assertGreater(graph['total_buffered_seconds'],0)
                # The receipt cache is populated above; a duplicate replay must not change durations.
                (spool/'000000.json').write_text(json.dumps(originals[0]));run_agent()
                with sqlite3.connect(self.db) as db:self.assertEqual(1,db.execute('SELECT count(*) FROM events WHERE EVENT_ID=?',(originals[0]['EVENT_ID'],)).fetchone()[0])
                self.assertEqual(graph['total_buffered_seconds'],self.get('Replay-PC')['total_buffered_seconds'])
            finally:
                process.terminate()
                try:process.wait(timeout=10)
                except subprocess.TimeoutExpired:process.kill();process.wait()

if __name__=='__main__':unittest.main()
