"""Frozen 60-second connection frames, pagination and GUI tab integration."""
import json,shutil,unittest,urllib.request,urllib.parse,urllib.error
import test_device_overview as fixtures

class LastMinuteTests(unittest.TestCase):
    setUp=fixtures.DeviceOverviewTests.setUp
    cleanup=fixtures.DeviceOverviewTests.cleanup
    add=fixtures.DeviceOverviewTests.add
    def get(self,host='Live-PC',at=None,watermark=None,page=1):
        query={'host':host,'page':page}
        if at is not None:query['at']=at
        if watermark is not None:query['watermark']=watermark
        with urllib.request.urlopen(self.url+'/api/last-minute?'+urllib.parse.urlencode(query)) as response:return json.load(response)
    def test_union_of_active_closed_partial_and_frozen_receipts(self):
        frame=self.get();self.assertEqual(4,frame['total']);self.assertEqual(2,frame['present']);self.assertEqual(1,frame['closed'])
        rows={r['SOURCE_PORT']:r for r in frame['connections']}
        self.assertEqual('Closed during minute',rows['50001']['window_status'])
        self.assertTrue(rows['50002']['present_at_latest']);self.assertTrue(rows['5353']['present_at_latest'])
        self.assertEqual('Observed; current state unknown',rows['50003']['window_status'])
        self.assertEqual('updating',frame['status'])
        # Even a late row with a backdated receipt cannot alter an already captured frame.
        self.add('TCP_CONNECTED',-5,'late',protocol='TCP',local='10.0.0.5',port='50004',peer='198.51.100.40',pid='4')
        frozen=self.get(at=frame['captured_utc'],watermark=frame['watermark'])
        self.assertEqual(frame['connections'],frozen['connections']);self.assertEqual(frame['captured_utc'],frozen['captured_utc'])
        self.assertEqual(5,self.get()['total'])
        self.add('HEARTBEAT',-1,'restart-empty',state='SNAPSHOT_COMPLETE:0')
        self.assertEqual(0,self.get()['present'])
    def test_long_running_connections_stale_legacy_and_minute_boundaries(self):
        self.add('TCP_SNAPSHOT',-80,'long',protocol='TCP',local='10.0.0.8',port='1111',peer='198.51.100.11',pid='1',host='Long-PC')
        self.add('HEARTBEAT',-79,'long',state='SNAPSHOT_COMPLETE:1',host='Long-PC')
        self.add('TCP_CONNECTED',-10,'poll-new',protocol='TCP',local='10.0.0.8',port='2222',peer='198.51.100.22',pid='2',host='Long-PC')
        frame=self.get('Long-PC');self.assertEqual(2,frame['total']);self.assertEqual(2,frame['present'])
        long=next(r for r in frame['connections'] if r['SOURCE_PORT']=='1111');self.assertEqual(0,long['observations_in_minute'])
        self.add('TCP_SNAPSHOT',-300,'stale',protocol='TCP',local='10.0.0.9',port='3333',peer='198.51.100.33',pid='3',host='Stale-PC')
        self.add('HEARTBEAT',-299,'stale',state='SNAPSHOT_COMPLETE:1',host='Stale-PC')
        stale=self.get('Stale-PC');self.assertEqual('stale',stale['status']);self.assertEqual(0,stale['total'])
        self.add('ACTIVE_AT_START',-5,'legacy',protocol='TCP',local='10.0.0.7',port='4444',peer='198.51.100.44',pid='4',host='Legacy-PC')
        legacy=self.get('Legacy-PC');self.assertEqual('snapshot_required',legacy['status']);self.assertEqual(1,legacy['total']);self.assertEqual(0,legacy['present'])
        self.add('TCP_CONNECTED',-60,'edge',protocol='TCP',local='10.0.0.6',port='6666',peer='198.51.100.66',pid='6',host='Edge-PC')
        self.add('TCP_CONNECTED',-61,'before-edge',protocol='TCP',local='10.0.0.6',port='7777',peer='198.51.100.77',pid='7',host='Edge-PC')
        self.add('HEARTBEAT',-1,'empty-edge',state='SNAPSHOT_COMPLETE:0',host='Edge-PC')
        edge=self.get('Edge-PC',at=self.now.isoformat());self.assertEqual({'6666'},{r['SOURCE_PORT'] for r in edge['connections']})
    def test_paging_stays_in_same_frame(self):
        for n in range(55):self.add('TCP_SNAPSHOT',-5,'many',protocol='TCP',local='10.0.0.9',port=str(50000+n),peer='198.51.100.1',pid='9',host='Many-PC')
        self.add('HEARTBEAT',-4,'many',state='SNAPSHOT_COMPLETE:55',host='Many-PC')
        first=self.get('Many-PC');self.assertEqual(50,len(first['connections']))
        self.add('TCP_CONNECTED',-1,'later',protocol='TCP',local='10.0.0.9',port='30000',peer='198.51.100.2',pid='9',host='Many-PC')
        second=self.get('Many-PC',at=first['captured_utc'],watermark=first['watermark'],page=2)
        self.assertEqual(55,second['total']);self.assertEqual(5,len(second['connections']))
        ports=[r['SOURCE_PORT'] for r in first['connections']+second['connections']]
        self.assertEqual(55,len(set(ports)));self.assertNotIn('30000',ports)
        self.assertEqual(56,self.get('Many-PC')['total'])
    def test_capture_parameters_are_validated(self):
        for suffix in ['&page=0','&at=not-a-date','&at=2099-01-01T00:00:00Z','&watermark=10','&at='+urllib.parse.quote(self.now.isoformat())+'&watermark=-1']:
            with self.assertRaises(urllib.error.HTTPError) as e:urllib.request.urlopen(self.url+'/api/last-minute?host=Live-PC'+suffix)
            self.assertEqual(400,e.exception.code)
        with self.assertRaises(urllib.error.HTTPError) as e:self.get('unknown')
        self.assertEqual(404,e.exception.code)

    def test_tab_capture_device_switch_and_keyboard_navigation(self):
        from playwright.sync_api import sync_playwright,expect
        self.add('ACTIVE_AT_START',-5,'legacy',protocol='TCP',local='10.0.0.7',port='4444',peer='198.51.100.44',pid='4',host='Legacy-PC')
        with sync_playwright() as p:
            browser=p.chromium.launch(executable_path=shutil.which('chromium'),args=['--no-sandbox']);page=browser.new_page(viewport={'width':1440,'height':1000})
            errors=[];page.on('pageerror',lambda e:errors.append(str(e)))
            page.goto(self.url);page.get_by_role('tab',name='Last minute',exact=True).click()
            expect(page.locator('#minutePanel')).to_be_visible();expect(page.locator('#minuteCapture')).to_be_disabled()
            page.get_by_role('button',name='Live-PC',exact=False).click()
            expect(page.locator('#minuteTotal')).to_have_text('4');expect(page.locator('#detail')).to_be_hidden()
            expect(page.locator('#minuteRows')).to_contain_text('Closed during minute');expect(page.locator('#minuteRows')).to_contain_text('2001:db8::5')
            captured=page.locator('#minuteTimes').text_content()
            self.add('TCP_CONNECTED',-5,'new',protocol='TCP',local='10.0.0.5',port='50004',peer='198.51.100.40',pid='4')
            page.get_by_role('button',name='Refresh data',exact=True).click()
            expect(page.locator('#minuteTotal')).to_have_text('4');expect(page.locator('#minuteTimes')).to_have_text(captured)
            page.get_by_role('button',name='Capture latest minute',exact=True).click();expect(page.locator('#minuteTotal')).to_have_text('5')
            page.get_by_role('tab',name='Last minute',exact=True).focus();page.keyboard.press('ArrowLeft')
            expect(page.locator('#detail')).to_be_visible();expect(page.locator('#minutePanel')).to_be_hidden()
            page.keyboard.press('ArrowRight');expect(page.locator('#minuteTotal')).to_have_text('5')
            page.get_by_role('button',name='Legacy-PC',exact=False).click();expect(page.locator('#minuteDevice')).to_have_text('Legacy-PC');expect(page.locator('#minuteTotal')).to_have_text('1')
            expect(page.locator('#minuteRows')).not_to_contain_text('2001:db8::5')
            self.assertEqual(0,page.locator('script:not([src])').count());self.assertEqual([],errors)
            page.set_viewport_size({'width':390,'height':844});expect(page.locator('#minuteCapture')).to_be_visible()
            browser.close()

if __name__=='__main__':unittest.main(verbosity=2)
