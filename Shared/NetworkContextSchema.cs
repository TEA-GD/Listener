using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
namespace WatcherCollector;
internal static class NetworkContextSchema
{
    internal static void Initialize(SqliteConnection db,SqliteTransaction tx){
        using var c=db.CreateCommand();c.Transaction=tx;c.CommandText="""
        CREATE TABLE IF NOT EXISTS device_network_communications(event_id TEXT PRIMARY KEY,host TEXT NOT NULL,snapshot_id TEXT NOT NULL,timestamp_utc TEXT NOT NULL,received_utc TEXT NOT NULL,adapter_id TEXT,adapter_name TEXT,network_type TEXT,network_name TEXT,connection_type TEXT,state TEXT,addresses TEXT,gateway TEXT);
        CREATE INDEX IF NOT EXISTS idx_network_context ON device_network_communications(host,snapshot_id);
        CREATE TABLE IF NOT EXISTS device_process_snapshots(event_id TEXT PRIMARY KEY,host TEXT NOT NULL,snapshot_id TEXT NOT NULL,timestamp_utc TEXT NOT NULL,received_utc TEXT NOT NULL,entry_type TEXT,pid INTEGER,name TEXT,path TEXT,started_utc TEXT,vpn_candidate INTEGER NOT NULL);
        CREATE INDEX IF NOT EXISTS idx_process_context ON device_process_snapshots(host,snapshot_id,vpn_candidate);
        CREATE TABLE IF NOT EXISTS device_context_snapshots(event_id TEXT PRIMARY KEY,host TEXT NOT NULL,snapshot_id TEXT NOT NULL,timestamp_utc TEXT NOT NULL,received_utc TEXT NOT NULL,network_count INTEGER NOT NULL,process_count INTEGER NOT NULL,status TEXT,note TEXT,UNIQUE(host,snapshot_id));
        CREATE INDEX IF NOT EXISTS idx_context_day ON device_context_snapshots(host,timestamp_utc);
        """;c.ExecuteNonQuery();
    }
    internal static void Store(SqliteConnection db,SqliteTransaction tx,JsonElement row,string host,string stamp,string type){
        string snapshot=row.GetProperty("SNAPSHOT_ID").GetString()??"";
        string raw=row.GetProperty("CONTEXT_JSON").GetString()??"";
        if(snapshot.Length is <1 or >100||raw.Length>16000)throw new ArgumentException("Invalid context snapshot");
        using var document=JsonDocument.Parse(raw);var data=document.RootElement;
        if(data.ValueKind!=JsonValueKind.Object)throw new ArgumentException("Context must be an object");
        string Text(string name){string value=data.TryGetProperty(name,out var v)?v.ToString():"";if(value.Length>2048)throw new ArgumentException("Context field too long");return value;}
        int Number(string name,int max){if(!int.TryParse(Text(name),out int value)||value<0||value>max)throw new ArgumentException("Invalid context count/PID");return value;}
        using var c=db.CreateCommand();c.Transaction=tx;
        c.Parameters.AddWithValue("$id",row.GetProperty("EVENT_ID").GetString()!);c.Parameters.AddWithValue("$host",host);c.Parameters.AddWithValue("$snapshot",snapshot);c.Parameters.AddWithValue("$stamp",stamp);c.Parameters.AddWithValue("$received",DateTimeOffset.UtcNow.ToString("o"));
        string fields,values,table;
        if(type=="CONTEXT_COMPLETE"){
            table="device_context_snapshots";fields="network_count,process_count,status,note";values="$networks,$processes,$status,$note";
            c.Parameters.AddWithValue("$networks",Number("networks",128));c.Parameters.AddWithValue("$processes",Number("processes",4096));c.Parameters.AddWithValue("$status",Text("status"));c.Parameters.AddWithValue("$note",Text("note"));
        }else if(type=="NETWORK_CONTEXT"){
            table="device_network_communications";fields="adapter_id,adapter_name,network_type,network_name,connection_type,state,addresses,gateway";
            values=string.Join(',',fields.Split(',').Select(f=>"$"+f));foreach(string f in fields.Split(','))c.Parameters.AddWithValue("$"+f,Text(f));
        }else{
            table="device_process_snapshots";fields="entry_type,pid,name,path,started_utc,vpn_candidate";values="$entry_type,$pid,$name,$path,$started_utc,$vpn";
            foreach(string f in new[]{"entry_type","name","path","started_utc"})c.Parameters.AddWithValue("$"+f,Text(f));c.Parameters.AddWithValue("$pid",Number("pid",int.MaxValue));
            c.Parameters.AddWithValue("$vpn",Regex.IsMatch(Text("name")+" "+Text("path"),"vpn|amnezia|wireguard|wintun|tailscale|zerotier|anyconnect|forticlient|strongswan",RegexOptions.IgnoreCase)?1:0);
        }
        c.CommandText=$"INSERT INTO {table}(event_id,host,snapshot_id,timestamp_utc,received_utc,{fields}) VALUES($id,$host,$snapshot,$stamp,$received,{values})";c.ExecuteNonQuery();
    }
}
