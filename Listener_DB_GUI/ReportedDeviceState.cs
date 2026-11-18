using System.Globalization;
using Microsoft.Data.Sqlite;

internal sealed class ReportedDeviceState
{
    internal const string Scope="host=$host AND rowid<=$watermark AND julianday(received_utc)<=julianday($at)";
    internal const string TimedScope=Scope+" AND julianday(TIMESTAMP_UTC)<=julianday($at)";
    internal static string Text(Dictionary<string,object?> row,string field)=>row.GetValueOrDefault(field)?.ToString()??"";
    internal static DateTimeOffset Stamp(string value)=>DateTimeOffset.Parse(value,CultureInfo.InvariantCulture,DateTimeStyles.AssumeUniversal);
    internal static string Key(Dictionary<string,object?> row)=>string.Join('\u001f',new[]{"PROTOCOL","SOURCE_IP","SOURCE_PORT","RECIEVER_IP","PORT_USED","PROCESS_ID"}.Select(f=>Text(row,f)));
    internal Dictionary<string,object?>? Latest,Baseline;
    internal Dictionary<string,Dictionary<string,object?>> Current=new();
    internal bool Incomplete;
    internal string Status="snapshot_required";
    internal long Watermark;
    internal Dictionary<string,object> Parameters=new();

    internal static ReportedDeviceState Read(SqliteConnection db,string host,DateTimeOffset at,long? watermark=null)
    {
        const string prefix="SNAPSHOT_COMPLETE:";
        var state=new ReportedDeviceState();
        state.Watermark=watermark??Convert.ToInt64(ViewerDatabase.Query(db,"SELECT COALESCE(MAX(rowid),0) AS watermark FROM events WHERE host=$host",new(){{"$host",host}})[0]["watermark"]);
        var args=state.Parameters=new(){{"$host",host},{"$at",at.ToString("o")},{"$watermark",state.Watermark}};
        state.Latest=ViewerDatabase.Query(db,"SELECT TIMESTAMP_UTC,received_utc,COLLECTOR_PEER_IP FROM events WHERE "+Scope+" ORDER BY julianday(TIMESTAMP_UTC) DESC,EVENT_ID DESC LIMIT 1",args).FirstOrDefault();
        if(state.Latest is null)return state;
        var markers=ViewerDatabase.Query(db,"SELECT SNAPSHOT_ID,TIMESTAMP_UTC,STATE FROM events WHERE "+TimedScope+" AND EVENT_TYPE='HEARTBEAT' AND STATE LIKE 'SNAPSHOT_COMPLETE:%' ORDER BY TIMESTAMP_UTC DESC,EVENT_ID DESC LIMIT 100",args);
        foreach(var marker in markers){
            if(!int.TryParse(Text(marker,"STATE")[prefix.Length..],out int expected)||expected<0||expected>100000)continue;
            var parameters=new Dictionary<string,object>(args){{"$snapshot",Text(marker,"SNAPSHOT_ID")}};
            var rows=ViewerDatabase.Query(db,"SELECT "+ViewerDatabase.EventColumns+" FROM events WHERE "+TimedScope+" AND SNAPSHOT_ID=$snapshot AND EVENT_TYPE IN ('TCP_SNAPSHOT','UDP_SNAPSHOT') LIMIT 100001",parameters);
            if(rows.Count!=expected)continue;
            foreach(var row in rows)state.Current[Key(row)]=row;
            if(state.Current.Count!=expected){state.Current.Clear();continue;}
            state.Baseline=marker;break;
        }
        if(state.Baseline is not null){
            var parameters=new Dictionary<string,object>(args){{"$snapshot",Text(state.Baseline,"SNAPSHOT_ID")},{"$since",Text(state.Baseline,"TIMESTAMP_UTC")},{"$day",Text(state.Baseline,"TIMESTAMP_UTC")[..10]}};
            var changes=ViewerDatabase.Query(db,"SELECT "+ViewerDatabase.EventColumns+" FROM events WHERE "+TimedScope+" AND date_utc>=$day AND SNAPSHOT_ID<>$snapshot AND julianday(TIMESTAMP_UTC)>=julianday($since) AND EVENT_TYPE IN ('TCP_CONNECTED','TCP_DISCONNECTED','UDP_ENDPOINT_OPENED','UDP_ENDPOINT_CLOSED') ORDER BY julianday(TIMESTAMP_UTC),CASE WHEN EVENT_TYPE IN ('TCP_DISCONNECTED','UDP_ENDPOINT_CLOSED') THEN 1 ELSE 0 END,EVENT_ID",parameters);
            foreach(var row in changes){
                if(Text(row,"EVENT_TYPE") is "TCP_DISCONNECTED" or "UDP_ENDPOINT_CLOSED")state.Current.Remove(Key(row));
                else state.Current[Key(row)]=row;
            }
        }
        state.Incomplete=markers.Count>0&&(state.Baseline is null||Text(markers[0],"SNAPSHOT_ID")!=Text(state.Baseline,"SNAPSHOT_ID"));
        if(state.Baseline is not null){
            var newest=ViewerDatabase.Query(db,"SELECT SNAPSHOT_ID,TIMESTAMP_UTC FROM events WHERE "+TimedScope+" AND EVENT_TYPE IN ('TCP_SNAPSHOT','UDP_SNAPSHOT') ORDER BY TIMESTAMP_UTC DESC LIMIT 1",args);
            if(newest.Count>0&&Text(newest[0],"SNAPSHOT_ID")!=Text(state.Baseline,"SNAPSHOT_ID")&&Stamp(Text(newest[0],"TIMESTAMP_UTC"))>Stamp(Text(state.Baseline,"TIMESTAMP_UTC")))state.Incomplete=true;
        }
        double age=(at-Stamp(Text(state.Latest,"TIMESTAMP_UTC"))).TotalSeconds;
        double? snapshotAge=state.Baseline is null?null:(at-Stamp(Text(state.Baseline,"TIMESTAMP_UTC"))).TotalSeconds;
        state.Status=age < -120?"clock_skew":state.Baseline is null?"snapshot_required":snapshotAge>120||age>120?"stale":state.Incomplete?"updating":"fresh";
        return state;
    }
}
