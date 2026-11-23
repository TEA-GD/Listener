using Microsoft.Data.Sqlite;
using System.Text.Json;
using WatcherShared;

public static class MailDatabaseSchema
{
 public static void Initialize(SqliteConnection db,SqliteTransaction? transaction=null){
  using var owned=transaction is null?db.BeginTransaction():null;var tx=transaction??owned!;
  using var c=db.CreateCommand();c.Transaction=tx;c.CommandText="""
   CREATE TABLE IF NOT EXISTS mail_information(
    alert_id TEXT PRIMARY KEY,host TEXT NOT NULL,date_received_utc TEXT,sender TEXT NOT NULL,recipients_json TEXT NOT NULL,
    attachment_count INTEGER NOT NULL CHECK(attachment_count>0),attachments_json TEXT NOT NULL,collected_utc TEXT NOT NULL,date_source TEXT NOT NULL);
   CREATE INDEX IF NOT EXISTS idx_mail_information_date ON mail_information(collected_utc,alert_id);
   """;c.ExecuteNonQuery();
  foreach(string table in new[]{"email_alerts","email_metadata_alerts"}){
   c.CommandText="SELECT count(*) FROM sqlite_master WHERE type='table' AND name=$table";c.Parameters.Clear();c.Parameters.AddWithValue("$table",table);if((long)c.ExecuteScalar()! ==0)continue;
   c.Parameters.Clear();c.CommandText=$"PRAGMA table_info({table})";var columns=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
   using(var schema=c.ExecuteReader()){while(schema.Read())columns.Add(schema.GetString(1));}
   string cc=columns.Contains("cc_json")?"cc_json":"'[]'",bcc=columns.Contains("bcc_json")?"bcc_json":"'[]'";
   c.CommandText=$"SELECT alert_id,client_label,sender,recipients_json,attachment_names_json,received_utc,{cc},{bcc} FROM {table}";
   var rows=new List<string[]>();using(var reader=c.ExecuteReader()){while(reader.Read())rows.Add(Enumerable.Range(0,8).Select(i=>reader.IsDBNull(i)?"":reader.GetString(i)).ToArray());}
   foreach(var row in rows){
    using var names=JsonDocument.Parse(row[4]);var files=names.RootElement.EnumerateArray().Select(n=>n.GetString()??"").Where(n=>n!="").Select(n=>new MailAttachment(n,Path.GetExtension(n).TrimStart('.').ToLowerInvariant(),null)).ToArray();if(files.Length==0)continue;
    var recipients=new List<string>();foreach(int index in new[]{3,6,7}){using var addresses=JsonDocument.Parse(row[index]);recipients.AddRange(addresses.RootElement.EnumerateArray().Select(a=>a.GetString()??""));}
    // Historical variants never recorded the mail arrival time or attachment sizes.
    var hash=System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(table+":"+row[0]));string id=new Guid(hash.AsSpan(0,16)).ToString("D");
    c.CommandText="INSERT OR IGNORE INTO mail_information VALUES($id,$host,NULL,$sender,$to,$count,$files,$collected,'legacy_unknown')";c.Parameters.Clear();
    foreach(var pair in new Dictionary<string,object>{{"$id",id},{"$host",row[1]},{"$sender",row[2]},{"$to",JsonSerializer.Serialize(recipients.Distinct(StringComparer.OrdinalIgnoreCase))},{"$count",files.Length},{"$files",JsonSerializer.Serialize(files)},{"$collected",row[5]}})c.Parameters.AddWithValue(pair.Key,pair.Value);
    c.ExecuteNonQuery();
   }
   c.Parameters.Clear();c.CommandText=$"DROP TABLE {table}";c.ExecuteNonQuery();
  }
  owned?.Commit();
 }
 public static void Store(SqliteConnection db,SqliteTransaction tx,AttachmentMail mail,string host){
  using var c=db.CreateCommand();c.Transaction=tx;c.CommandText="INSERT OR IGNORE INTO mail_information VALUES($id,$host,$date,$sender,$to,$count,$files,$now,'thunderbird_notification')";
  foreach(var p in new Dictionary<string,object>{{"$id",mail.alert_id},{"$host",host},{"$date",mail.date_received_utc},{"$sender",mail.sender},{"$to",JsonSerializer.Serialize(mail.recipients)},{"$count",mail.attachments.Length},{"$files",JsonSerializer.Serialize(mail.attachments)},{"$now",DateTimeOffset.UtcNow.ToString("o")}})c.Parameters.AddWithValue(p.Key,p.Value);
  c.ExecuteNonQuery();
 }
}
