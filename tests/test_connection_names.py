"""Name columns and asynchronous annotations with deterministic DNS responses."""
import json,sqlite3,shutil,unittest,urllib.parse,urllib.request,urllib.error
import test_device_overview as fixtures

class ConnectionNameTests(fixtures.DeviceOverviewTests):
    def test_name_columns_reported_precedence_and_async_dns(self):
        from playwright.sync_api import sync_playwright,expect
        self.add('ACTIVE_AT_START',-21,'names',protocol='TCP',local='10.0.0.25',port='5001',peer='203.0.113.1',pid='1',host='DNS-PC')
        self.add('TCP_SNAPSHOT',-20,'names',protocol='TCP',local='10.0.0.25',port='5001',peer='203.0.113.1',pid='1',host='DNS-PC')
        self.add('HEARTBEAT',-19,'names',state='SNAPSHOT_COMPLETE:1',host='DNS-PC')
        with sqlite3.connect(self.db) as db:
            db.execute("UPDATE events SET SOURCE_DOMAIN_NAME='pc.example.local',RECIEVER_DOMAINNAME='reported.example.local' WHERE host='DNS-PC'")
        self.add('TCP_CONNECTED',-10,'next',protocol='TCP',local='10.0.0.25',port='5002',peer='203.0.113.2',pid='2',host='DNS-PC')
        self.add('TCP_CONNECTED',-5,'next2',protocol='TCP',local='10.0.0.25',port='5003',peer='203.0.113.3',pid='3',host='DNS-PC')
        with sync_playwright() as p:
            browser=p.chromium.launch(executable_path=shutil.which('chromium'),args=['--no-sandbox']);page=browser.new_page();lookups=[];errors=[]
            page.on('pageerror',lambda error:errors.append(str(error)))
            def resolve(route):
                ips=urllib.parse.parse_qs(urllib.parse.urlsplit(route.request.url).query)['ip'];lookups.extend(ips)
                route.fulfill(json={'names':{ip:{'name':'ptr.example.local' if ip=='203.0.113.2' else None,'status':'resolved' if ip=='203.0.113.2' else 'unavailable'} for ip in ips}})
            page.route('**/api/connection-names?*',resolve)
            page.goto(self.url);page.get_by_role('button',name='DNS-PC',exact=False).click()
            expect(page.locator('#activeRows')).to_contain_text('pc.example.local');expect(page.locator('#activeRows')).to_contain_text('reported.example.local')
            expect(page.locator('#activeRows')).to_contain_text('ptr.example.local');expect(page.locator('#events')).to_contain_text('reported.example.local')
            self.assertNotIn('203.0.113.1',lookups)
            self.assertEqual(10,page.locator('#activeRows tr').first.locator('td').count())
            self.assertEqual(12,page.locator('#events tr').first.locator('td').count())
            unknown=page.locator('#activeRows tr').filter(has_text='203.0.113.3');expect(unknown.locator('td').nth(7)).to_have_text('—')
            page.get_by_role('button',name='Check connections',exact=True).click()
            expect(page.locator('#connections')).to_contain_text('DNS-PC');expect(page.locator('#connections')).to_contain_text('ptr.example.local');expect(page.locator('#connections')).to_contain_text('reported.example.local')
            page.get_by_role('tab',name='Last minute',exact=True).click()
            expect(page.locator('#minuteRows')).to_contain_text('reported.example.local');expect(page.locator('#minuteRows')).to_contain_text('ptr.example.local')
            self.assertEqual(11,page.locator('#minuteRows tr').first.locator('td').count());self.assertEqual([],errors)
            browser.close()
    def test_name_api_validation_and_non_dns_addresses(self):
        def get(params):
            with urllib.request.urlopen(self.url+'/api/connection-names?'+urllib.parse.urlencode(params,doseq=True)) as response:return json.load(response)
        self.assertEqual('localhost',get({'ip':['127.0.0.1']})['names']['127.0.0.1']['name'])
        self.assertEqual('unavailable',get({'ip':['0.0.0.0']})['names']['0.0.0.0']['status'])
        for params in [{},{'ip':['not an IP']},{'ip':['127.0.0.1']*101}]:
            with self.assertRaises(urllib.error.HTTPError) as error:get(params)
            self.assertEqual(400,error.exception.code)

if __name__=='__main__':unittest.main()
