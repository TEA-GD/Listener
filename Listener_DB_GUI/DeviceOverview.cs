using System.Globalization;
using System.Net;
using Microsoft.AspNetCore.Http;
using static ReportedDeviceState;

internal static class DeviceOverview
{
    public static IResult Read(HttpRequest request,ViewerDatabase database,DeviceAvailability availability)
    {
        string host=request.Query["host"].ToString();
        if(string.IsNullOrWhiteSpace(host)||host.Length>120)throw new ArgumentException("Choose a valid PC.");
        int page=1;
        if(request.Query.ContainsKey("page")&&(!int.TryParse(request.Query["page"],out page)||page<1||page>1000000))throw new ArgumentException("Invalid page.");
        var now=DateTimeOffset.UtcNow;
        var minute=new DateTimeOffset(now.Year,now.Month,now.Day,now.Hour,now.Minute,0,TimeSpan.Zero);
        var start=minute.AddMinutes(-59);
        using var db=database.Open();
        var state=ReportedDeviceState.Read(db,host,now);
        var latest=state.Latest;
        if(latest is null)return Results.NotFound(new{error="Device not found"});
        var args=state.Parameters;
        var baseline=state.Baseline;
        var current=state.Current;
        var ips=current.Values.Select(r=>Text(r,"SOURCE_IP")).Where(ip=>IPAddress.TryParse(ip,out var parsed)&&!IPAddress.IsLoopback(parsed)&&!parsed.Equals(IPAddress.Any)&&!parsed.Equals(IPAddress.IPv6Any)).Distinct().Order().ToArray();
        string ipSource="current_sockets";
        if(ips.Length==0){
            var previous=ViewerDatabase.Query(db,"SELECT SOURCE_IP FROM events WHERE "+ReportedDeviceState.TimedScope+" AND SOURCE_IP NOT IN ('','0.0.0.0','::','127.0.0.1','::1') ORDER BY date_utc DESC,TIMESTAMP_UTC DESC LIMIT 64",args);
            ips=previous.Select(r=>Text(r,"SOURCE_IP")).Where(ip=>IPAddress.TryParse(ip,out var parsed)&&!IPAddress.IsLoopback(parsed)).Distinct().Take(1).ToArray();
            ipSource="last_reported";
        }
        var chartArgs=new Dictionary<string,object>(args){{"$start",start.ToString("o")},{"$end",now.ToString("o")},{"$dayStart",start.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture)},{"$dayEnd",now.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture)}};
        var counts=ViewerDatabase.Query(db,"SELECT strftime('%Y-%m-%dT%H:%M:00Z',TIMESTAMP_UTC) AS minute, SUM(CASE WHEN EVENT_TYPE='TCP_CONNECTED' THEN 1 ELSE 0 END) AS tcp, SUM(CASE WHEN EVENT_TYPE='UDP_ENDPOINT_OPENED' THEN 1 ELSE 0 END) AS udp FROM events WHERE "+ReportedDeviceState.TimedScope+" AND date_utc BETWEEN $dayStart AND $dayEnd AND EVENT_TYPE IN ('TCP_CONNECTED','UDP_ENDPOINT_OPENED') AND julianday(TIMESTAMP_UTC)>=julianday($start) AND julianday(TIMESTAMP_UTC)<=julianday($end) GROUP BY minute",chartArgs).ToDictionary(r=>Text(r,"minute"));
        var buckets=Enumerable.Range(0,60).Select(i=>{
            string label=start.AddMinutes(i).ToString("yyyy-MM-ddTHH:mm:00Z",CultureInfo.InvariantCulture);counts.TryGetValue(label,out var value);
            return new{minute=label,tcp=value is null?0:Convert.ToInt32(value["tcp"]),udp=value is null?0:Convert.ToInt32(value["udp"])};
        }).ToArray();
        string status=state.Status;
        bool incomplete=state.Incomplete;
        var rowsOut=current.Values.OrderBy(r=>Text(r,"PROTOCOL")).ThenBy(r=>Text(r,"PROCESS_NAME")).ThenBy(r=>Text(r,"SOURCE_IP")).ThenBy(r=>Text(r,"SOURCE_PORT")).ThenBy(r=>Text(r,"RECIEVER_IP")).ThenBy(r=>Text(r,"PORT_USED")).ThenBy(r=>Text(r,"PROCESS_ID")).Skip((page-1)*50).Take(50).ToArray();
        return Results.Ok(new{host,availability=availability.Summary(db,host,now),generated_utc=now.ToString("o"),status,last_event_utc=Text(latest,"TIMESTAMP_UTC"),last_received_utc=Text(latest,"received_utc"),snapshot_utc=baseline is null?null:Text(baseline,"TIMESTAMP_UTC"),incomplete_snapshot=incomplete,device_ips=ips,ip_source=ipSource,collector_peer_ip=Text(latest,"COLLECTOR_PEER_IP"),total=current.Count,tcp=current.Values.Count(r=>Text(r,"PROTOCOL")=="TCP"),udp=current.Values.Count(r=>Text(r,"PROTOCOL")=="UDP"),page,size=50,connections=rowsOut,buckets,average_tcp_per_minute=Math.Round(buckets.Sum(b=>b.tcp)/60.0,2),peak_tcp_per_minute=buckets.Max(b=>b.tcp)});
    }
}
