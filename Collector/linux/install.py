#!/usr/bin/env python3
"""Install the local, self-contained Collector bundle and its two analyzers."""
import argparse
import grp
import ipaddress
import json
import os
from pathlib import Path
import pwd
import shutil
import shlex
import subprocess
import sys
import tempfile

UNITS = ['watcher-collector.service', 'listener-connection-analyzer.service', 'listener-analyzer.service']

def read(path):
    if not path.exists(): return {}
    value = json.loads(path.read_text())
    if not isinstance(value, dict): raise ValueError('Configuration must be a JSON object: ' + str(path))
    return value

def refuse_link(path):
    if path.is_symlink(): raise ValueError('Setup does not replace a linked path: ' + str(path))

def atomic(path, data, mode=0o640, backup=False):
    refuse_link(path); path.parent.mkdir(parents=True, exist_ok=True)
    if backup and path.exists(): shutil.copyfile(path, str(path) + '.bak'); os.chmod(str(path)+'.bak', 0o640)
    temporary = None
    try:
        with tempfile.NamedTemporaryFile(dir=path.parent, delete=False) as output:
            temporary = Path(output.name); output.write(data); output.flush(); os.fsync(output.fileno())
        temporary.chmod(mode); temporary.replace(path)
    finally:
        if temporary and temporary.exists(): temporary.unlink()

def save(path, data): atomic(path, (json.dumps(data, indent=2)+'\n').encode(), backup=True)
def quote(value): return '"'+str(value).replace('\\','\\\\').replace('"','\\"').replace('%','%%')+'"'

class Deployment:
    def __init__(self, args):
        self.args=args; self.staged=args.stage_root is not None
        self.root=Path(args.stage_root).resolve() if self.staged else Path('/')
        self.bundle=Path(args.bundle).resolve(); self.app=self.at('/opt/watcher-collector')
        self.config=self.at('/etc/watcher-collector/collector.json'); self.manifest=self.config.parent/'analyzers.json'
        self.main_data=self.at('/var/lib/watcher-collector'); self.main_log=self.at('/var/log/watcher-collector')
        self.connection_data=self.at('/var/lib/watcher-connection-analyzer'); self.behavior_data=self.at('/var/lib/watcher-analyzer')
        self.connection_cfg=self.at('/etc/watcher-connection-analyzer/config.json'); self.behavior_cfg=self.at('/etc/watcher-analyzer/config.json')
        self.unit_dir=self.at('/etc/systemd/system'); self.uid=self.gid=None; self.was_active=False; self.collector_ready=False
    def at(self, value): return self.root / value.lstrip('/')
    def run(self, *args, check=True):
        print('+', ' '.join(map(str,args)), flush=True)
        return subprocess.run(list(map(str,args)), check=check, capture_output=True, text=True)
    def active(self, unit): return self.run('systemctl','is-active','--quiet',unit,check=False).returncode==0
    def loaded(self, unit):
        status=self.run('systemctl','show',unit,'--property=LoadState','--value',check=False)
        return status.returncode==0 and status.stdout.strip() not in ('','not-found')
    def stop(self):
        if not self.staged:
            for unit in reversed(UNITS):
                if self.loaded(unit): self.run('systemctl','stop',unit)
    def account(self):
        if self.staged: return
        try: grp.getgrnam('watcher-collector')
        except KeyError: self.run('groupadd','--system','watcher-collector')
        try: pwd.getpwnam('watcher-collector')
        except KeyError: self.run('useradd','--system','--gid','watcher-collector','--home-dir','/var/lib/watcher-collector','--no-create-home','--shell','/usr/sbin/nologin','watcher-collector')
        self.uid=pwd.getpwnam('watcher-collector').pw_uid; self.gid=grp.getgrnam('watcher-collector').gr_gid
    def directory(self, path, state=False):
        path=Path(path); refuse_link(path); created=not path.exists(); path.mkdir(parents=True,exist_ok=True)
        if state or created:
            path.chmod(0o750)
            if not self.staged: os.chown(path,self.uid if state else 0,self.gid)
    def config_write(self,path,settings):
        self.directory(path.parent); save(path,settings)
        if not self.staged: os.chown(path,0,self.gid)
    def preflight(self,binary,config,cache):
        if self.staged: return
        self.run('runuser','-u','watcher-collector','-g','watcher-collector','--','env','DOTNET_BUNDLE_EXTRACT_BASE_DIR='+str(cache/'.net'),binary,'--config',config,'--check-config')
    def unit(self,name,binary,config,state,writable,enabled):
        dependencies='Wants=listener-connection-analyzer.service listener-analyzer.service\n' if name==UNITS[0] and enabled else ''
        if name!=UNITS[0]: dependencies='After=watcher-collector.service\nPartOf=watcher-collector.service\n'
        text='[Unit]\nDescription=Listener '+name+'\n'+dependencies+'\n[Service]\nType=simple\nUser=watcher-collector\nGroup=watcher-collector\n'
        text+='WorkingDirectory='+quote(binary.parent)+'\nExecStart='+quote(binary)+' --config '+quote(config)+'\n'
        text+='Environment='+quote('DOTNET_BUNDLE_EXTRACT_BASE_DIR='+str(state/'.net'))+'\n'
        text+='Restart=on-failure\nRestartSec=5\nTimeoutStopSec=45\nUMask=0027\nNoNewPrivileges=yes\nProtectSystem=strict\nProtectHome=yes\nPrivateTmp=yes\n'
        text+='ReadWritePaths='+' '.join(quote(p) for p in dict.fromkeys([state,*writable]))+'\n\n[Install]\nWantedBy=multi-user.target\n'
        atomic(self.unit_dir/name,text.encode(),0o644)
    def install(self):
        enabled=self.args.analyzers if self.args.analyzers is not None else read(self.manifest).get('enabled',True)
        settings=read(self.config)
        defaults={'bind_host':'0.0.0.0','bind_port':6515,'database_path':str(self.main_data/'watcher-events.db'),'csv_dir':str(self.main_data/'csv'),'log_file':str(self.main_log/'collector.log'),'csv_export_seconds':10,'max_clients':64,'allowed_client_cidrs':[]}
        for key,value in defaults.items(): settings.setdefault(key,value)
        if self.args.port is not None: settings['bind_port']=self.args.port
        if self.args.bind_host is not None: settings['bind_host']=self.args.bind_host
        if self.args.allow_cidr is not None: settings['allowed_client_cidrs']=self.args.allow_cidr
        ipaddress.ip_address(settings['bind_host'])
        if not 1<=int(settings['bind_port'])<=65535: raise ValueError('Port must be 1..65535')
        for cidr in settings['allowed_client_cidrs']:
            if ipaddress.ip_network(cidr,strict=False).prefixlen==0: raise ValueError('Do not use a /0 allowlist')
        for key in ['database_path','csv_dir','log_file']:
            if not Path(settings[key]).is_absolute(): raise ValueError(key+' must be absolute')
        conn=read(self.connection_cfg) if enabled else {}; behavior=read(self.behavior_cfg) if enabled else {}
        for config in [conn,behavior]: config['database_path']=settings['database_path']
        for key,value in {'analysis_database':str(self.connection_data/'connection-analysis.db'),'poll_seconds':5,'batch_size':1000}.items(): conn.setdefault(key,value)
        for key,value in {'state_directory':str(self.behavior_data),'alerts_file':str(self.behavior_data/'alerts.jsonl'),'poll_seconds':10,'batch_size':500}.items(): behavior.setdefault(key,value)
        required={'WatcherCollector':self.app/'WatcherCollector'}
        if enabled: required.update({'analyzers/connection/Listener_ConnectionAnalyzer':self.app/'analyzers/connection/Listener_ConnectionAnalyzer','analyzers/behavior/Listener_Analyzer':self.app/'analyzers/behavior/Listener_Analyzer'})
        for name in required:
            if not (self.bundle/name).is_file(): raise ValueError('Incomplete bundle: '+name)
        if not self.staged: self.was_active=self.active(UNITS[0])
        self.stop(); self.account()
        for directory in [self.main_data,self.main_log,self.connection_data,self.behavior_data]: self.directory(directory,True)
        self.app.mkdir(parents=True,exist_ok=True); refuse_link(self.app)
        for name,target in required.items(): atomic(target,(self.bundle/name).read_bytes(),0o755)
        for name in ['install-linux.sh','uninstall-linux.sh','install.py']:
            if (self.bundle/name).exists(): atomic(self.app/name,(self.bundle/name).read_bytes(),0o755)
        self.config_write(self.config,settings)
        database=Path(settings['database_path']); csv=Path(settings['csv_dir']); log=Path(settings['log_file'])
        for directory in [database.parent,csv,log.parent]:
            if not directory.exists(): self.directory(directory,True)
        self.preflight(self.app/'WatcherCollector',self.config,self.main_data)
        self.collector_ready=True
        if enabled:
            self.config_write(self.connection_cfg,conn); self.config_write(self.behavior_cfg,behavior)
            for value,base in [(conn['analysis_database'],self.connection_cfg.parent),(behavior['state_directory'],self.behavior_cfg.parent),(behavior['alerts_file'],self.behavior_cfg.parent)]:
                destination=Path(value); destination=destination if destination.is_absolute() else base/destination
                directory=destination if value==behavior['state_directory'] else destination.parent
                if not directory.exists(): self.directory(directory,True)
            self.preflight(self.app/'analyzers/connection/Listener_ConnectionAnalyzer',self.connection_cfg,self.connection_data)
            self.preflight(self.app/'analyzers/behavior/Listener_Analyzer',self.behavior_cfg,self.behavior_data)
        self.unit(UNITS[0],self.app/'WatcherCollector',self.config,self.main_data,[database.parent,csv,log.parent],enabled)
        if enabled:
            connection_db=Path(conn['analysis_database']); connection_db=connection_db if connection_db.is_absolute() else self.connection_cfg.parent/connection_db
            state=Path(behavior['state_directory']); state=state if state.is_absolute() else self.behavior_cfg.parent/state
            alerts=Path(behavior['alerts_file']); alerts=alerts if alerts.is_absolute() else self.behavior_cfg.parent/alerts
            self.unit(UNITS[1],self.app/'analyzers/connection/Listener_ConnectionAnalyzer',self.connection_cfg,self.connection_data,[connection_db.parent],enabled)
            self.unit(UNITS[2],self.app/'analyzers/behavior/Listener_Analyzer',self.behavior_cfg,self.behavior_data,[state,alerts.parent],enabled)
        self.config_write(self.manifest,{'enabled':bool(enabled)})
        environment='WATCHER_DB_PATH='+shlex.quote(settings['database_path'])+'\n'
        if enabled:
            analysis_path=Path(conn['analysis_database']); analysis_path=analysis_path if analysis_path.is_absolute() else self.connection_cfg.parent/analysis_path
            environment+='WATCHER_ANALYSIS_DB_PATH='+shlex.quote(str(analysis_path))+'\n'
        atomic(self.config.parent/'gui.env',environment.encode())
        if not self.staged: os.chown(self.config.parent/'gui.env',0,self.gid)
        if not self.staged:
            self.run('systemctl','daemon-reload')
            if not enabled:
                for unit in UNITS[1:]:
                    if (self.unit_dir/unit).exists(): self.run('systemctl','disable',unit)
            units=UNITS if enabled else UNITS[:1]
            self.run('systemctl','enable',*units)
            if not self.args.no_start:
                self.run('systemctl','start',*units)
                for unit in units:
                    if not self.active(unit): raise RuntimeError('Service not active: '+unit+'; check journalctl -u '+unit)
        print('Installed Collector'+(' and both analyzers' if enabled else ' (analyzers disabled)')+'. Configuration and existing databases retained.')
        if enabled: print('GUI analysis DB: '+str(conn['analysis_database']))
        if self.staged: print('Staged only: no accounts, preflight initialization or system services were changed.')
    def recover(self):
        if not self.staged and self.was_active and self.collector_ready:
            result=self.run('systemctl','start',UNITS[0],check=False)
            print('Collector restart requested after setup failure.' if result.returncode==0 else 'Collector restart also failed; inspect its journal.',file=sys.stderr)
    def uninstall(self):
        self.stop()
        if not self.staged:
            for unit in UNITS:
                if (self.unit_dir/unit).exists(): self.run('systemctl','disable',unit)
        for unit in UNITS:
            path=self.unit_dir/unit; refuse_link(path)
            if path.exists(): path.unlink()
        refuse_link(self.app)
        if self.app.exists(): shutil.rmtree(self.app)
        if not self.staged: self.run('systemctl','daemon-reload')
        print('Removed programs and managed services. All configuration, databases, CSVs and alert files were preserved.')

def main():
    parser=argparse.ArgumentParser(description=__doc__); parser.add_argument('operation',choices=['install','uninstall'])
    parser.add_argument('--bundle',default=str(Path(__file__).resolve().parent)); parser.add_argument('--stage-root',help='Stage files under this directory without changing accounts/services or running preflight')
    parser.add_argument('--port',type=int); parser.add_argument('--bind-host'); parser.add_argument('--allow-cidr',action='append'); parser.add_argument('--no-start',action='store_true')
    choice=parser.add_mutually_exclusive_group();choice.add_argument('--with-analyzers',dest='analyzers',action='store_true');choice.add_argument('--without-analyzers',dest='analyzers',action='store_false');parser.set_defaults(analyzers=None)
    args=parser.parse_args()
    if not args.stage_root and os.geteuid()!=0: parser.error('Run as root (sudo), or use --stage-root for staging/testing')
    if args.stage_root and Path(args.stage_root).resolve()==Path('/'): parser.error('Staging root cannot be /')
    deployment=None
    try:
        deployment=Deployment(args)
        if args.operation=='install': deployment.install()
        else: deployment.uninstall()
    except (OSError,ValueError,subprocess.SubprocessError) as error:
        print('Setup failed; existing data was retained: '+str(error),file=sys.stderr)
        if deployment and args.operation=='install': deployment.recover()
        if isinstance(error,subprocess.CalledProcessError): print((error.stdout or '')+(error.stderr or ''),file=sys.stderr)
        return 1
    return 0
if __name__=='__main__':sys.exit(main())
