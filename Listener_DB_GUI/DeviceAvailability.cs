using System.Globalization;
using Microsoft.Data.Sqlite;

// Availability is inferred from heartbeats, never from receipt time or socket counts.
internal sealed class DeviceAvailability(int timeoutSeconds)
{
    internal sealed record Session(long Start,long Last)
    {
        internal long End(int timeout)=>Last+timeout*1000L;
    }
    internal sealed record History(List<Session> Sessions,long? First,Session? Latest);
    internal History Load(SqliteConnection db,string host,DateTimeOffset now,long rangeStart,long rangeEnd)
    {
        const string sql="""
            WITH stamps AS (
                SELECT DISTINCT CAST(ROUND((julianday(TIMESTAMP_UTC)-2440587.5)*86400000) AS INTEGER) AS t
                FROM events WHERE host=$host AND EVENT_TYPE='HEARTBEAT'
            ), valid AS (SELECT t FROM stamps WHERE t IS NOT NULL AND t<=$now),
            previous AS (SELECT t,LAG(t) OVER(ORDER BY t) AS p FROM valid),
            grouped AS (SELECT t,SUM(CASE WHEN p IS NULL OR t-p>$timeout THEN 1 ELSE 0 END) OVER(ORDER BY t) AS g FROM previous),
            sessions AS (SELECT MIN(t) AS started,MAX(t) AS last FROM grouped GROUP BY g),
            annotated AS (SELECT *,MIN(started) OVER() AS first,MAX(last) OVER() AS newest FROM sessions)
            SELECT started,last,first,newest FROM annotated
            WHERE (last+$timeout>$start AND started<$end) OR last=newest ORDER BY started
            """;
        var rows=ViewerDatabase.Query(db,sql,new(){{"$host",host},{"$now",now.ToUnixTimeMilliseconds()},{"$timeout",timeoutSeconds*1000L},{"$start",rangeStart},{"$end",rangeEnd}});
        var sessions=rows.Select(r=>new Session(Convert.ToInt64(r["started"]),Convert.ToInt64(r["last"]))).ToList();
        return new(sessions,rows.Count==0?null:Convert.ToInt64(rows[0]["first"]),sessions.Count==0?null:sessions.MaxBy(s=>s.Last));
    }
    internal object Summary(History history,DateTimeOffset now)
    {
        var last=history.Latest;long stamp=now.ToUnixTimeMilliseconds();
        bool online=last is not null&&stamp<last.End(timeoutSeconds);
        string? Iso(long? value)=>value is null?null:DateTimeOffset.FromUnixTimeMilliseconds(value.Value).ToString("o");
        return new{status=last is null?"unknown":online?"online":"offline",timeout_seconds=timeoutSeconds,generated_utc=now.ToString("o"),first_seen_utc=Iso(history.First),last_heartbeat_utc=Iso(last?.Last),online_since_utc=online?Iso(last!.Start):null,offline_since_utc=last is not null&&!online?Iso(last.End(timeoutSeconds)):null,current_online_seconds=online?(stamp-last!.Start)/1000:0,last_session_start_utc=Iso(last?.Start),last_session_end_utc=last is null?null:Iso(Math.Min(stamp,last.End(timeoutSeconds))),last_session_online_seconds=last is null?0:(Math.Min(stamp,last.End(timeoutSeconds))-last.Start)/1000};
    }
    internal object Summary(SqliteConnection db,string host,DateTimeOffset now)=>Summary(Load(db,host,now,now.ToUnixTimeMilliseconds(),now.ToUnixTimeMilliseconds()),now);

    public IResult Read(HttpRequest request,ViewerDatabase database)
    {
        string host=request.Query["host"].ToString();if(string.IsNullOrWhiteSpace(host)||host.Length>120)throw new ArgumentException("Choose a valid device.");
        string zoneId=request.Query["tz"].ToString();if(zoneId.Length==0)zoneId="UTC";
        TimeZoneInfo zone;
        try{zone=TimeZoneInfo.FindSystemTimeZoneById(zoneId);}catch(TimeZoneNotFoundException){throw new ArgumentException("Unknown time zone. Use an IANA name such as Europe/Moscow or UTC.");}catch(InvalidTimeZoneException){throw new ArgumentException("Invalid time zone.");}
        var now=DateTimeOffset.UtcNow;
        var today=DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now,zone).DateTime);
        DateOnly Date(string key,DateOnly fallback){string value=request.Query[key].ToString();if(value.Length==0)return fallback;if(!DateOnly.TryParseExact(value,"yyyy-MM-dd",CultureInfo.InvariantCulture,DateTimeStyles.None,out var parsed))throw new ArgumentException("Dates must use yyyy-MM-dd.");return parsed;}
        var from=Date("from",today.AddDays(-6));var to=Date("to",today);
        if(from>to||to>today||to.DayNumber-from.DayNumber>=90)throw new ArgumentException("Choose a range of 1–90 days ending no later than today in the selected time zone.");
        DateTimeOffset Midnight(DateOnly day){
            var local=day.ToDateTime(TimeOnly.MinValue,DateTimeKind.Unspecified);
            // Some zones move clocks at midnight. Use the first valid local instant.
            for(int i=0;zone.IsInvalidTime(local)&&i<1440;i++)local=local.AddMinutes(1);
            if(zone.IsAmbiguousTime(local))return new DateTimeOffset(local,zone.GetAmbiguousTimeOffsets(local).Max()).ToUniversalTime();
            return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local,zone));
        }
        var start=Midnight(from);var end=Midnight(to.AddDays(1));
        using var db=database.Open();
        if(ViewerDatabase.Query(db,"SELECT 1 FROM events WHERE host=$host LIMIT 1",new(){{"$host",host}}).Count==0)return Results.NotFound(new{error="Device not found"});
        var history=Load(db,host,now,start.ToUnixTimeMilliseconds(),end.ToUnixTimeMilliseconds());
        long nowMs=now.ToUnixTimeMilliseconds();
        var days=new List<object>();long totalOnline=0,totalOffline=0;
        for(var date=from;date<=to;date=date.AddDays(1)){
            long a=Midnight(date).ToUnixTimeMilliseconds(),b=Midnight(date.AddDays(1)).ToUnixTimeMilliseconds();
            var available=history.Sessions.Where(s=>s.Start<b&&s.End(timeoutSeconds)>a).Select(s=>(Start:Math.Max(a,s.Start),End:Math.Min(Math.Min(b,nowMs),s.End(timeoutSeconds)))).Where(s=>s.End>s.Start).ToArray();
            var cuts=new SortedSet<long>{a,b};
            if(nowMs>a&&nowMs<b)cuts.Add(nowMs);
            if(history.First is long first&&first>a&&first<b)cuts.Add(first);
            foreach(var s in available){cuts.Add(s.Start);cuts.Add(s.End);}
            long online=0,offline=0;var segments=new List<object>();var points=cuts.ToArray();
            string Iso(long t)=>DateTimeOffset.FromUnixTimeMilliseconds(t).ToString("o");
            string Local(long t)=>TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeMilliseconds(t),zone).ToString("yyyy-MM-dd HH:mm:ss zzz",CultureInfo.InvariantCulture);
            for(int i=0;i+1<points.Length;i++){
                long left=points[i],right=points[i+1];if(right<=left)continue;
                string state=left>=nowMs?"future":history.First is null||right<=history.First.Value?"unknown":available.Any(s=>s.Start<=left&&s.End>=right)?"online":"offline";
                if(state=="online")online+=right-left;else if(state=="offline")offline+=right-left;
                segments.Add(new{status=state,start_utc=Iso(left),end_utc=Iso(right),start_local=Local(left),end_local=Local(right),offset_seconds=(left-a)/1000.0,duration_seconds=(right-left)/1000.0});
            }
            var ticks=new List<object>();for(long t=a;t<b;t+=3600000L)ticks.Add(new{offset_seconds=(t-a)/1000.0,label=TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeMilliseconds(t),zone).ToString("HH:mm",CultureInfo.InvariantCulture)});
            ticks.Add(new{offset_seconds=(b-a)/1000.0,label="24:00"});
            totalOnline+=online;totalOffline+=offline;
            days.Add(new{date=date.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture),day_seconds=(b-a)/1000.0,online_seconds=online/1000.0,offline_seconds=offline/1000.0,segments,ticks});
        }
        return Results.Ok(new{host,timezone=zoneId,from=from.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture),to=to.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture),generated_utc=now.ToString("o"),summary=Summary(history,now),total_online_seconds=totalOnline/1000.0,total_offline_seconds=totalOffline/1000.0,days});
    }
}
