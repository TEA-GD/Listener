using System.Net;
using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
using WatcherShared;
namespace WatcherAgent;
internal sealed class MailBridge : IDisposable
{
 private readonly HttpListener listener=new();private readonly AgentConfig config;private readonly Action<List<Dictionary<string,string>>> enqueue;
 public MailBridge(AgentConfig config,Action<List<Dictionary<string,string>>> enqueue){
  this.config=config;this.enqueue=enqueue;
  if(config.mail_token=="")return;
  if(config.mail_token.Length<32||config.mail_port is <1024 or >65535)throw new ArgumentException("Set mail_token to at least 32 characters and mail_port to 1024–65535");
  listener.Prefixes.Add($"http://127.0.0.1:{config.mail_port}/");
  try{listener.Start();_ = Task.Run(Serve);}catch(HttpListenerException){Console.Error.WriteLine("Local mail endpoint could not start; check port/URL permissions. Connection monitoring continues.");listener.Close();}
 }
 private async Task Serve(){while(listener.IsListening){HttpListenerContext context;try{context=await listener.GetContextAsync();}catch(HttpListenerException){break;}catch(ObjectDisposedException){break;}
  try{await Handle(context);}catch(Exception ex) when(ex is ArgumentException or JsonException or InvalidOperationException or KeyNotFoundException or FormatException){context.Response.StatusCode=400;}catch{context.Response.StatusCode=503;}
  finally{context.Response.Close();}
 }}
 private async Task Handle(HttpListenerContext c){
  c.Response.Headers["Access-Control-Allow-Origin"]="*";c.Response.Headers["Access-Control-Allow-Headers"]="Authorization, Content-Type";c.Response.Headers["Access-Control-Allow-Methods"]="POST, OPTIONS";
  if(c.Request.HttpMethod=="OPTIONS"){c.Response.StatusCode=204;return;}
  if(c.Request.HttpMethod!="POST"||c.Request.Url!.AbsolutePath!="/api/mail-attachments"){c.Response.StatusCode=404;return;}
  var expected=SHA256.HashData(Encoding.UTF8.GetBytes("Bearer "+config.mail_token));var actual=SHA256.HashData(Encoding.UTF8.GetBytes(c.Request.Headers["Authorization"]??""));
  if(!CryptographicOperations.FixedTimeEquals(expected,actual)){c.Response.StatusCode=401;return;}
  if(c.Request.ContentLength64 is <1 or >16384){c.Response.StatusCode=413;return;}
  using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(5));using var buffer=new MemoryStream();byte[] bytes=new byte[4096];int n;
  while((n=await c.Request.InputStream.ReadAsync(bytes,timeout.Token))>0){buffer.Write(bytes,0,n);if(buffer.Length>16384){c.Response.StatusCode=413;return;}}
  var mail=MailPayload.Parse(Encoding.UTF8.GetString(buffer.ToArray()));
  var row=new Dictionary<string,string>{{"EVENT_ID",mail.alert_id},{"EVENT_TYPE","MAIL_ATTACHMENT"},{"MONITORED_HOST",config.agent_id},{"DEVICE_CATEGORY",config.device_category},{"TIMESTAMP_UTC",DateTimeOffset.UtcNow.ToString("o")},{"MAIL_JSON",JsonSerializer.Serialize(mail)}};
  if(JsonSerializer.SerializeToUtf8Bytes(row).Length>30000){c.Response.StatusCode=413;return;}
  enqueue([row]); // HTTP acceptance only follows the existing flushed/renamed local spool write.
  c.Response.ContentType="application/json";byte[] result=JsonSerializer.SerializeToUtf8Bytes(new{status="queued",alert_id=mail.alert_id});await c.Response.OutputStream.WriteAsync(result);
 }
 public void Dispose()=>listener.Close();
}
