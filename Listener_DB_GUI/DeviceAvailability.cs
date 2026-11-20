using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Caching.Memory;

// Working time follows heartbeat event timestamps. Receipt time only classifies delivery delay.
internal sealed class DeviceAvailability(int timeoutSeconds,int deliveryDelaySeconds=120) : IDisposable
{
    private readonly MemoryCache historyCache=new(new MemoryCacheOptions { SizeLimit=200000 });
    public void Dispose()=>historyCache.Dispose();
    private History Slice(History history,long start,long end)=>history with {
        Sessions=history.Sessions.Where(s=>(s.End(timeoutSeconds)>start&&s.Start<end)||s==history.Latest).ToList(),
        Buffered=history.Buffered.Where(s=>s.End>start&&s.Start<end).ToList()
    };
    internal sealed record Session(long Start,long Last)
    {
        internal long End(int timeout)=>Last+timeout*1000L;
    }
    internal sealed record Interval(long Start,long End);
    internal sealed record History(List<Session> Sessions,long? First,Session? Latest,List<Interval> Buffered);
    internal History Load(SqliteConnection db,string host,DateTimeOffset now,long rangeStart,long rangeEnd)
    {
        bool summarized=ViewerDatabase.HasReadModel(db);
        bool deliveryModel=summarized&&ViewerDatabase.Query(db,"SELECT 1 FROM gui_schema WHERE version=2").Count>0;
        object? cacheKey=null;
        if(summarized){
            var version=ViewerDatabase.Query(db,"SELECT heartbeat_revision,(SELECT MAX(stamp_ms) FROM gui_heartbeats WHERE host=$host AND stamp_ms<=$now) AS eligible FROM gui_devices WHERE host=$host",
                new(){{"$host",host},{"$now",now.ToUnixTimeMilliseconds()}}).FirstOrDefault();
            cacheKey=(db.DataSource,deliveryModel,host,version?.GetValueOrDefault("heartbeat_revision"),version?.GetValueOrDefault("eligible"));
            if(historyCache.TryGetValue(cacheKey,out History? cached)&&cached is not null)return Slice(cached,rangeStart,rangeEnd);
        }
        string stamps=deliveryModel?"SELECT h.stamp_ms AS t,d.received_ms AS receipt FROM gui_heartbeats h LEFT JOIN gui_heartbeat_delivery d ON d.host=h.host AND d.stamp_ms=h.stamp_ms WHERE h.host=$host":
            "SELECT CAST(ROUND((julianday(TIMESTAMP_UTC)-2440587.5)*86400000) AS INTEGER) AS t,MIN(CAST(ROUND((julianday(received_utc)-2440587.5)*86400000) AS INTEGER)) AS receipt FROM events WHERE host=$host AND EVENT_TYPE='HEARTBEAT' GROUP BY t";
        string sql="WITH stamps AS ("+stamps+"),"+"""
            valid AS (SELECT t,receipt FROM stamps WHERE t IS NOT NULL AND t<=$now),
            ordered AS (SELECT t,receipt,LAG(t) OVER(ORDER BY t) AS p,LEAD(t) OVER(ORDER BY t) AS n FROM valid),
            grouped AS (SELECT t,SUM(CASE WHEN p IS NULL OR t-p>$timeout THEN 1 ELSE 0 END) OVER(ORDER BY t) AS g FROM ordered),
            sessions AS (SELECT MIN(t) AS started,MAX(t) AS last FROM grouped GROUP BY g),
            pieces AS (SELECT t AS started,MIN(t+$timeout,COALESCE(n,t+$timeout)) AS ended FROM ordered WHERE receipt-t>$delay),
            cuts AS (SELECT *,CASE WHEN started<=LAG(ended) OVER(ORDER BY started) THEN 0 ELSE 1 END AS cut FROM pieces),
            colored AS (SELECT *,SUM(cut) OVER(ORDER BY started) AS g FROM cuts),
            buffered AS (SELECT MIN(started) AS started,MAX(ended) AS last FROM colored GROUP BY g)
            SELECT 'working' AS kind,started,last FROM sessions
            UNION ALL SELECT 'buffered',started,last FROM buffered ORDER BY kind,started
            """;
        var rows=ViewerDatabase.Query(db,sql,new(){{"$host",host},{"$now",now.ToUnixTimeMilliseconds()},{"$timeout",timeoutSeconds*1000L},{"$delay",deliveryDelaySeconds*1000L}});
        var sessions=rows.Where(r=>(string)r["kind"]! == "working").Select(r=>new Session(Convert.ToInt64(r["started"]),Convert.ToInt64(r["last"]))).ToList();
        var buffered=rows.Where(r=>(string)r["kind"]! == "buffered").Select(r=>new Interval(Convert.ToInt64(r["started"]),Convert.ToInt64(r["last"]))).ToList();
        var history=new History(sessions,sessions.Count==0?null:sessions[0].Start,sessions.Count==0?null:sessions[^1],buffered);
        int weight=sessions.Count+buffered.Count;
        if(cacheKey is not null&&weight<=200000)historyCache.Set(cacheKey,history,new MemoryCacheEntryOptions {
            Size=Math.Max(1,weight),AbsoluteExpirationRelativeToNow=TimeSpan.FromMinutes(15)
        });
        return Slice(history,rangeStart,rangeEnd);
    }
    internal object Summary(History history,DateTimeOffset now)
    {
        var last=history.Latest;long stamp=now.ToUnixTimeMilliseconds();
        bool online=last is not null&&stamp<last.End(timeoutSeconds);
        string? Iso(long? value)=>value is null?null:DateTimeOffset.FromUnixTimeMilliseconds(value.Value).ToString("o");
        return new{status=last is null?"unknown":online?"online":"offline",timeout_seconds=timeoutSeconds,delivery_delay_seconds=deliveryDelaySeconds,generated_utc=now.ToString("o"),first_seen_utc=Iso(history.First),last_heartbeat_utc=Iso(last?.Last),online_since_utc=online?Iso(last!.Start):null,offline_since_utc=last is not null&&!online?Iso(last.End(timeoutSeconds)):null,current_online_seconds=online?(stamp-last!.Start)/1000:0,last_session_start_utc=Iso(last?.Start),last_session_end_utc=last is null?null:Iso(Math.Min(stamp,last.End(timeoutSeconds))),last_session_online_seconds=last is null?0:(Math.Min(stamp,last.End(timeoutSeconds))-last.Start)/1000};
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
        var from=Date("from",today.AddDays(-2));var to=Date("to",today);
        if(from>to||to>today||to.DayNumber-from.DayNumber>=90)throw new ArgumentException("Choose a range of 1–90 days ending no later than today in the selected time zone.");
        DateTimeOffset Midnight(DateOnly day){
            var local=day.ToDateTime(TimeOnly.MinValue,DateTimeKind.Unspecified);
            // Some zones move clocks at midnight. Use the first valid local instant.
            for(int i=0;zone.IsInvalidTime(local)&&i<1440;i++)local=local.AddMinutes(1);
            if(zone.IsAmbiguousTime(local))return new DateTimeOffset(local,zone.GetAmbiguousTimeOffsets(local).Max()).ToUniversalTime();
            return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local,zone));
        }
        int Number(string key,int fallback,int max){string value=request.Query[key].ToString();if(value=="")return fallback;if(!int.TryParse(value,out int n)||n<1||n>max)throw new ArgumentException("Invalid timeline page or days per page");return n;}
        int perPage=Number("days",90,90),totalDays=to.DayNumber-from.DayNumber+1,pages=(totalDays+perPage-1)/perPage;
        int page=Number("page",1,90);if(page>pages)throw new ArgumentException("Timeline page is outside the selected dates");
        var visibleFrom=from.AddDays((page-1)*perPage);var visibleTo=DateOnly.FromDayNumber(Math.Min(to.DayNumber,visibleFrom.DayNumber+perPage-1));
        var start=Midnight(from);var end=Midnight(to.AddDays(1));
        using var db=database.Open();
        if(ViewerDatabase.Query(db,"SELECT 1 FROM events WHERE host=$host LIMIT 1",new(){{"$host",host}}).Count==0)return Results.NotFound(new{error="Device not found"});
        var history=Load(db,host,now,start.ToUnixTimeMilliseconds(),end.ToUnixTimeMilliseconds());
        var network=NetworkTimeline.Load(db,host,Midnight(visibleFrom),Midnight(visibleTo.AddDays(1)),now);
        long nowMs=now.ToUnixTimeMilliseconds();
        var days=new List<object>();long totalOnline=0,totalOffline=0,totalBuffered=0;
        for(var date=from;date<=to;date=date.AddDays(1)){
            long a=Midnight(date).ToUnixTimeMilliseconds(),b=Midnight(date.AddDays(1)).ToUnixTimeMilliseconds();
            var available=history.Sessions.Where(s=>s.Start<b&&s.End(timeoutSeconds)>a).Select(s=>(Start:Math.Max(a,s.Start),End:Math.Min(Math.Min(b,nowMs),s.End(timeoutSeconds)))).Where(s=>s.End>s.Start).ToArray();
            var buffered=history.Buffered.Where(s=>s.Start<b&&s.End>a).Select(s=>(Start:Math.Max(a,s.Start),End:Math.Min(Math.Min(b,nowMs),s.End))).Where(s=>s.End>s.Start).ToArray();
            var cuts=new SortedSet<long>{a,b};
            if(nowMs>a&&nowMs<b)cuts.Add(nowMs);
            if(history.First is long first&&first>a&&first<b)cuts.Add(first);
            foreach(var s in available){cuts.Add(s.Start);cuts.Add(s.End);}
            foreach(var s in buffered){cuts.Add(s.Start);cuts.Add(s.End);}
            long online=0,offline=0,bufferedTime=0;var segments=new List<object>();var points=cuts.ToArray();
            string Iso(long t)=>DateTimeOffset.FromUnixTimeMilliseconds(t).ToString("o");
            string Local(long t)=>TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeMilliseconds(t),zone).ToString("yyyy-MM-dd HH:mm:ss zzz",CultureInfo.InvariantCulture);
            for(int i=0;i+1<points.Length;i++){
                long left=points[i],right=points[i+1];if(right<=left)continue;
                string state=left>=nowMs?"future":history.First is null||right<=history.First.Value?"unknown":available.Any(s=>s.Start<=left&&s.End>=right)?buffered.Any(s=>s.Start<=left&&s.End>=right)?"buffered":"online":"offline";
                if(state is "online" or "buffered")online+=right-left;
                if(state=="buffered")bufferedTime+=right-left;else if(state=="offline")offline+=right-left;
                segments.Add(new{status=state,start_utc=Iso(left),end_utc=Iso(right),start_local=Local(left),end_local=Local(right),offset_seconds=(left-a)/1000.0,duration_seconds=(right-left)/1000.0});
            }
            var ticks=new List<object>();for(long t=a;t<b;t+=3600000L)ticks.Add(new{offset_seconds=(t-a)/1000.0,label=TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeMilliseconds(t),zone).ToString("HH:mm",CultureInfo.InvariantCulture)});
            ticks.Add(new{offset_seconds=(b-a)/1000.0,label="24:00"});
            totalOnline+=online;totalOffline+=offline;totalBuffered+=bufferedTime;
            if(date<visibleFrom||date>visibleTo)continue;
            var networkSegments=network.Where(n=>n.Start<b&&n.End>a).Select(n=>new{label=n.Label,type=n.Type,start_utc=DateTimeOffset.FromUnixTimeMilliseconds(Math.Max(a,n.Start)).ToString("o"),end_utc=DateTimeOffset.FromUnixTimeMilliseconds(Math.Min(b,n.End)).ToString("o"),offset_seconds=(Math.Max(a,n.Start)-a)/1000.0,duration_seconds=(Math.Min(b,n.End)-Math.Max(a,n.Start))/1000.0}).ToArray();
            days.Add(new{network_segments=networkSegments,date=date.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture),day_seconds=(b-a)/1000.0,online_seconds=online/1000.0,buffered_seconds=bufferedTime/1000.0,connected_seconds=(online-bufferedTime)/1000.0,offline_seconds=offline/1000.0,segments,ticks});
        }
        return Results.Ok(new{host,timezone=zoneId,page,days_per_page=perPage,total_days=totalDays,pages,from=from.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture),to=to.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture),generated_utc=now.ToString("o"),summary=Summary(history,now),total_online_seconds=totalOnline/1000.0,total_buffered_seconds=totalBuffered/1000.0,total_connected_seconds=(totalOnline-totalBuffered)/1000.0,total_offline_seconds=totalOffline/1000.0,days});
    }
}
