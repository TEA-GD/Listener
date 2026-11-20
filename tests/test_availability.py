"""Heartbeat session accounting, calendar timelines and GUI navigation."""
from datetime import datetime,timedelta,timezone
import json,sqlite3,unittest,urllib.request,urllib.parse,urllib.error,shutil,uuid
import test_device_overview as fixtures

class AvailabilityTests(unittest.TestCase):
    setUp=fixtures.DeviceOverviewTests.setUp
    cleanup=fixtures.DeviceOverviewTests.cleanup
    add=fixtures.DeviceOverviewTests.add
    def heartbeat(self,host,stamp,received=None):
        stamp=stamp.isoformat() if isinstance(stamp,datetime) else stamp
        row=dict.fromkeys(fixtures.FIELDS,'');row.update(EVENT_ID=str(uuid.uuid4()),EVENT_TYPE='HEARTBEAT',TIMESTAMP_UTC=stamp,MONITORED_HOST=host,SNAPSHOT_ID='presence',COLLECTOR_PEER_IP='192.0.2.1')
        with sqlite3.connect(self.db) as db:db.execute('INSERT INTO events VALUES('+','.join('?' for _ in range(len(fixtures.FIELDS)+3))+')',[row[f] for f in fixtures.FIELDS]+[host,stamp[:10],received or stamp])
    def get(self,host='Live-PC',**params):
        with urllib.request.urlopen(self.url+'/api/availability?'+urllib.parse.urlencode({'host':host,**params})) as response:return json.load(response)
    def overview(self,host):
        with urllib.request.urlopen(self.url+'/api/device-overview?'+urllib.parse.urlencode({'host':host})) as response:return json.load(response)
    def test_current_online_offline_late_future_and_no_heartbeats(self):
        self.heartbeat('Online-PC',self.now-timedelta(seconds=180))
        self.heartbeat('Online-PC',self.now-timedelta(seconds=60)) # Exactly 120 seconds joins.
        self.heartbeat('Online-PC',self.now-timedelta(seconds=10))
        online=self.get('Online-PC')['summary'];self.assertEqual('online',online['status'])
        self.assertGreaterEqual(online['current_online_seconds'],180);self.assertLess(online['current_online_seconds'],195)
        self.assertEqual('online',self.overview('Online-PC')['availability']['status'])
        self.heartbeat('Late-PC',self.now-timedelta(hours=2),received=self.now.isoformat())
        late=self.get('Late-PC')['summary'];self.assertEqual('offline',late['status']);self.assertEqual(120,late['last_session_online_seconds'])
        self.heartbeat('Future-PC',self.now+timedelta(hours=2));self.assertEqual('unknown',self.get('Future-PC')['summary']['status'])
        self.add('TCP_CONNECTED',-5,'no-heartbeat',protocol='TCP',local='10.0.0.20',port='5000',peer='198.51.100.1',pid='1',host='No-HB-PC')
        self.assertEqual('unknown',self.get('No-HB-PC')['summary']['status'])
        self.assertEqual(['10.0.0.20'],self.overview('No-HB-PC')['device_ips'])
    def test_daily_sessions_cross_midnight_unknown_time_and_timezone(self):
        # Two sessions separated by a 121-second heartbeat gap.
        for stamp in ['2026-11-10T23:58:00Z','2026-11-10T23:59:00Z','2026-11-11T00:01:00Z','2026-11-11T00:03:01Z']:
            self.heartbeat('History-PC',stamp)
        data=self.get('History-PC',**{'from':'2026-11-10','to':'2026-11-11','tz':'UTC'})
        self.assertEqual([120,300],[d['online_seconds'] for d in data['days']])
        self.assertEqual(420,data['total_online_seconds'])
        segments=data['days'][0]['segments'];self.assertEqual('unknown',segments[0]['status'])
        nextday=data['days'][1]['segments'];self.assertTrue(any(s['status']=='offline' and s['duration_seconds']==1 for s in nextday))
        moscow=self.get('History-PC',**{'from':'2026-11-11','to':'2026-11-11','tz':'Europe/Moscow'})
        self.assertEqual(420,moscow['total_online_seconds'])
        periods=[s for s in moscow['days'][0]['segments'] if s['status']=='online'];self.assertIn('02:58:00 +03:00',periods[0]['start_local'])
        # Replays/duplicates never double the union duration.
        self.heartbeat('History-PC','2026-11-10T23:59:00Z')
        self.assertEqual(420,self.get('History-PC',**{'from':'2026-11-10','to':'2026-11-11','tz':'UTC'})['total_online_seconds'])
    def test_dst_calendar_lengths_and_validation(self):
        self.heartbeat('DST-PC','2026-03-01T00:00:00Z')
        spring=self.get('DST-PC',**{'from':'2026-03-29','to':'2026-03-29','tz':'Europe/Berlin'})
        autumn=self.get('DST-PC',**{'from':'2026-10-25','to':'2026-10-25','tz':'Europe/Berlin'})
        self.assertEqual(23*3600,spring['days'][0]['day_seconds']);self.assertEqual(25*3600,autumn['days'][0]['day_seconds'])
        for params in [{'tz':'not-a-zone'},{'from':'bad'},{'from':'2026-01-01','to':'2026-11-10'},{'from':'2026-11-11','to':'2026-11-10'},{'to':'2099-01-01'}]:
            with self.assertRaises(urllib.error.HTTPError) as e:self.get(**params)
            self.assertEqual(400,e.exception.code)
        with self.assertRaises(urllib.error.HTTPError) as e:self.get('missing')
        self.assertEqual(404,e.exception.code)
    def test_blue_online_red_offline_and_online_graph_page(self):
        from playwright.sync_api import sync_playwright,expect
        # A device with IP data and an expired heartbeat, plus online Live-PC fixture.
        self.add('ACTIVE_AT_START',-500,'old',protocol='TCP',local='10.0.0.30',port='5000',peer='198.51.100.1',pid='3',host='Offline-PC')
        self.heartbeat('Offline-PC',self.now-timedelta(seconds=400))
        with sync_playwright() as p:
            browser=p.chromium.launch(executable_path=shutil.which('chromium'),args=['--no-sandbox']);context=browser.new_context(timezone_id='Europe/Moscow');page=context.new_page()
            errors=[];page.on('pageerror',lambda e:errors.append(str(e)))
            page.goto(self.url);page.get_by_role('button',name='Live-PC',exact=False).click()
            expect(page.locator('#deviceIPs')).to_have_class('device-ip-online');expect(page.locator('#deviceIPs')).to_have_css('color','rgb(37, 99, 235)');expect(page.locator('#deviceOnlineStatus')).to_contain_text('Online for')
            page.get_by_role('link',name='Show online graph',exact=True).click()
            expect(page.locator('#onlineTitle')).to_have_text('Live-PC · Online history');expect(page.locator('#onlineState')).to_have_text('Online')
            expect(page.locator('#onlineTimezone')).to_have_value('Europe/Moscow');expect(page.locator('.online-day')).to_have_count(3)
            expect(page.locator('.availability-segment.online')).not_to_have_count(0)
            page.get_by_text('Show exact online periods',exact=True).click();expect(page.locator('#onlinePeriods')).to_contain_text('+03:00')
            page.get_by_role('link',name='Back to devices',exact=True).click();page.get_by_role('button',name='Offline-PC',exact=False).click()
            expect(page.locator('#deviceIPs')).to_have_class('device-ip-offline');expect(page.locator('#deviceIPs')).to_have_css('color','rgb(220, 38, 38)');expect(page.locator('#deviceIPs')).to_have_text('10.0.0.30');expect(page.locator('#deviceOnlineStatus')).to_contain_text('Last online period:')
            page.get_by_role('link',name='Show online graph',exact=True).click();expect(page.locator('#onlineState')).to_have_text('Offline')
            page.set_viewport_size({'width':390,'height':844});expect(page.locator('#onlineRefresh')).to_be_visible()
            self.assertEqual([],errors);browser.close()

if __name__=='__main__':unittest.main(verbosity=2)
