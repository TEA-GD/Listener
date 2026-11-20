using Microsoft.Data.Sqlite;
using System.Globalization;
internal static class NetworkTimeline
{
    internal sealed record Span(long Start,long End,string Label,string Type);
    internal static List<Span> Load(SqliteConnection db,string host,DateTimeOffset start,DateTimeOffset end,DateTimeOffset now)
    {
        if(ViewerDatabase.Query(db,"SELECT 1 FROM sqlite_master WHERE name='device_context_snapshots'").Count==0)return [];
        var rows=ViewerDatabase.Query(db,"""
            SELECT s.snapshot_id,s.timestamp_utc,s.status,n.network_type,n.network_name,n.adapter_name
            FROM device_context_snapshots s LEFT JOIN device_network_communications n ON n.host=s.host AND n.snapshot_id=s.snapshot_id
            WHERE s.host=$host AND s.timestamp_utc>=$from AND s.timestamp_utc<$to
              AND julianday(s.timestamp_utc)<=julianday($now)
              AND (SELECT count(*) FROM device_network_communications x WHERE x.host=s.host AND x.snapshot_id=s.snapshot_id)=s.network_count
            ORDER BY julianday(s.timestamp_utc),s.received_utc,s.snapshot_id,n.adapter_name
            """,new(){{"$host",host},{"$from",start.AddSeconds(-120).ToString("yyyy-MM-dd",CultureInfo.InvariantCulture)},{"$to",end.AddDays(1).ToString("yyyy-MM-dd",CultureInfo.InvariantCulture)},{"$now",now.ToString("o")}});
        var samples=rows.GroupBy(r=>r["snapshot_id"]!.ToString()).Select(group=>{
            var first=group.First();long time=DateTimeOffset.Parse(first["timestamp_utc"]!.ToString()!,CultureInfo.InvariantCulture).ToUnixTimeMilliseconds();
            var networks=group.Where(r=>r["network_type"] is not null).ToArray();
            string type=networks.Select(r=>r["network_type"]!.ToString()).Distinct().Count()==1?networks[0]["network_type"]!.ToString()!:"mixed";
            string label=string.Join(" · ",networks.Select(r=>(r["network_type"]?.ToString()??"other")+": "+(string.IsNullOrWhiteSpace(r["network_name"]?.ToString())?r["adapter_name"]?.ToString():r["network_name"]?.ToString())));
            if(first["status"]?.ToString()!="ok")label+=" (partial sample)";
            return (time,type,label,known:networks.Length>0);
        }).OrderBy(s=>s.time).ToArray();
        var result=new List<Span>();
        for(int i=0;i<samples.Length;i++){
            var sample=samples[i];if(!sample.known)continue;
            long a=Math.Max(start.ToUnixTimeMilliseconds(),sample.time),b=Math.Min(Math.Min(end.ToUnixTimeMilliseconds(),now.ToUnixTimeMilliseconds()),sample.time+120000);
            if(i+1<samples.Length)b=Math.Min(b,samples[i+1].time);
            if(b<=a)continue;
            if(result.Count>0&&result[^1].End==a&&result[^1].Label==sample.label&&result[^1].Type==sample.type)result[^1]=result[^1] with {End=b};
            else result.Add(new(a,b,sample.label,sample.type));
        }
        return result;
    }
}
