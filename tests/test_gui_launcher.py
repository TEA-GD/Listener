"""Launcher plans and actual PowerShell startup flow with an inert test server."""
import json,os,pathlib,shutil,socket,subprocess,tempfile,unittest,signal,sqlite3,time,urllib.request,urllib.error,shlex
ROOT=pathlib.Path(__file__).resolve().parents[1]
PWSH=os.environ.get('PWSH_PATH') or shutil.which('pwsh')
def quote(value):return "'"+str(value).replace("'","''")+"'"
class Launcher(unittest.TestCase):
    def setUp(self):
        self.assertTrue(PWSH,'Set PWSH_PATH to run launcher checks')
        self.temp=tempfile.TemporaryDirectory();self.addCleanup(self.temp.cleanup);self.root=pathlib.Path(self.temp.name)
        self.env={**os.environ,'XDG_DATA_HOME':str(self.root/'data'),'XDG_CONFIG_HOME':str(self.root/'config'),'XDG_CACHE_HOME':str(self.root/'cache'),'POWERSHELL_TELEMETRY_OPTOUT':'1'}
    def run_ps(self,script,expected=0):
        result=subprocess.run([PWSH,'-NoLogo','-NoProfile','-Command',script],env=self.env,capture_output=True,text=True,timeout=30)
        self.assertEqual(expected,result.returncode,result.stdout+result.stderr);return result
    def test_modes_and_script_syntax(self):
        common=ROOT/'Listener_DB_GUI/Launcher.Common.ps1'
        script=f". {quote(common)}; @((Get-ListenerGuiPlan -AccessMode Local -Port 5080 -ComputerName 'server.example.local'),(Get-ListenerGuiPlan -AccessMode Domain -Port 8080 -ComputerName 'server.example.local')) | ConvertTo-Json -Compress"
        local,domain=json.loads(self.run_ps(script).stdout)
        self.assertEqual('http://127.0.0.1:5080',local['BindUrl']);self.assertEqual(local['BindUrl'],local['ShareUrl'])
        self.assertEqual('http://0.0.0.0:8080',domain['BindUrl']);self.assertEqual('http://server.example.local:8080',domain['ShareUrl']);self.assertEqual('http://127.0.0.1:8080',domain['BrowserUrl'])
        self.assertEqual('Listener-GUI-Domain-TCP-8080',domain['FirewallName'])
        for path in (ROOT/'Listener_DB_GUI').glob('*.ps1'):
            self.run_ps(f"$tokens=$null;$errors=$null;[System.Management.Automation.Language.Parser]::ParseFile({quote(path)},[ref]$tokens,[ref]$errors)|Out-Null;if($errors){{$errors;exit 1}}")
    @unittest.skipIf(os.name=='nt','Inert POSIX executable checks run on Linux; actual Windows startup is a manual check')
    def test_start_modes_key_handling_and_environment_restore(self):
        for name in ['Start-Windows.ps1','Launcher.Common.ps1']:shutil.copyfile(ROOT/'Listener_DB_GUI'/name,self.root/name)
        database=self.root/'database with spaces.db';database.write_bytes(b'')
        key=self.root/'mail view.key';key.write_text('synthetic-'+('a'*40))
        trace=self.root/'trace.json'
        server=self.root/'Listener_DB_GUI.exe'
        server.write_text('#!/usr/bin/env python3\nimport sys,os,json\nfrom pathlib import Path\nPath(os.environ["GUI_TEST_TRACE"]).write_text(json.dumps({"args":sys.argv[1:],"db":os.environ.get("WATCHER_DB_PATH"),"key":os.environ.get("LISTENER_MAIL_VIEW_TOKEN_FILE"),"token":os.environ.get("LISTENER_MAIL_VIEW_TOKEN")}))\n')
        server.chmod(0o755);self.env['GUI_TEST_TRACE']=str(trace)
        for mode,bind in [('Local','127.0.0.1'),('Domain','0.0.0.0')]:
            with socket.socket() as sock:sock.bind(('127.0.0.1',0));port=sock.getsockname()[1]
            result=self.run_ps(f"$env:WATCHER_DB_PATH='previous-db';$env:LISTENER_MAIL_VIEW_TOKEN='previous-token'; & {quote(self.root/'Start-Windows.ps1')} -DatabasePath {quote(database)} -Port {port} -AccessMode {mode} -MailViewKeyFile {quote(key)}; if($env:WATCHER_DB_PATH -ne 'previous-db' -or $env:LISTENER_MAIL_VIEW_TOKEN -ne 'previous-token'){{throw 'Environment not restored'}}")
            captured=json.loads(trace.read_text());self.assertEqual(['--urls',f'http://{bind}:{port}'],captured['args']);self.assertEqual(str(database),captured['db']);self.assertEqual(str(key),captured['key']);self.assertIsNone(captured['token'])
            if mode=='Domain':self.assertIn('Share with domain-network users:',result.stdout)
        with socket.socket() as sock:
            sock.bind(('127.0.0.1',0));sock.listen();port=sock.getsockname()[1]
            result=self.run_ps(f"& {quote(self.root/'Start-Windows.ps1')} -DatabasePath {quote(database)} -Port {port}",expected=1)
            self.assertIn('already in use',result.stderr)
        result=self.run_ps(f"& {quote(self.root/'Start-Windows.ps1')} -DatabasePath {quote(database)} -AccessMode Local -ConfigureFirewall",expected=1)
        self.assertIn('only available',result.stderr)

    @unittest.skipIf(os.name=='nt','POSIX harness verifies actual HTTP binding; Windows firewall/UI need a Windows PC')
    def test_actual_server_local_and_network_binding(self):
        dotnet=os.environ.get('DOTNET_HOST_PATH') or shutil.which('dotnet')
        dll=ROOT/'Listener_DB_GUI/bin/Release/net10.0/Listener_DB_GUI.dll'
        self.assertTrue(dotnet and dll.exists(),'Build Listener_DB_GUI and set DOTNET_HOST_PATH first')
        try:network_ip=socket.gethostbyname(socket.gethostname())
        except OSError:self.skipTest('No local network interface address')
        if network_ip.startswith('127.'):self.skipTest('No non-loopback address available')
        import test_dashboard as fixtures
        for name in ['Start-Windows.ps1','Launcher.Common.ps1']:shutil.copyfile(ROOT/'Listener_DB_GUI'/name,self.root/name)
        database=self.root/'network.db'
        with sqlite3.connect(database) as db:db.execute('CREATE TABLE events('+','.join(f'[{f}] TEXT' for f in fixtures.FIELDS)+',host TEXT,date_utc TEXT,received_utc TEXT)')
        server=self.root/'Listener_DB_GUI.exe'
        server.write_text('#!/bin/sh\nexec '+shlex.quote(dotnet)+' '+shlex.quote(str(dll))+' "$@"\n');server.chmod(0o755)
        opener=urllib.request.build_opener(urllib.request.ProxyHandler({}))
        for mode in ['Local','Domain']:
            with socket.socket() as sock:sock.bind(('127.0.0.1',0));port=sock.getsockname()[1]
            with (self.root/(mode+'.log')).open('w+') as log:
                process=subprocess.Popen([PWSH,'-NoLogo','-NoProfile','-File',str(self.root/'Start-Windows.ps1'),'-DatabasePath',str(database),'-AccessMode',mode,'-Port',str(port)],env=self.env,stdout=log,stderr=subprocess.STDOUT,start_new_session=True)
                try:
                    for _ in range(100):
                        if process.poll() is not None:log.seek(0);self.fail(log.read())
                        try:opener.open(f'http://127.0.0.1:{port}/api/pcs',timeout=.5).close();break
                        except (urllib.error.URLError,TimeoutError):time.sleep(.05)
                    else:log.seek(0);self.fail(log.read())
                    if mode=='Domain':
                        with opener.open(f'http://{network_ip}:{port}/api/pcs',timeout=2) as response:self.assertEqual([],json.load(response))
                    else:
                        with self.assertRaises(urllib.error.URLError):opener.open(f'http://{network_ip}:{port}/api/pcs',timeout=2)
                finally:
                    os.killpg(process.pid,signal.SIGTERM);process.wait(timeout=10)

if __name__=='__main__':unittest.main(verbosity=2)
