using System.Text.Json;
namespace WatcherShared;
public sealed record MailAttachment(string name,string format,long? size_bytes);
public sealed record AttachmentMail(string alert_id,string date_received_utc,string sender,string[] recipients,MailAttachment[] attachments);
public static class MailPayload
{
 public static AttachmentMail Parse(string json){
  using var doc=JsonDocument.Parse(json);var r=doc.RootElement;
  if(r.ValueKind!=JsonValueKind.Object||r.EnumerateObject().Any(p=>!new[]{"alert_id","date_received_utc","sender","recipients","attachments"}.Contains(p.Name)))throw new ArgumentException("Only attachment mail metadata is accepted");
  string Text(JsonElement source,string name,int max){var p=source.GetProperty(name);if(p.ValueKind!=JsonValueKind.String||p.GetString()!.Length>max)throw new ArgumentException("Invalid mail field "+name);return p.GetString()!;}
  string id=Text(r,"alert_id",36),date=Text(r,"date_received_utc",50);
  if(!Guid.TryParseExact(id,"D",out _)||!DateTimeOffset.TryParse(date,out var stamp)||stamp.Offset!=TimeSpan.Zero||(!date.EndsWith('Z')&&!date.EndsWith("+00:00")))throw new ArgumentException("Invalid mail ID/UTC receipt date");
  var recipients=r.GetProperty("recipients");var attachments=r.GetProperty("attachments");
  if(recipients.ValueKind!=JsonValueKind.Array||recipients.GetArrayLength()>100||attachments.ValueKind!=JsonValueKind.Array||attachments.GetArrayLength() is <1 or >100)throw new ArgumentException("Mail must have 1–100 attachments");
  var to=recipients.EnumerateArray().Select(v=>v.ValueKind==JsonValueKind.String&&v.GetString()!.Length<=512?v.GetString()!:throw new ArgumentException("Invalid recipient")).ToArray();
  var files=attachments.EnumerateArray().Select(a=>{
   if(a.ValueKind!=JsonValueKind.Object||a.EnumerateObject().Any(p=>!new[]{"name","format","size_bytes"}.Contains(p.Name)))throw new ArgumentException("Invalid attachment metadata");
   long? size=null;if(a.TryGetProperty("size_bytes",out var v)&&v.ValueKind!=JsonValueKind.Null){if(!v.TryGetInt64(out long n)||n<0)throw new ArgumentException("Invalid attachment size");size=n;}
   return new MailAttachment(Text(a,"name",256),Text(a,"format",256),size);
  }).ToArray();
  return new(id,date,Text(r,"sender",512),to,files);
 }
}
