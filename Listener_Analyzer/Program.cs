using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace ListenerAnalyzer;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        try
        {
            if (args.Contains("--help")) { Console.WriteLine("Listener_Analyzer --config config.json [--once] [--export-profiles report.json]"); return 0; }
            string Option(string name, string fallback) { int i=Array.IndexOf(args,name); return i<0?fallback:i+1<args.Length?args[i+1]:throw new ArgumentException("Missing value for " + name); }
            var config=AnalyzerConfig.Load(Option("--config","config.json"));
            Directory.CreateDirectory(config.state_directory);
            using var processLock=new FileStream(Path.Combine(config.state_directory,"analyzer.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
            using var analyzer=new Analyzer(config);
            using var stop=new CancellationTokenSource();
            Console.CancelKeyPress+=(_,e)=>{e.Cancel=true;stop.Cancel();};
            do
            {
                int count;
                do { count=analyzer.ProcessBatch(); analyzer.Deliver(); } while(count>0&&!stop.IsCancellationRequested);
                string report=Option("--export-profiles","");
                if(report.Length>0) analyzer.ExportProfiles(report);
                if(args.Contains("--once")) return analyzer.PendingAlerts==0?0:2;
                await Task.Delay(TimeSpan.FromSeconds(config.poll_seconds),stop.Token);
            } while(!stop.IsCancellationRequested);
            return 0;
        }
        catch(OperationCanceledException) { return 0; }
        catch(Exception ex) { Console.Error.WriteLine("Analyzer stopped: "+ex.Message); return 1; }
    }
}

internal sealed class AnalyzerConfig
{
    public string database_path {get;set;}="";
    public string state_directory {get;set;}="state";
    public string alerts_file {get;set;}="alerts.jsonl";
    public int poll_seconds {get;set;}=10;
    public int baseline_days {get;set;}=7;
    public int rarity_window_days {get;set;}=30;
    public int rare_max_active_days {get;set;}=2;
    public int long_gap_days {get;set;}=30;
    public int cooldown_hours {get;set;}=24;
    public int maximum_alert_age_hours {get;set;}=24;
    public int batch_size {get;set;}=500;
    public static AnalyzerConfig Load(string path)
    {
        string directory=Path.GetDirectoryName(Path.GetFullPath(path))!;
        var c=JsonSerializer.Deserialize<AnalyzerConfig>(File.ReadAllText(path))??throw new ArgumentException("Invalid config JSON");
        if(string.IsNullOrWhiteSpace(c.database_path)||string.IsNullOrWhiteSpace(c.state_directory)||string.IsNullOrWhiteSpace(c.alerts_file)) throw new ArgumentException("Database, state directory and alert file paths are required");
        c.database_path=Path.GetFullPath(c.database_path,directory);c.state_directory=Path.GetFullPath(c.state_directory,directory);c.alerts_file=Path.GetFullPath(c.alerts_file,directory);
        if(c.poll_seconds is <1 or >3600 || c.baseline_days is <0 or >3650 || c.rarity_window_days is <1 or >3650 || c.rare_max_active_days<0 || c.rare_max_active_days>c.rarity_window_days || c.long_gap_days is <1 or >3650 || c.cooldown_hours is <1 or >8760 || c.maximum_alert_age_hours is <1 or >8760 || c.batch_size is <1 or >10000) throw new ArgumentException("Invalid analyzer thresholds");
        string statePath=Path.Combine(c.state_directory,"profiles.db");
        if(string.Equals(statePath,c.database_path,StringComparison.OrdinalIgnoreCase)||string.Equals(c.alerts_file,c.database_path,StringComparison.OrdinalIgnoreCase)||string.Equals(c.alerts_file,statePath,StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Source database, profile database and alerts file must be different files");
        return c;
    }
}

internal sealed record Observation(long RowId,string EventId,string Host,string Type,DateTimeOffset Time,string Ip,string Port,string Protocol,string Process,string Software,string Os,Dictionary<string,object?> Fields)
{
    public bool Positive => Type is "TCP_CONNECTED" or "ACTIVE_AT_START";
    public string Signature=>JsonSerializer.Serialize(new[]{Ip,Port,Protocol,Process,Software});
}

internal sealed class Analyzer : IDisposable
{
    private readonly AnalyzerConfig config;
    private readonly SqliteConnection state;
    private DateTimeOffset retryOutputAfter=DateTimeOffset.MinValue;
    private static string Stamp(DateTimeOffset time)=>time.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ",CultureInfo.InvariantCulture);
    public Analyzer(AnalyzerConfig config)
    {
        this.config=config;
        Directory.CreateDirectory(config.state_directory);
        state=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=Path.Combine(config.state_directory,"profiles.db"),DefaultTimeout=15}.ToString());state.Open();
        Exec("""
            PRAGMA journal_mode=WAL;
            PRAGMA synchronous=FULL;
            CREATE TABLE IF NOT EXISTS meta(key TEXT PRIMARY KEY,value TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS devices(host TEXT PRIMARY KEY,first_seen TEXT NOT NULL,last_seen TEXT NOT NULL,os TEXT NOT NULL,observations INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS destinations(host TEXT NOT NULL,ip TEXT NOT NULL,first_seen TEXT NOT NULL,last_seen TEXT NOT NULL,observations INTEGER NOT NULL,PRIMARY KEY(host,ip));
            CREATE TABLE IF NOT EXISTS connections(host TEXT NOT NULL,signature TEXT NOT NULL,ip TEXT NOT NULL,port TEXT NOT NULL,protocol TEXT NOT NULL,process TEXT NOT NULL,software TEXT NOT NULL,first_seen TEXT NOT NULL,last_seen TEXT NOT NULL,observations INTEGER NOT NULL,PRIMARY KEY(host,signature));
            CREATE TABLE IF NOT EXISTS destination_days(host TEXT NOT NULL,ip TEXT NOT NULL,day TEXT NOT NULL,observations INTEGER NOT NULL,PRIMARY KEY(host,ip,day));
            CREATE TABLE IF NOT EXISTS connection_days(host TEXT NOT NULL,signature TEXT NOT NULL,day TEXT NOT NULL,observations INTEGER NOT NULL,PRIMARY KEY(host,signature,day));
            CREATE TABLE IF NOT EXISTS cooldowns(host TEXT NOT NULL,signature TEXT NOT NULL,last_alert TEXT NOT NULL,PRIMARY KEY(host,signature));
            CREATE TABLE IF NOT EXISTS outbox(alert_id TEXT PRIMARY KEY,event_id TEXT NOT NULL UNIQUE,payload_json TEXT NOT NULL,created_utc TEXT NOT NULL,sent_utc TEXT);
            """);
        using var source=OpenSource();
        string path=Path.GetFullPath(config.database_path);
        if(Meta("source_path") is string previous && !string.Equals(path,previous,OperatingSystem.IsWindows()?StringComparison.OrdinalIgnoreCase:StringComparison.Ordinal)) throw new InvalidOperationException("This state belongs to another source database. Use a separate state directory.");
        if(Meta("bootstrap_end") is null)
        {
            using var transaction=state.BeginTransaction();
            SetMeta("source_path",path,transaction);
            using var max=source.CreateCommand();max.CommandText="SELECT COALESCE(MAX(rowid),0) FROM events";
            SetMeta("bootstrap_end",Convert.ToString(max.ExecuteScalar(),CultureInfo.InvariantCulture)!,transaction);
            SetMeta("cursor","0",transaction);SetMeta("anchor","",transaction);transaction.Commit();
            Console.WriteLine("Learning existing records without alerts. New records after this snapshot will be evaluated.");
        }
    }
    private SqliteConnection OpenSource()
    {
        if(!File.Exists(config.database_path)) throw new IOException("Collector database not found: "+config.database_path);
        var db=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=config.database_path,Mode=SqliteOpenMode.ReadOnly,DefaultTimeout=15}.ToString());
        try
        {
            db.Open();using var c=db.CreateCommand();c.CommandText="PRAGMA query_only=ON;";c.ExecuteNonQuery();
            c.CommandText="SELECT rowid,EVENT_ID,EVENT_TYPE,TIMESTAMP_UTC,MONITORED_HOST,RECIEVER_IP,PORT_USED,PROTOCOL,PROCESS_NAME,SOFTWARE_THAT_USED,OPERATING_SYSTEM FROM events LIMIT 0";
            using var reader=c.ExecuteReader();return db;
        }
        catch {db.Dispose();throw;}
    }
    private SqliteCommand Command(string sql,SqliteTransaction? transaction=null,params (string,object?)[] parameters)
    {
        var c=state.CreateCommand();c.CommandText=sql;c.Transaction=transaction;
        foreach(var (key,value) in parameters)c.Parameters.AddWithValue(key,value??DBNull.Value);
        return c;
    }
    private void Exec(string sql,SqliteTransaction? transaction=null,params (string,object?)[] parameters){using var c=Command(sql,transaction,parameters);c.ExecuteNonQuery();}
    private string? Scalar(string sql,SqliteTransaction? transaction=null,params (string,object?)[] parameters){using var c=Command(sql,transaction,parameters);var value=c.ExecuteScalar();return value is null or DBNull?null:Convert.ToString(value,CultureInfo.InvariantCulture);}
    private string? Meta(string key)=>Scalar("SELECT value FROM meta WHERE key=$key",null,("$key",key));
    private void SetMeta(string key,string value,SqliteTransaction transaction)=>Exec("INSERT INTO meta VALUES($key,$value) ON CONFLICT(key) DO UPDATE SET value=excluded.value",transaction,("$key",key),("$value",value));
    public int PendingAlerts=>int.Parse(Scalar("SELECT COUNT(*) FROM outbox WHERE sent_utc IS NULL")!,CultureInfo.InvariantCulture);
    public int ProcessBatch()
    {
        long cursor=long.Parse(Meta("cursor")!,CultureInfo.InvariantCulture);
        long bootstrap=long.Parse(Meta("bootstrap_end")!,CultureInfo.InvariantCulture);
        using var source=OpenSource();
        if(cursor>0)
        {
            using var anchor=source.CreateCommand();anchor.CommandText="SELECT EVENT_ID FROM events WHERE rowid=$row";anchor.Parameters.AddWithValue("$row",cursor);
            if(!string.Equals(anchor.ExecuteScalar() as string,Meta("anchor"),StringComparison.Ordinal)) throw new InvalidOperationException("Source cursor changed: database may have been restored, rotated or rewritten. Preserve this state and use a new state directory to relearn the new database.");
        }
        using var query=source.CreateCommand();query.CommandText="SELECT rowid AS analyzer_rowid,* FROM events WHERE rowid>$cursor ORDER BY rowid LIMIT $limit";
        query.Parameters.AddWithValue("$cursor",cursor);query.Parameters.AddWithValue("$limit",config.batch_size);
        var rows=new List<Dictionary<string,object?>>();
        using(var reader=query.ExecuteReader())while(reader.Read())
        {
            var row=new Dictionary<string,object?>(StringComparer.OrdinalIgnoreCase);
            for(int i=0;i<reader.FieldCount;i++)row[reader.GetName(i)]=reader.IsDBNull(i)?null:reader.GetValue(i);
            rows.Add(row);
        }
        if(rows.Count==0)return 0;
        using var transaction=state.BeginTransaction();
        foreach(var row in rows)
        {
            string Text(string name)=>row.TryGetValue(name,out var value)?Convert.ToString(value,CultureInfo.InvariantCulture)??"":"";
            long id=Convert.ToInt64(row["analyzer_rowid"],CultureInfo.InvariantCulture);
            if(DateTimeOffset.TryParse(Text("TIMESTAMP_UTC"),CultureInfo.InvariantCulture,DateTimeStyles.None,out var time)&&Text("MONITORED_HOST").Length>0)
            {
                var observation=new Observation(id,Text("EVENT_ID"),Text("MONITORED_HOST"),Text("EVENT_TYPE"),time.ToUniversalTime(),Text("RECIEVER_IP"),Text("PORT_USED"),Text("PROTOCOL"),Text("PROCESS_NAME"),Text("SOFTWARE_THAT_USED"),Text("OPERATING_SYSTEM"),row);
                Learn(observation,id>bootstrap,transaction);
            }
            else Console.Error.WriteLine("Skipped malformed timestamp/device identity on source row "+id);
            SetMeta("cursor",id.ToString(CultureInfo.InvariantCulture),transaction);SetMeta("anchor",Text("EVENT_ID"),transaction);
        }
        transaction.Commit();Console.WriteLine("Processed "+rows.Count+" source records; pending alerts="+PendingAlerts);return rows.Count;
    }
    private void Learn(Observation e,bool live,SqliteTransaction tx)
    {
        string now=Stamp(e.Time),day=e.Time.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture);
        string? deviceFirst=Scalar("SELECT first_seen FROM devices WHERE host=$host",tx,("$host",e.Host));
        string? deviceLast=Scalar("SELECT last_seen FROM devices WHERE host=$host",tx,("$host",e.Host));
        bool mature=deviceFirst is not null && e.Time-DateTimeOffset.Parse(deviceFirst,CultureInfo.InvariantCulture)>=TimeSpan.FromDays(config.baseline_days);
        bool timely=e.Time>=DateTimeOffset.UtcNow.AddHours(-config.maximum_alert_age_hours)&&e.Time<=DateTimeOffset.UtcNow.AddMinutes(5);
        bool ordered=deviceLast is null||string.CompareOrdinal(now,deviceLast)>=0;
        bool connection=e.Type is "TCP_CONNECTED" or "ACTIVE_AT_START" or "TCP_DISCONNECTED";
        if(connection&&IPAddress.TryParse(e.Ip,out _))
        {
            string? last=Scalar("SELECT last_seen FROM destinations WHERE host=$host AND ip=$ip",tx,("$host",e.Host),("$ip",e.Ip));
            string? combo=Scalar("SELECT last_seen FROM connections WHERE host=$host AND signature=$signature",tx,("$host",e.Host),("$signature",e.Signature));
            string windowStart=e.Time.AddDays(-config.rarity_window_days).ToString("yyyy-MM-dd",CultureInfo.InvariantCulture);
            int days=int.Parse(Scalar("SELECT COUNT(*) FROM destination_days WHERE host=$host AND ip=$ip AND day>=$from AND day<$day",tx,("$host",e.Host),("$ip",e.Ip),("$from",windowStart),("$day",day))!,CultureInfo.InvariantCulture);
            int combinationDays=int.Parse(Scalar("SELECT COUNT(*) FROM connection_days WHERE host=$host AND signature=$signature AND day>=$from AND day<$day",tx,("$host",e.Host),("$signature",e.Signature),("$from",windowStart),("$day",day))!,CultureInfo.InvariantCulture);
            var reasons=new List<string>();
            if(last is null)reasons.Add("new_destination");
            else
            {
                if(e.Time-DateTimeOffset.Parse(last,CultureInfo.InvariantCulture)>=TimeSpan.FromDays(config.long_gap_days))reasons.Add("returned_after_gap");
                if(days<=config.rare_max_active_days)reasons.Add("rare_destination");
                if(combo is null)reasons.Add("new_port_or_process_combination");
                else
                {
                    if(combinationDays<=config.rare_max_active_days)reasons.Add("rare_port_or_process_combination");
                    if(e.Time-DateTimeOffset.Parse(combo,CultureInfo.InvariantCulture)>=TimeSpan.FromDays(config.long_gap_days))reasons.Add("returned_port_or_process_after_gap");
                }
            }
            string cooldownSignature=JsonSerializer.Serialize(new[]{e.Ip,e.Port,e.Protocol,e.Process,e.Software});
            string? alerted=Scalar("SELECT last_alert FROM cooldowns WHERE host=$host AND signature=$signature",tx,("$host",e.Host),("$signature",cooldownSignature));
            bool cooled=alerted is null||e.Time-DateTimeOffset.Parse(alerted,CultureInfo.InvariantCulture)>=TimeSpan.FromHours(config.cooldown_hours);
            if(live&&e.Positive&&mature&&timely&&ordered&&cooled&&reasons.Count>0)
            {
                string alertId=Guid.NewGuid().ToString("D");
                var fields=new Dictionary<string,object?>(e.Fields,StringComparer.OrdinalIgnoreCase);fields.Remove("analyzer_rowid");
                string payload=JsonSerializer.Serialize(new{alert_id=alertId,event_id=e.EventId,created_utc=Stamp(DateTimeOffset.UtcNow),device=e.Host,operating_system=e.Os,
                    destination_ip=e.Ip,destination_port=e.Port,protocol=e.Protocol,process=e.Process,software=e.Software,observed_utc=now,previous_observation_utc=last,
                    prior_active_days_in_window=days,prior_combination_active_days_in_window=combinationDays,previous_combination_observation_utc=combo,window_days=config.rarity_window_days,reasons,
                    message=$"{e.Host} connected to {e.Ip}:{e.Port} using {e.Process}. Reasons: {string.Join(", ",reasons)}.",original_event=fields});
                Exec("INSERT OR IGNORE INTO outbox(alert_id,event_id,payload_json,created_utc) VALUES($id,$event,$payload,$now)",tx,("$id",alertId),("$event",e.EventId),("$payload",payload),("$now",Stamp(DateTimeOffset.UtcNow)));
                Exec("INSERT INTO cooldowns VALUES($host,$signature,$now) ON CONFLICT(host,signature) DO UPDATE SET last_alert=excluded.last_alert",tx,("$host",e.Host),("$signature",cooldownSignature),("$now",now));
            }
            Exec("""
                INSERT INTO destinations VALUES($host,$ip,$now,$now,1)
                ON CONFLICT(host,ip) DO UPDATE SET first_seen=MIN(first_seen,excluded.first_seen),last_seen=MAX(last_seen,excluded.last_seen),observations=observations+1
                """,tx,("$host",e.Host),("$ip",e.Ip),("$now",now));
            Exec("""
                INSERT INTO connections VALUES($host,$signature,$ip,$port,$protocol,$process,$software,$now,$now,1)
                ON CONFLICT(host,signature) DO UPDATE SET first_seen=MIN(first_seen,excluded.first_seen),last_seen=MAX(last_seen,excluded.last_seen),observations=observations+1
                """,tx,("$host",e.Host),("$signature",e.Signature),("$ip",e.Ip),("$port",e.Port),("$protocol",e.Protocol),("$process",e.Process),("$software",e.Software),("$now",now));
            Exec("INSERT INTO destination_days VALUES($host,$ip,$day,1) ON CONFLICT(host,ip,day) DO UPDATE SET observations=observations+1",tx,("$host",e.Host),("$ip",e.Ip),("$day",day));
            Exec("INSERT INTO connection_days VALUES($host,$signature,$day,1) ON CONFLICT(host,signature,day) DO UPDATE SET observations=observations+1",tx,("$host",e.Host),("$signature",e.Signature),("$day",day));
        }
        Exec("""
            INSERT INTO devices VALUES($host,$now,$now,$os,1)
            ON CONFLICT(host) DO UPDATE SET first_seen=MIN(first_seen,excluded.first_seen),last_seen=MAX(last_seen,excluded.last_seen),
                os=CASE WHEN excluded.os<>'' AND excluded.last_seen>=last_seen THEN excluded.os ELSE os END,observations=observations+1
            """,tx,("$host",e.Host),("$now",now),("$os",e.Os));
    }
    public void Deliver()
    {
        if(DateTimeOffset.UtcNow<retryOutputAfter)return;
        while (DeliverBatch() == 100) { }
    }
    private int DeliverBatch()
    {
        using var query=Command("SELECT alert_id,payload_json FROM outbox WHERE sent_utc IS NULL ORDER BY created_utc,alert_id LIMIT 100");
        var pending=new List<(string Id,string Payload)>();using(var r=query.ExecuteReader())while(r.Read())pending.Add((r.GetString(0),r.GetString(1)));
        int delivered=0;
        foreach(var (id,payload) in pending)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(config.alerts_file)!);
                using(var file=new FileStream(config.alerts_file,FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.Read))
                {
                    // A previous interrupted write may have left an incomplete tail. Keep
                    // the next retry on its own line so valid alerts remain recoverable.
                    if(file.Length>0){file.Seek(-1,SeekOrigin.End);if(file.ReadByte()!=10)file.WriteByte(10);}
                    file.Seek(0,SeekOrigin.End);var bytes=Encoding.UTF8.GetBytes(payload+"\n");file.Write(bytes);file.Flush(true);
                }
                Exec("UPDATE outbox SET sent_utc=$now WHERE alert_id=$id",null,("$id",id),("$now",Stamp(DateTimeOffset.UtcNow)));
                delivered++;
            }
            catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or SqliteException)
            {retryOutputAfter=DateTimeOffset.UtcNow.AddSeconds(config.poll_seconds);Console.Error.WriteLine("Alert output unavailable; retained in local outbox: "+ex.Message);break;}
        }
        return delivered;
    }
    public void ExportProfiles(string path)
    {
        List<Dictionary<string,object?>> Rows(string sql){using var c=Command(sql);using var r=c.ExecuteReader();var rows=new List<Dictionary<string,object?>>();while(r.Read()){var row=new Dictionary<string,object?>();for(int i=0;i<r.FieldCount;i++)row[r.GetName(i)]=r.GetValue(i);rows.Add(row);}return rows;}
        string full=Path.GetFullPath(path);
        if(string.Equals(full,config.database_path,StringComparison.OrdinalIgnoreCase)||string.Equals(full,config.alerts_file,StringComparison.OrdinalIgnoreCase)||string.Equals(full,Path.Combine(config.state_directory,"profiles.db"),StringComparison.OrdinalIgnoreCase))throw new ArgumentException("Report must not overwrite source/state/alerts files");
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        string temp=full+".tmp";
        File.WriteAllText(temp,JsonSerializer.Serialize(new{generated_utc=Stamp(DateTimeOffset.UtcNow),devices=Rows("SELECT * FROM devices ORDER BY host"),connections=Rows("SELECT * FROM connections ORDER BY host,ip,port,process"),destination_days=Rows("SELECT * FROM destination_days ORDER BY host,ip,day"),connection_days=Rows("SELECT * FROM connection_days ORDER BY host,signature,day"),rules=new{config.baseline_days,config.rarity_window_days,config.rare_max_active_days,config.long_gap_days,config.cooldown_hours}},new JsonSerializerOptions{WriteIndented=true}));
        File.Move(temp,full,true);
    }
    public void Dispose()=>state.Dispose();
}
