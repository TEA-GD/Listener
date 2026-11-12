"""Integration checks using a real collector-shaped SQLite database and analyzer process."""
import datetime as dt
import hashlib
import json
import os
from pathlib import Path
import shutil
import sqlite3
import subprocess
import tempfile
import unittest
import uuid

ROOT=Path(__file__).resolve().parents[1]
COLUMNS='EVENT_ID EVENT_TYPE TIMESTAMP_UTC DATE_AND_TIME SOURCE_IP SOURCE_DOMAIN_NAME PROCESS_NAME PROCESS_ID RECIEVER_IP RECIEVER_DOMAINNAME PORT_USED SOFTWARE_THAT_USED SOURCE_PORT PROTOCOL STATE OPERATING_SYSTEM MONITORED_HOST SNAPSHOT_ID COLLECTOR_PEER_IP'.split()

class AnalyzerTests(unittest.TestCase):
    def setUp(self):
        self.temp=tempfile.TemporaryDirectory(prefix='listener-analyzer-');self.root=Path(self.temp.name)
        self.source=self.root/'events.db';self.config_path=self.root/'config.json';self.now=dt.datetime.now(dt.timezone.utc)
        self.config={'database_path':str(self.source),'state_directory':str(self.root/'state'),'alerts_file':str(self.root/'share/alerts.jsonl'),
            'baseline_days':7,'rarity_window_days':30,'rare_max_active_days':2,'long_gap_days':30,'cooldown_hours':24,'maximum_alert_age_hours':24,'batch_size':5}
        self.save_config()
        with sqlite3.connect(self.source) as db:
            db.execute('CREATE TABLE events('+','.join(f'[{c}] TEXT' for c in COLUMNS)+',host TEXT,date_utc TEXT,received_utc TEXT)')
        self.add('PC-win','',40,kind='HEARTBEAT')
        self.add('PC-linux','',40,kind='HEARTBEAT',os_name='Linux')
        for day in range(1,21):self.add('PC-win','203.0.113.10',day)
        self.add('PC-win','203.0.113.20',15);self.add('PC-win','203.0.113.20',29)
        self.add('PC-win','203.0.113.30',35)
    def tearDown(self):self.temp.cleanup()
    def save_config(self):self.config_path.write_text(json.dumps(self.config))
    def add(self,host,ip,days=0,seconds=0,kind='TCP_CONNECTED',port='443',process='browser.exe',os_name='Windows'):
        event_id=str(uuid.uuid4());stamp=(self.now-dt.timedelta(days=days,seconds=seconds)).isoformat()
        row=dict.fromkeys(COLUMNS,'');row.update(EVENT_ID=event_id,EVENT_TYPE=kind,TIMESTAMP_UTC=stamp,MONITORED_HOST=host,
            RECIEVER_IP=ip,PORT_USED=port,PROCESS_NAME=process,SOFTWARE_THAT_USED='/programs/'+process,PROTOCOL='TCP',OPERATING_SYSTEM=os_name,SOURCE_IP='192.0.2.10',SOURCE_PORT='50001')
        with sqlite3.connect(self.source) as db:db.execute('INSERT INTO events VALUES('+','.join('?' for _ in range(len(COLUMNS)+3))+')',[row[c] for c in COLUMNS]+[host,stamp[:10],stamp])
        return event_id
    def run_analyzer(self,expected=0,report=False):
        before=hashlib.sha256(self.source.read_bytes()).hexdigest()
        command=([os.environ['ANALYZER_EXE']] if os.environ.get('ANALYZER_EXE') else
            [os.environ.get('DOTNET_HOST_PATH') or shutil.which('dotnet'),str(ROOT/'Listener_Analyzer/bin/Release/net10.0/Listener_Analyzer.dll')])
        command+=['--config',str(self.config_path),'--once']
        if report:command+=['--export-profiles',str(self.root/'report.json')]
        result=subprocess.run(command,capture_output=True,text=True,timeout=40)
        self.assertEqual(expected,result.returncode,result.stdout+result.stderr)
        self.assertEqual(before,hashlib.sha256(self.source.read_bytes()).hexdigest(),'Source database changed')
        return result
    def alerts(self):
        path=Path(self.config['alerts_file'])
        return [json.loads(line) for line in path.read_text().splitlines()] if path.is_file() else []
    def test_profiles_rarity_novelty_and_restart(self):
        self.run_analyzer(report=True);self.assertEqual([],self.alerts())
        self.add('PC-win','203.0.113.10',seconds=70) # usual destination, same tuple: silent
        combo=self.add('PC-win','203.0.113.10',seconds=60,port='8443')
        new=self.add('PC-win','203.0.113.40',seconds=50)
        rare=self.add('PC-win','203.0.113.20',seconds=40)
        repeat=self.add('PC-win','203.0.113.20',seconds=39)
        long_gap=self.add('PC-win','203.0.113.30',seconds=30)
        linux=self.add('PC-linux','203.0.113.10',seconds=20,os_name='Linux')
        self.add('fresh-device','203.0.113.40',seconds=10) # warm-up, not yet profiled
        self.run_analyzer(report=True)
        rows={a['event_id']:a for a in self.alerts()};self.assertEqual(5,len(rows))
        self.assertEqual(['new_port_or_process_combination'],rows[combo]['reasons'])
        self.assertEqual(['new_destination'],rows[new]['reasons'])
        self.assertIn('rare_destination',rows[rare]['reasons']);self.assertEqual(2,rows[rare]['prior_active_days_in_window'])
        self.assertNotIn(repeat,rows)
        self.assertIn('returned_after_gap',rows[long_gap]['reasons'])
        self.assertEqual('PC-linux',rows[linux]['device']);self.assertEqual('Linux',rows[linux]['operating_system'])
        self.assertIn('new_destination',rows[linux]['reasons'])
        self.assertEqual('50001',rows[new]['original_event']['SOURCE_PORT'])
        self.run_analyzer();self.assertEqual(5,len(self.alerts()),'Restart duplicated alerts')
        report=json.loads((self.root/'report.json').read_text())
        self.assertEqual(3,len(report['devices']))
        self.assertTrue(any(c['port']=='8443' and c['process']=='browser.exe' for c in report['connections']))
    def test_rare_process_on_otherwise_frequent_destination(self):
        self.add('PC-win','203.0.113.10',35,process='rare-tool.exe')
        self.run_analyzer()
        event=self.add('PC-win','203.0.113.10',seconds=5,process='rare-tool.exe')
        self.run_analyzer()
        row=self.alerts()[0];self.assertEqual(event,row['event_id'])
        self.assertNotIn('rare_destination',row['reasons'])
        self.assertIn('rare_port_or_process_combination',row['reasons'])
        self.assertIn('returned_port_or_process_after_gap',row['reasons'])
        self.assertEqual(0,row['prior_combination_active_days_in_window'])

    def test_share_failure_outbox_and_retry(self):
        self.run_analyzer()
        self.add('PC-win','203.0.113.99',seconds=5)
        blocked=self.root/'share';blocked.write_text('not a directory')
        self.run_analyzer(expected=2)
        with sqlite3.connect(self.root/'state/profiles.db') as db:self.assertEqual(1,db.execute('SELECT COUNT(*) FROM outbox WHERE sent_utc IS NULL').fetchone()[0])
        blocked.unlink();self.run_analyzer();self.assertEqual(1,len(self.alerts()))
        self.run_analyzer();self.assertEqual(1,len(self.alerts()))
    def test_delayed_records_and_disconnect_only_are_learning_not_alerts(self):
        self.run_analyzer()
        self.add('PC-win','203.0.113.90',days=2)
        self.add('PC-win','203.0.113.91',seconds=10,kind='TCP_DISCONNECTED')
        self.add('PC-win','',seconds=5,kind='HEARTBEAT')
        self.run_analyzer();self.assertEqual([],self.alerts())
        with sqlite3.connect(self.root/'state/profiles.db') as db:self.assertEqual(2,db.execute("SELECT COUNT(*) FROM destinations WHERE ip IN ('203.0.113.90','203.0.113.91')").fetchone()[0])
    def test_large_outbox_drains_and_interrupted_tail_is_separated(self):
        self.config['batch_size']=500;self.save_config();self.run_analyzer()
        blocked=self.root/'share';blocked.write_text('blocked')
        for number in range(120):self.add('PC-win',f'198.51.100.{number+1}',seconds=5)
        self.run_analyzer(expected=2)
        blocked.unlink();blocked.mkdir();output=Path(self.config['alerts_file'])
        output.write_text('{"interrupted":')
        self.run_analyzer()
        valid=[];malformed=0
        for line in output.read_text().splitlines():
            try:valid.append(json.loads(line))
            except json.JSONDecodeError:malformed+=1
        self.assertEqual(1,malformed);self.assertEqual(120,len(valid))
        self.assertEqual(120,len({a['alert_id'] for a in valid}))
        before=output.read_bytes();self.run_analyzer();self.assertEqual(before,output.read_bytes())

    def test_recent_out_of_order_and_warmup_suppression(self):
        self.config['baseline_days']=45;self.save_config();self.run_analyzer()
        self.add('PC-win','203.0.113.80',seconds=60);self.run_analyzer();self.assertEqual([],self.alerts())
        self.config['baseline_days']=7;self.save_config()
        event=self.add('PC-win','203.0.113.81',seconds=5);self.run_analyzer()
        self.add('PC-win','203.0.113.82',seconds=30);self.run_analyzer()
        self.assertEqual([event],[a['event_id'] for a in self.alerts()])

    def test_source_replacement_is_not_silently_accepted(self):
        self.run_analyzer()
        with sqlite3.connect(self.source) as db:db.execute('DELETE FROM events WHERE rowid=(SELECT MAX(rowid) FROM events)')
        result=self.run_analyzer(expected=1);self.assertIn('cursor changed',result.stderr)
    def test_state_cannot_be_reused_for_different_source(self):
        self.run_analyzer();other=self.root/'other.db';shutil.copyfile(self.source,other)
        self.config['database_path']=str(other);self.save_config()
        self.assertIn('another source database',self.run_analyzer(expected=1).stderr)

if __name__=='__main__':unittest.main(verbosity=2)
