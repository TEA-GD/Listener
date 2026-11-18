using System.Globalization;
using System.Net;
using static ReportedDeviceState;

internal static class LastMinuteSnapshot
{
    public static IResult Read(HttpRequest request,ViewerDatabase database)
    {
        string host=request.Query["host"].ToString();
        if(string.IsNullOrWhiteSpace(host)||host.Length>120)throw new ArgumentException("Choose a valid PC.");
        var at=DateTimeOffset.UtcNow;
        if(request.Query.ContainsKey("at")){
            string input=request.Query["at"].ToString();
            if(!(input.EndsWith('Z')||input.EndsWith("+00:00"))||!DateTimeOffset.TryParse(input,CultureInfo.InvariantCulture,DateTimeStyles.None,out at)||at.Offset!=TimeSpan.Zero||at>DateTimeOffset.UtcNow.AddSeconds(2))throw new ArgumentException("Capture time must be a valid UTC timestamp that is not in the future.");
        }
        int page=1;
        if(request.Query.ContainsKey("page")&&(!int.TryParse(request.Query["page"],out page)||page<1||page>1000000))throw new ArgumentException("Invalid page.");
        long? watermark=null;
        if(request.Query.ContainsKey("watermark")){
            if(!request.Query.ContainsKey("at")||!long.TryParse(request.Query["watermark"],out long parsed)||parsed<0)throw new ArgumentException("A valid capture time and nonnegative watermark are required.");
            watermark=parsed;
        }
        var start=at.AddSeconds(-60);
        using var db=database.Open();
        var state=ReportedDeviceState.Read(db,host,at,watermark);
        if(state.Latest is null)return Results.NotFound(new{error="Device not found at this capture time"});
        var parameters=new Dictionary<string,object>(state.Parameters){{"$start",start.ToString("o")},{"$day",start.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture)}};
        var observations=ViewerDatabase.Query(db,"SELECT "+ViewerDatabase.EventColumns+" FROM events WHERE "+TimedScope+" AND date_utc>=$day AND julianday(TIMESTAMP_UTC)>=julianday($start) AND EVENT_TYPE IN ('TCP_CONNECTED','TCP_DISCONNECTED','ACTIVE_AT_START','UDP_ENDPOINT_AT_START','UDP_ENDPOINT_OPENED','UDP_ENDPOINT_CLOSED','TCP_SNAPSHOT','UDP_SNAPSHOT') ORDER BY julianday(TIMESTAMP_UTC),CASE WHEN EVENT_TYPE IN ('TCP_DISCONNECTED','UDP_ENDPOINT_CLOSED') THEN 1 ELSE 0 END,EVENT_ID",parameters);
        var entries=new Dictionary<string,Entry>();
        foreach(var row in observations){
            string key=Key(row);
            if(!entries.TryGetValue(key,out var entry))entries[key]=entry=new Entry(row);
            entry.Last=row;entry.Count++;
        }
        var reportTime=Stamp(Text(state.Latest,"TIMESTAMP_UTC"));
        bool recentState=state.Baseline is not null&&reportTime>=start&&reportTime<=at;
        if(recentState){
            foreach(var row in state.Current.Values){
                string key=Key(row);
                if(!entries.ContainsKey(key))entries[key]=new Entry(row);
            }
        }
        var all=new List<Dictionary<string,object?>>();
        foreach(var (key,entry) in entries){
            bool present=recentState&&state.Current.ContainsKey(key);
            bool closed=Text(entry.Last,"EVENT_TYPE") is "TCP_DISCONNECTED" or "UDP_ENDPOINT_CLOSED";
            string label=present?"Present at latest report":closed?"Closed during minute":recentState&&!state.Incomplete?"Not in latest snapshot":"Observed; current state unknown";
            var row=new Dictionary<string,object?>(present?state.Current[key]:entry.Last){
                ["window_status"]=label,["present_at_latest"]=present,["first_observed_utc"]=Text(entry.First,"TIMESTAMP_UTC"),
                ["last_observed_utc"]=Text(entry.Last,"TIMESTAMP_UTC"),["observations_in_minute"]=entry.Count
            };
            all.Add(row);
        }
        var ordered=all.OrderBy(r=>Text(r,"PROTOCOL")).ThenBy(r=>Text(r,"PROCESS_NAME")).ThenBy(r=>Key(r)).ToArray();
        var ips=all.Select(r=>Text(r,"SOURCE_IP")).Where(ip=>IPAddress.TryParse(ip,out var parsed)&&!IPAddress.IsLoopback(parsed)&&!parsed.Equals(IPAddress.Any)&&!parsed.Equals(IPAddress.IPv6Any)).Distinct().Order().ToArray();
        return Results.Ok(new{host,captured_utc=at.ToString("o"),from_utc=start.ToString("o"),watermark=state.Watermark,status=state.Status,snapshot_utc=state.Baseline is null?null:Text(state.Baseline,"TIMESTAMP_UTC"),last_event_utc=Text(state.Latest,"TIMESTAMP_UTC"),last_received_utc=Text(state.Latest,"received_utc"),device_ips=ips,collector_peer_ip=Text(state.Latest,"COLLECTOR_PEER_IP"),total=all.Count,present=all.Count(r=>(bool)r["present_at_latest"]!),closed=all.Count(r=>Text(r,"window_status")=="Closed during minute"),page,size=50,connections=ordered.Skip((page-1)*50).Take(50).ToArray()});
    }
    private sealed class Entry(Dictionary<string,object?> first)
    {
        internal Dictionary<string,object?> First=first,Last=first;
        internal int Count;
    }
}
