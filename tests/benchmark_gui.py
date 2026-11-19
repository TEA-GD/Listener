"""Synthetic warm-query comparison. Build tests/ServerAgentCollector first; never uses production data."""
import argparse, datetime, json, os, pathlib, shutil, socket, sqlite3, statistics, subprocess, tempfile, time, uuid
from test_dashboard import FIELDS, ROOT

RAW="""SELECT MONITORED_HOST AS host,COUNT(*) AS events,MAX(TIMESTAMP_UTC) AS lastEvent,MAX(received_utc) AS lastReceived,
COUNT(DISTINCT NULLIF(PROCESS_NAME,'')) AS processes,COUNT(DISTINCT NULLIF(RECIEVER_IP,'')) AS destinations
FROM events GROUP BY MONITORED_HOST ORDER BY lastReceived DESC,host"""
FAST='SELECT host,events,lastEvent,lastReceived,processes,destinations FROM gui_devices ORDER BY lastReceived DESC,host'

def main():
    parser=argparse.ArgumentParser();parser.add_argument('--rows',type=int,default=200000);args=parser.parse_args()
    if args.rows<100:parser.error('Use at least 100 rows')
    dotnet=os.environ.get('DOTNET_HOST_PATH') or shutil.which('dotnet')
    with tempfile.TemporaryDirectory(prefix='listener-perf-') as temp:
        root=pathlib.Path(temp);database=root/'events.db'
        with sqlite3.connect(database) as db:
            db.execute('CREATE TABLE events('+','.join(f'[{f}] TEXT NOT NULL DEFAULT \'\'' for f in FIELDS)+',host TEXT,date_utc TEXT,received_utc TEXT)')
            def rows():
                for i in range(args.rows):
                    host=f'PC-{i%100:03}';stamp=(datetime.datetime(2026,11,1,tzinfo=datetime.timezone.utc)+datetime.timedelta(seconds=i//100*5)).isoformat()
                    yield (str(uuid.UUID(int=i+1)),'HEARTBEAT' if i//100%12==0 else 'TCP_SNAPSHOT',stamp,host,'process-'+str(i//100%30),'203.0.113.'+str(i//100%200),host,stamp[:10],stamp)
            db.executemany('INSERT INTO events(EVENT_ID,EVENT_TYPE,TIMESTAMP_UTC,MONITORED_HOST,PROCESS_NAME,RECIEVER_IP,host,date_utc,received_utc) VALUES(?,?,?,?,?,?,?,?,?)',rows())
        with socket.socket() as s:s.bind(('127.0.0.1',0));port=s.getsockname()[1]
        config=root/'collector.json';config.write_text(json.dumps({'bind_host':'127.0.0.1','bind_port':port,'database_path':str(database),'csv_dir':str(root/'csv'),'log_file':str(root/'collector.log'),'csv_export_seconds':3600,'allowed_client_cidrs':[]}))
        with (root/'stdout.log').open('w+') as log:
            start=time.perf_counter()
            process=subprocess.Popen([dotnet,str(ROOT/'tests/ServerAgentCollector/bin/Release/net10.0/ServerAgentCollector.dll'),'--config',str(config)],stdout=log,stderr=log)
            try:
                while time.perf_counter()-start<120:
                    if process.poll() is not None:log.seek(0);raise RuntimeError(log.read())
                    try:
                        with socket.create_connection(('127.0.0.1',port),timeout=.2):break
                    except OSError:time.sleep(.1)
                else:raise RuntimeError('Migration timed out')
                migration=time.perf_counter()-start
                with sqlite3.connect(database) as db:
                    assert db.execute(RAW).fetchall()==db.execute(FAST).fetchall(),'Summary differs from raw aggregation'
                    def measure(sql):
                        samples=[]
                        for _ in range(7):
                            start=time.perf_counter();db.execute(sql).fetchall();samples.append((time.perf_counter()-start)*1000)
                        return statistics.median(samples)
                    raw=measure(RAW);fast=measure(FAST)
                    print(json.dumps({'rows':args.rows,'devices':100,'migration_seconds':round(migration,2),'raw_list_query_median_ms':round(raw,3),'summary_list_query_median_ms':round(fast,3),'query_speedup':round(raw/fast,1),'repetitions':7},indent=2))
            finally:
                process.terminate()
                try:process.wait(timeout=10)
                except subprocess.TimeoutExpired:process.kill();process.wait()

if __name__=='__main__':main()
