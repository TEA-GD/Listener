"""Three-day pagination, timezone-aligned network strips and the empty analysis destination."""
import json,sqlite3,shutil,unittest,urllib.request,urllib.parse
import test_availability as fixtures

class TimelineInterfaceTests(unittest.TestCase):
 setUp=fixtures.AvailabilityTests.setUp
 cleanup=fixtures.AvailabilityTests.cleanup
 add=fixtures.AvailabilityTests.add
 heartbeat=fixtures.AvailabilityTests.heartbeat
 get=fixtures.AvailabilityTests.get
 def prepare(self):
  self.heartbeat('Network-PC','2026-11-15T00:00:00Z')
  with sqlite3.connect(self.db) as db:
   db.executescript('''CREATE TABLE device_context_snapshots(host TEXT,snapshot_id TEXT,timestamp_utc TEXT,received_utc TEXT,status TEXT,network_count INTEGER,process_count INTEGER);
    CREATE TABLE device_network_communications(host TEXT,snapshot_id TEXT,network_type TEXT,network_name TEXT,adapter_name TEXT);
    CREATE TABLE device_process_snapshots(host TEXT,snapshot_id TEXT);''')
   for index,stamp,name in [(1,'2026-11-15T00:00:00Z','WIFI1'),(2,'2026-11-15T00:01:00Z','WIFI1'),(3,'2026-11-15T00:02:00Z','WIFI2'),(4,'2026-11-15T00:03:00Z',''),(5,'2026-11-15T20:59:00Z','NIGHT')]:
    db.execute('INSERT INTO device_context_snapshots VALUES(?,?,?,?,?,?,?)',('Network-PC',str(index),stamp,stamp,'ok',1 if name else 0,0))
    if name:db.execute('INSERT INTO device_network_communications VALUES(?,?,?,?,?)',('Network-PC',str(index),'wifi',name,'Wi-Fi'))
 def test_pagination_and_network_intervals(self):
  self.prepare()
  data=self.get('Network-PC',**{'from':'2026-11-14','to':'2026-11-20','tz':'Europe/Moscow','days':3,'page':1})
  self.assertEqual(3,len(data['days']));self.assertEqual(7,data['total_days']);self.assertEqual(3,data['pages'])
  day=next(day for day in data['days'] if day['date']=='2026-11-15');segments=day['network_segments']
  first=next(s for s in segments if 'WIFI1' in s['label']);self.assertEqual(10800,first['offset_seconds']);self.assertEqual(120,first['duration_seconds'])
  second=next(s for s in segments if 'WIFI2' in s['label']);self.assertEqual(60,second['duration_seconds'])
  self.assertFalse(any(s['duration_seconds']>120 for s in segments))
  nextday=next(d for d in data['days'] if d['date']=='2026-11-16');night=next(s for s in nextday['network_segments'] if 'NIGHT' in s['label']);self.assertEqual(0,night['offset_seconds']);self.assertEqual(60,night['duration_seconds'])
  page3=self.get('Network-PC',**{'from':'2026-11-14','to':'2026-11-20','tz':'Europe/Moscow','days':3,'page':3})
  self.assertEqual(['2026-11-20'],[d['date'] for d in page3['days']]);self.assertEqual(data['total_online_seconds'],page3['total_online_seconds'])
  larger=self.get('Network-PC',**{'from':'2026-11-14','to':'2026-11-20','days':7});self.assertEqual(7,len(larger['days']))
 def test_controls_and_analysis_button(self):
  self.prepare()
  from playwright.sync_api import sync_playwright,expect
  with sync_playwright() as p:
   browser=p.chromium.launch(executable_path=shutil.which('chromium'),args=['--no-sandbox']);page=browser.new_page();errors=[];page.on('pageerror',lambda e:errors.append(str(e)))
   page.goto(self.url+'/online.html?host=Network-PC');expect(page.locator('.online-day')).to_have_count(3)
   page.locator('#onlineFrom').fill('2026-11-14');page.locator('#onlineTo').fill('2026-11-20');page.locator('#onlineTimezone').fill('Europe/Moscow');page.get_by_role('button',name='Show timeline',exact=True).click()
   expect(page.locator('#timelinePage')).to_contain_text('Page 1 of 3');expect(page.locator('.network-segment.wifi')).not_to_have_count(0)
   expect(page.locator('.network-segment.wifi').first).to_have_attribute('aria-label',__import__('re').compile('WIFI1'))
   page.get_by_role('button',name='Next days').click();expect(page.locator('#timelinePage')).to_contain_text('Page 2 of 3');expect(page.locator('.online-day')).to_have_count(3)
   page.locator('#onlineDays').select_option('7');page.get_by_role('button',name='Show timeline',exact=True).click();expect(page.locator('.online-day')).to_have_count(7)
   page.goto(self.url);page.get_by_role('button',name='Network-PC',exact=False).click();button=page.get_by_role('link',name="It's time to analyze",exact=True)
   expect(button).to_have_attribute('href','analyze.html?host=Network-PC');button.click();self.assertEqual('',page.locator('body').inner_text());self.assertEqual([],errors);browser.close()

if __name__=='__main__':unittest.main()
