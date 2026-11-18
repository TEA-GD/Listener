using System.Globalization;
using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Data.Sqlite;

var builder=WebApplication.CreateBuilder(args);
if(string.IsNullOrEmpty(builder.Configuration["urls"]))builder.WebHost.UseUrls("http://127.0.0.1:6516");
string token=Environment.GetEnvironmentVariable("LISTENER_MAIL_TOKEN")??(Environment.GetEnvironmentVariable("LISTENER_MAIL_TOKEN_FILE") is string tokenPath?File.ReadAllText(tokenPath).Trim():"");
if(token.Length<32)throw new InvalidOperationException("Set LISTENER_MAIL_TOKEN to a random secret of at least 32 characters. Do not put it in source control.");
string database=builder.Configuration["DatabasePath"]??Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),"WatcherCollector","watcher-events.db");
var domains=(builder.Configuration["InternalDomains"]??"").Split(',',StringSplitOptions.TrimEntries|StringSplitOptions.RemoveEmptyEntries).Select(MailStore.NormalizeDomain).Distinct().ToArray();
if(domains.Length==0)throw new InvalidOperationException("Set InternalDomains to your comma-separated internal email domains.");
bool subdomains=bool.TryParse(builder.Configuration["IncludeSubdomains"],out bool include)?include:true;
int maximum=int.TryParse(builder.Configuration["MaximumRequestBytes"],out int configured)?configured:16*1024*1024;
if(maximum<1024||maximum>64*1024*1024)throw new InvalidOperationException("MaximumRequestBytes must be between 1024 and 67108864.");
builder.Services.Configure<KestrelServerOptions>(o=>o.Limits.MaxRequestBodySize=maximum);
builder.Services.AddCors(options=>options.AddDefaultPolicy(p=>p.SetIsOriginAllowed(origin=>Uri.TryCreate(origin,UriKind.Absolute,out var u)&&u.Scheme=="moz-extension").WithHeaders("Authorization","Content-Type").WithMethods("POST")));
var store=new MailStore(database);store.Initialize();
var app=builder.Build();
app.Use(async(context,next)=>{
    context.Response.Headers.CacheControl="no-store";
    context.Response.Headers["X-Content-Type-Options"]="nosniff";
    if(context.Request.Path.StartsWithSegments("/api")&&!HttpMethods.IsOptions(context.Request.Method)){
        string supplied=context.Request.Headers.Authorization.ToString();
        byte[] expectedHash=SHA256.HashData(Encoding.UTF8.GetBytes("Bearer "+token));
        if(!CryptographicOperations.FixedTimeEquals(expectedHash,SHA256.HashData(Encoding.UTF8.GetBytes(supplied)))){context.Response.StatusCode=401;await context.Response.WriteAsJsonAsync(new{error="Receiver key required"});return;}
    }
    try{await next();}
    catch(Microsoft.AspNetCore.Http.BadHttpRequestException ex){context.Response.StatusCode=ex.StatusCode;await context.Response.WriteAsJsonAsync(new{error="Invalid or oversized request. Mail must remain queued at the sender."});}
    catch(ArgumentException ex){context.Response.StatusCode=400;await context.Response.WriteAsJsonAsync(new{error=ex.Message});}
    catch(JsonException){context.Response.StatusCode=400;await context.Response.WriteAsJsonAsync(new{error="Invalid JSON"});}
    catch(IOException){context.Response.StatusCode=503;await context.Response.WriteAsJsonAsync(new{error="Mail storage unavailable; retry later"});}
    catch(UnauthorizedAccessException){context.Response.StatusCode=503;await context.Response.WriteAsJsonAsync(new{error="Mail storage unavailable; retry later"});}
    catch(SqliteException ex){app.Logger.LogError("Mail database write failed, SQLite code {Code}",ex.SqliteErrorCode);context.Response.StatusCode=503;await context.Response.WriteAsJsonAsync(new{error="Mail storage unavailable; retry later"});}
});
app.UseCors();
app.MapGet("/health",()=>Results.Ok(new{service="Listener Mail Receiver",status="running"}));
app.MapPost("/api/mail-alerts",async(HttpRequest request)=>{
    var mail=await JsonSerializer.DeserializeAsync<MailAlert>(request.Body)??throw new ArgumentException("Mail payload required");
    mail.Validate();
    // Classify the actual reported From header, not the display name or a client-supplied domain.
    string senderAddress="",senderDomain="";
    try{var from=new MailAddress(mail.sender);senderAddress=from.Address;senderDomain=MailStore.NormalizeDomain(from.Host);}catch(FormatException){}catch(ArgumentException){}
    if(senderDomain.Length>0&&domains.Any(d=>senderDomain==d||(subdomains&&senderDomain.EndsWith("."+d,StringComparison.Ordinal))))
        return Results.Ok(new{status="ignored_internal",alert_id=mail.alert_id,message_key=mail.message_key});
    var saved=store.Save(mail,senderAddress,senderDomain);
    return Results.Ok(new{status=saved.Duplicate?"duplicate":"stored",alert_id=saved.AlertId,message_key=mail.message_key});
});
app.Run();

sealed class MailAlert
{
    public string alert_id{get;set;}="";
    public string client_id{get;set;}="";
    public string client_label{get;set;}="";
    public string account_id{get;set;}="";
    public string message_key{get;set;}="";
    public string message_id{get;set;}="";
    public string sender{get;set;}="";
    public string[] recipients{get;set;}=[];
    public string[] cc{get;set;}=[];
    public string[] bcc{get;set;}=[];
    public string[] mailbox_addresses{get;set;}=[];
    public string[] delivered_to{get;set;}=[];
    public string subject{get;set;}="";
    public string body_text{get;set;}="";
    public TextPart[] text_parts{get;set;}=[];
    public string[] attachment_names{get;set;}=[];
    public string body_status{get;set;}="available";
    public string? message_date_utc{get;set;}
    public void Validate(){
        if(!Guid.TryParseExact(alert_id,"D",out _)||!Guid.TryParseExact(client_id,"D",out _))throw new ArgumentException("Invalid alert/client ID");
        if(string.IsNullOrWhiteSpace(account_id)||account_id.Length>500||string.IsNullOrWhiteSpace(message_key)||message_key.Length>2000||string.IsNullOrWhiteSpace(client_label)||client_label.Length>120)throw new ArgumentException("Account, message key and client label are required");
        if(sender is null||sender.Length>16384||subject is null||subject.Length>65536||body_text is null||message_id is null||message_id.Length>2000)throw new ArgumentException("Invalid message fields");
        if(new[]{recipients,cc,bcc,mailbox_addresses,delivered_to,attachment_names}.Any(a=>a is null||a.Length>10000||a.Any(v=>v is null||v.Length>16384)))throw new ArgumentException("Invalid recipients or attachment names");
        if(text_parts is null||text_parts.Length>10000||text_parts.Any(p=>p is null||p.content_type is null||p.text is null||p.part_name is null))throw new ArgumentException("Invalid MIME text parts");
        if(body_status is not("available" or "empty" or "headers_only" or "decryption_failed" or "decryption_skipped"))throw new ArgumentException("Invalid body status");
        if(message_date_utc is not null&&!DateTimeOffset.TryParse(message_date_utc,CultureInfo.InvariantCulture,DateTimeStyles.None,out _))throw new ArgumentException("Invalid message date");
    }
}
sealed class TextPart{public string content_type{get;set;}="";public string text{get;set;}="";public string part_name{get;set;}="";}
sealed class MailStore(string path)
{
    public static string NormalizeDomain(string input){
        string domain=input.Trim().TrimEnd('.').ToLowerInvariant();
        if(domain.Contains('@')||domain.Contains('/')||domain.Contains(':')||Uri.CheckHostName(domain)!=UriHostNameType.Dns)throw new ArgumentException("InternalDomains must contain DNS domain names, without @ or URLs");
        return new IdnMapping().GetAscii(domain).ToLowerInvariant();
    }
    private SqliteConnection Open(){
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var db=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=path,Mode=SqliteOpenMode.ReadWriteCreate,DefaultTimeout=15}.ToString());
        try{db.Open();using var pragma=db.CreateCommand();pragma.CommandText="PRAGMA busy_timeout=15000; PRAGMA synchronous=FULL;";pragma.ExecuteNonQuery();return db;}
        catch{db.Dispose();throw;}
    }
    public void Initialize(){
        using var db=Open();
        using(var wal=db.CreateCommand()){wal.CommandText="PRAGMA journal_mode=WAL;";wal.ExecuteNonQuery();}
        using var transaction=db.BeginTransaction();
        MailDatabaseSchema.Initialize(db,transaction);
        transaction.Commit();
    }
    public (string AlertId,bool Duplicate) Save(MailAlert mail,string senderAddress,string senderDomain){
        using var db=Open();using var tx=db.BeginTransaction();using var c=db.CreateCommand();c.Transaction=tx;
        c.CommandText="SELECT alert_id FROM email_alerts WHERE client_id=$client AND account_id=$account AND message_key=$key";
        c.Parameters.AddWithValue("$client",mail.client_id);c.Parameters.AddWithValue("$account",mail.account_id);c.Parameters.AddWithValue("$key",mail.message_key);
        if(c.ExecuteScalar() is string existing){tx.Commit();return(existing,true);}
        c.Parameters.Clear();
        c.CommandText="""
            INSERT INTO email_alerts(alert_id,client_id,client_label,account_id,message_key,message_id,sender,sender_address,sender_domain,recipients_json,cc_json,bcc_json,mailbox_addresses_json,delivered_to_json,subject,body_text,text_parts_json,attachment_names_json,body_status,message_date_utc,received_utc) VALUES($id,$client,$label,$account,$key,$message,$sender,$address,$domain,$to,$cc,$bcc,$mailboxes,$delivered,$subject,$body,$parts,$attachments,$status,$date,$now)
            """;
        void Add(string name,object? value)=>c.Parameters.AddWithValue(name,value??DBNull.Value);
        Add("$id",mail.alert_id);Add("$client",mail.client_id);Add("$label",mail.client_label);Add("$account",mail.account_id);Add("$key",mail.message_key);Add("$message",mail.message_id);
        Add("$sender",mail.sender);Add("$address",senderAddress);Add("$domain",senderDomain);Add("$to",JsonSerializer.Serialize(mail.recipients));Add("$cc",JsonSerializer.Serialize(mail.cc));Add("$bcc",JsonSerializer.Serialize(mail.bcc));
        Add("$mailboxes",JsonSerializer.Serialize(mail.mailbox_addresses));Add("$delivered",JsonSerializer.Serialize(mail.delivered_to));Add("$subject",mail.subject);Add("$body",mail.body_text);Add("$parts",JsonSerializer.Serialize(mail.text_parts));Add("$attachments",JsonSerializer.Serialize(mail.attachment_names));Add("$status",mail.body_status);Add("$date",mail.message_date_utc);Add("$now",DateTimeOffset.UtcNow.ToString("o",CultureInfo.InvariantCulture));
        c.ExecuteNonQuery();tx.Commit();return(mail.alert_id,false);
    }
}
