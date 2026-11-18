using System.Globalization;
using System.Net;
using Microsoft.AspNetCore.Http;

internal static class DeviceOverview
{
    const string Prefix="SNAPSHOT_COMPLETE:";
    static string Text(Dictionary<string,object?> row,string field)=>row.GetValueOrDefault(field)?.ToString()??"";
    static DateTimeOffset Stamp(string value)=>DateTimeOffset.Parse(value,CultureInfo.InvariantCulture,DateTimeStyles.AssumeUniversal);
    static string Key(Dictionary<string,object?> row)=>string.Join('\u001f',new[]{"PROTOCOL","SOURCE_IP","SOURCE_PORT","RECIEVER_IP","PORT_USED","PROCESS_ID"}.Select(f=>Text(row,f)));
    public static IResult Read(HttpRequest request,ViewerDatabase database)
    {
        string host=request.Query["host"].ToString();
        if(string.IsNullOrWhiteSpace(host)||host.Length>120)throw new ArgumentException("Choose a valid PC.");
        int page=1;
        if(request.Query.ContainsKey("page")&&(!int.TryParse(request.Query["page"],out page)||page<1||page>1000000))throw new ArgumentException("Invalid page.");
        var now=DateTimeOffset.UtcNow;
        var minute=new DateTimeOffset(now.Year,now.Month,now.Day,now.Hour,now.Minute,0,TimeSpan.Zero);
        var start=minute.AddMinutes(-59);
        using var db=database.Open();
        var args=new Dictionary<string,object>{{"$host",host}};
        var latest=ViewerDatabase.Query(db,"SELECT TIMESTAMP_UTC,received_utc,COLLECTOR_PEER_IP FROM events WHERE host=$host ORDER BY julianday(TIMESTAMP_UTC) DESC,EVENT_ID DESC LIMIT 1",args).FirstOrDefault();
        if(latest is null)return Results.NotFound(new{error="Device not found"});
        var markers=ViewerDatabase.Query(db,"SELECT SNAPSHOT_ID,TIMESTAMP_UTC,STATE FROM events WHERE host=$host AND EVENT_TYPE='HEARTBEAT' AND STATE LIKE 'SNAPSHOT_COMPLETE:%' ORDER BY TIMESTAMP_UTC DESC,EVENT_ID DESC LIMIT 100",args);
        Dictionary<string,object?>? baseline=null;
        var current=new Dictionary<string,Dictionary<string,object?>>();
        foreach(var marker in markers){
            if(!int.TryParse(Text(marker,"STATE")[Prefix.Length..],out int expected)||expected<0||expected>100000)continue;
            var parameters=new Dictionary<string,object>(args){{"$snapshot",Text(marker,"SNAPSHOT_ID")}};
            var rows=ViewerDatabase.Query(db,"SELECT "+ViewerDatabase.EventColumns+" FROM events WHERE host=$host AND SNAPSHOT_ID=$snapshot AND EVENT_TYPE IN ('TCP_SNAPSHOT','UDP_SNAPSHOT') LIMIT 100001",parameters);
            if(rows.Count!=expected)continue; // Never treat a partially delivered snapshot as complete.
            foreach(var row in rows)current[Key(row)]=row;
            if(current.Count!=expected){current.Clear();continue;}
            baseline=marker;break;
        }
        if(baseline is not null){
            var parameters=new Dictionary<string,object>(args){{"$snapshot",Text(baseline,"SNAPSHOT_ID")},{"$since",Text(baseline,"TIMESTAMP_UTC")},{"$sinceDay",Text(baseline,"TIMESTAMP_UTC")[..10]}};
            // Snapshot IDs originate at poll start; excluding the baseline poll avoids
            // replaying startup deltas that the authoritative baseline already contains.
            var changes=ViewerDatabase.Query(db,"SELECT "+ViewerDatabase.EventColumns+" FROM events WHERE host=$host AND date_utc>=$sinceDay AND SNAPSHOT_ID<>$snapshot AND julianday(TIMESTAMP_UTC)>=julianday($since) AND EVENT_TYPE IN ('TCP_CONNECTED','TCP_DISCONNECTED','UDP_ENDPOINT_OPENED','UDP_ENDPOINT_CLOSED') ORDER BY julianday(TIMESTAMP_UTC),CASE WHEN EVENT_TYPE IN ('TCP_DISCONNECTED','UDP_ENDPOINT_CLOSED') THEN 1 ELSE 0 END,EVENT_ID",parameters);
            foreach(var row in changes){
                string key=Key(row);
                if(Text(row,"EVENT_TYPE") is "TCP_DISCONNECTED" or "UDP_ENDPOINT_CLOSED")current.Remove(key);
                else current[key]=row;
            }
        }
        var ips=current.Values.Select(r=>Text(r,"SOURCE_IP")).Where(ip=>IPAddress.TryParse(ip,out var parsed)&&!IPAddress.IsLoopback(parsed)&&!parsed.Equals(IPAddress.Any)&&!parsed.Equals(IPAddress.IPv6Any)).Distinct().Order().ToArray();
        var chartArgs=new Dictionary<string,object>(args){{"$start",start.ToString("o")},{"$end",now.ToString("o")},{"$dayStart",start.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture)},{"$dayEnd",now.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture)}};
        var counts=ViewerDatabase.Query(db,"SELECT strftime('%Y-%m-%dT%H:%M:00Z',TIMESTAMP_UTC) AS minute, SUM(CASE WHEN EVENT_TYPE='TCP_CONNECTED' THEN 1 ELSE 0 END) AS tcp, SUM(CASE WHEN EVENT_TYPE='UDP_ENDPOINT_OPENED' THEN 1 ELSE 0 END) AS udp FROM events WHERE host=$host AND date_utc BETWEEN $dayStart AND $dayEnd AND EVENT_TYPE IN ('TCP_CONNECTED','UDP_ENDPOINT_OPENED') AND julianday(TIMESTAMP_UTC)>=julianday($start) AND julianday(TIMESTAMP_UTC)<=julianday($end) GROUP BY minute",chartArgs).ToDictionary(r=>Text(r,"minute"));
        var buckets=Enumerable.Range(0,60).Select(i=>{
            string label=start.AddMinutes(i).ToString("yyyy-MM-ddTHH:mm:00Z",CultureInfo.InvariantCulture);counts.TryGetValue(label,out var value);
            return new{minute=label,tcp=value is null?0:Convert.ToInt32(value["tcp"]),udp=value is null?0:Convert.ToInt32(value["udp"])};
        }).ToArray();
        double age=(now-Stamp(Text(latest,"TIMESTAMP_UTC"))).TotalSeconds;
        double? snapshotAge=baseline is null?null:(now-Stamp(Text(baseline,"TIMESTAMP_UTC"))).TotalSeconds;
        bool incomplete=markers.Count>0&&(baseline is null||Text(markers[0],"SNAPSHOT_ID")!=Text(baseline,"SNAPSHOT_ID"));
        if(baseline is not null){
            var newestRows=ViewerDatabase.Query(db,"SELECT SNAPSHOT_ID,TIMESTAMP_UTC FROM events WHERE host=$host AND EVENT_TYPE IN ('TCP_SNAPSHOT','UDP_SNAPSHOT') ORDER BY TIMESTAMP_UTC DESC LIMIT 1",args);
            if(newestRows.Count>0&&Text(newestRows[0],"SNAPSHOT_ID")!=Text(baseline,"SNAPSHOT_ID")&&Stamp(Text(newestRows[0],"TIMESTAMP_UTC"))>Stamp(Text(baseline,"TIMESTAMP_UTC")))incomplete=true;
        }
        string status=age < -120?"clock_skew":baseline is null?"snapshot_required":snapshotAge>120||age>120?"stale":incomplete?"updating":"fresh";
        var rowsOut=current.Values.OrderBy(r=>Text(r,"PROTOCOL")).ThenBy(r=>Text(r,"PROCESS_NAME")).ThenBy(r=>Text(r,"SOURCE_IP")).ThenBy(r=>Text(r,"SOURCE_PORT")).ThenBy(r=>Text(r,"RECIEVER_IP")).ThenBy(r=>Text(r,"PORT_USED")).ThenBy(r=>Text(r,"PROCESS_ID")).Skip((page-1)*50).Take(50).ToArray();
        return Results.Ok(new{host,generated_utc=now.ToString("o"),status,last_event_utc=Text(latest,"TIMESTAMP_UTC"),last_received_utc=Text(latest,"received_utc"),snapshot_utc=baseline is null?null:Text(baseline,"TIMESTAMP_UTC"),incomplete_snapshot=incomplete,device_ips=ips,collector_peer_ip=Text(latest,"COLLECTOR_PEER_IP"),total=current.Count,tcp=current.Values.Count(r=>Text(r,"PROTOCOL")=="TCP"),udp=current.Values.Count(r=>Text(r,"PROTOCOL")=="UDP"),page,size=50,connections=rowsOut,buckets,average_tcp_per_minute=Math.Round(buckets.Sum(b=>b.tcp)/60.0,2),peak_tcp_per_minute=buckets.Max(b=>b.tcp)});
    }
}
