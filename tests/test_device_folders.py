"""Expandable category menu, searching, selection and refresh without changing event history."""
import shutil,unittest
import test_device_overview as fixtures

class DeviceFolderTests(fixtures.DeviceOverviewTests):
    def test_expand_search_select_and_refresh(self):
        from playwright.sync_api import sync_playwright,expect
        devices=[{'host':host,'category':category,'events':10,'processes':1,'lastReceived':self.now.isoformat(),'lastEvent':self.now.isoformat()}
            for host,category in [('Win-One','windows_servers'),('Linux-One','linux_servers'),('Live-PC','user_pcs'),('Stand-One','project_stands'),('Legacy-PC','legacy')]]
        with sync_playwright() as p:
            browser=p.chromium.launch(executable_path=shutil.which('chromium'),args=['--no-sandbox']);page=browser.new_page();errors=[]
            page.on('pageerror',lambda error:errors.append(str(error)))
            page.route('**/api/pcs',lambda route:route.fulfill(json=devices))
            page.goto(self.url)
            for category,label in [('windows_servers','Win Server'),('linux_servers','Linux Server'),('user_pcs','User PCs'),('project_stands','Project Stand')]:
                folder=page.locator(f'details[data-category="{category}"]')
                expect(folder.locator('summary')).to_contain_text(label)
                self.assertFalse(folder.evaluate('(node)=>node.open'))
                folder.locator('summary').click();self.assertTrue(folder.evaluate('(node)=>node.open'))
                expect(folder.locator('.pc')).to_have_count(1)
            expect(page.locator('#pcCount')).to_have_text('5')
            expect(page.locator('details[data-category="legacy"] .pc')).to_be_visible()
            page.locator('#pcSearch').fill('Linux-One')
            expect(page.locator('details[data-category="linux_servers"] .pc')).to_be_visible()
            expect(page.locator('#pcs .pc')).to_have_count(1)
            page.locator('#pcSearch').fill('no-such-device');expect(page.locator('#pcs')).to_contain_text('No matching PCs.')
            page.locator('#pcSearch').fill('')
            page.get_by_role('button',name='Live-PC',exact=False).click()
            expect(page.locator('#title')).to_have_text('Live-PC')
            expect(page.locator('#deviceIPs')).to_have_text('10.0.0.5')
            expect(page.locator('details[data-category="user_pcs"]')).to_have_class('device-folder selected-folder')
            devices[2]['category']='windows_servers'
            page.get_by_role('button',name='Refresh data',exact=True).click()
            expect(page.locator('details[data-category="windows_servers"] .pc')).to_have_count(2)
            expect(page.locator('details[data-category="windows_servers"] .pc.selected')).to_contain_text('Live-PC')
            expect(page.locator('details[data-category="user_pcs"]')).to_contain_text('No devices in this category yet.')
            self.assertEqual([],errors);browser.close()

if __name__=='__main__':unittest.main()
