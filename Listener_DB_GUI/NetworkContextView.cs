using System.Globalization;
internal static class NetworkContextView
{
    internal static IResult Read(HttpRequest request,ViewerDatabase database){
        string host=request.Query["host"].ToString();if(string.IsNullOrWhiteSpace(host)||host.Length>120)throw new ArgumentException("Choose a device");
        int Page(string key){string text=request.Query[key].ToString();if(text=="")return 1;if(!int.TryParse(text,out int p)||p<1||p>100000)throw new ArgumentException("Invalid page");return p;}
        using var db=database.Open();
        if(ViewerDatabase.Query(db,"SELECT 1 FROM sqlite_master WHERE name='device_context_snapshots'").Count==0)return Results.Ok(new { available=false,snapshots=Array.Empty<object>(),total=0 });
        string snapshot=request.Query["snapshot"].ToString();
        var args=new Dictionary<string,object>{{"$host",host}};
        const string complete="""
          (SELECT count(*) FROM device_network_communications n WHERE n.host=s.host AND n.snapshot_id=s.snapshot_id)=s.network_count
          AND (SELECT count(*) FROM device_process_snapshots p WHERE p.host=s.host AND p.snapshot_id=s.snapshot_id)=s.process_count
          """;
        if(snapshot!=""){
            if(snapshot.Length>100)throw new ArgumentException("Invalid snapshot");args["$snapshot"]=snapshot;
            var markers=ViewerDatabase.Query(db,"SELECT * FROM device_context_snapshots s WHERE host=$host AND snapshot_id=$snapshot AND "+complete,args);
            if(markers.Count==0)return Results.NotFound(new {error="Snapshot not found or still arriving"});
            int page=Page("processPage");args["$limit"]=200;args["$offset"]=(page-1)*200;
            var networks=ViewerDatabase.Query(db,"SELECT * FROM device_network_communications WHERE host=$host AND snapshot_id=$snapshot ORDER BY adapter_name",args);
            var processes=ViewerDatabase.Query(db,"SELECT * FROM device_process_snapshots WHERE host=$host AND snapshot_id=$snapshot ORDER BY vpn_candidate DESC,entry_type,name,pid LIMIT $limit OFFSET $offset",args);
            return Results.Ok(new {available=true,snapshot=markers[0],networks,processes,processPage=page,size=200});
        }
        string day=request.Query["day"].ToString();if(!DateOnly.TryParseExact(day,"yyyy-MM-dd",CultureInfo.InvariantCulture,DateTimeStyles.None,out var date))throw new ArgumentException("Choose a UTC day");
        int listPage=Page("page");args["$from"]=date.ToString("yyyy-MM-dd");args["$to"]=date.AddDays(1).ToString("yyyy-MM-dd");args["$limit"]=50;args["$offset"]=(listPage-1)*50;
        string where="host=$host AND timestamp_utc>=$from AND timestamp_utc<$to AND "+complete;
        var total=ViewerDatabase.Query(db,"SELECT count(*) AS total FROM device_context_snapshots s WHERE "+where,args)[0]["total"];
        var rows=ViewerDatabase.Query(db,"SELECT s.*,(SELECT count(*) FROM device_process_snapshots p WHERE p.host=s.host AND p.snapshot_id=s.snapshot_id AND vpn_candidate=1) AS vpn_candidates FROM device_context_snapshots s WHERE "+where+" ORDER BY timestamp_utc DESC,snapshot_id LIMIT $limit OFFSET $offset",args);
        return Results.Ok(new {available=true,snapshots=rows,total,page=listPage,size=50});
    }
}
