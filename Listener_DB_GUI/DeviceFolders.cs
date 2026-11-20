using Microsoft.Data.Sqlite;

internal static class DeviceFolders
{
    private static readonly string[] Categories=["windows_servers","linux_servers","user_pcs","project_stands"];

    // Probe the newest report through each partition's host/time index. Do not regroup raw history.
    internal static void Attach(SqliteConnection db,List<Dictionary<string,object?>> devices)
    {
        var tables=ViewerDatabase.Query(db,"SELECT name FROM sqlite_master WHERE type='table'")
            .Select(row=>row["name"]?.ToString()).ToHashSet(StringComparer.Ordinal);
        var commands=new Dictionary<string,SqliteCommand>();
        try{
            if(tables.Contains("event_registry"))foreach(string category in Categories){
                string table="events_"+category;
                if(!tables.Contains(table))continue;
                var command=db.CreateCommand();
                command.CommandText=$"SELECT julianday(e.TIMESTAMP_UTC),i.sequence FROM {table} e JOIN event_registry i ON i.event_id=e.EVENT_ID WHERE e.host=$host ORDER BY julianday(e.TIMESTAMP_UTC) DESC,i.sequence DESC LIMIT 1";
                command.Parameters.AddWithValue("$host","");command.Prepare();commands.Add(category,command);
            }
            foreach(var device in devices){
                string category="legacy";double newest=double.NegativeInfinity;long sequence=-1;
                foreach(var (candidate,command) in commands){
                    command.Parameters["$host"].Value=device["host"]?.ToString()??"";
                    using var reader=command.ExecuteReader();
                    if(!reader.Read()||reader.IsDBNull(0))continue;
                    double stamp=reader.GetDouble(0);long id=reader.GetInt64(1);
                    if(stamp>newest||(stamp==newest&&id>sequence)){category=candidate;newest=stamp;sequence=id;}
                }
                device["category"]=category;
            }
        }
        finally{foreach(var command in commands.Values)command.Dispose();}
    }
}
