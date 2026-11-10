using System.Globalization;
using Microsoft.Data.Sqlite;

var builder = WebApplication.CreateBuilder(new WebApplicationOptions {
    Args = args, WebRootPath = Path.Combine(AppContext.BaseDirectory, "wwwroot")
});
if (string.IsNullOrEmpty(builder.Configuration["urls"])) builder.WebHost.UseUrls("http://127.0.0.1:5080");
builder.Services.AddSingleton(new ViewerDatabase(
    Environment.GetEnvironmentVariable("WATCHER_DB_PATH") ?? builder.Configuration["DatabasePath"] ??
    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "WatcherCollector", "watcher-events.db")));
var app = builder.Build();
app.Use(async (context, next) => {
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["Content-Security-Policy"] = "default-src 'self'; script-src 'self'; style-src 'self'; object-src 'none'; frame-ancestors 'none'";
    if (context.Request.Path.StartsWithSegments("/api")) context.Response.Headers.CacheControl = "no-store";
    try { await next(); }
    catch (ArgumentException ex) { context.Response.StatusCode = 400; await context.Response.WriteAsJsonAsync(new { error = ex.Message }); }
    catch (ViewerException ex) { context.Response.StatusCode = 503; await context.Response.WriteAsJsonAsync(new { error = ex.Message }); }
    catch (SqliteException ex) {
        app.Logger.LogError(ex, "Dashboard SQLite read failed");
        context.Response.StatusCode = 503;
        await context.Response.WriteAsJsonAsync(new { error = "Cannot read collector database. Check file permissions, schema and collector status. See dashboard console for details." });
    }
});
app.UseDefaultFiles();
app.UseStaticFiles();
app.MapGet("/api/pcs", (ViewerDatabase database) => {
    using var db = database.Open();
    return Results.Ok(ViewerDatabase.Query(db, """
        SELECT MONITORED_HOST AS host, COUNT(*) AS events,
          MAX(TIMESTAMP_UTC) AS lastEvent, MAX(received_utc) AS lastReceived,
          COUNT(DISTINCT NULLIF(PROCESS_NAME,'')) AS processes,
          COUNT(DISTINCT NULLIF(RECIEVER_IP,'')) AS destinations
        FROM events GROUP BY MONITORED_HOST ORDER BY lastReceived DESC,host
        """));
});
app.MapGet("/api/pc", (HttpRequest request, ViewerDatabase database) => {
    string host = request.Query["host"].ToString();
    if (string.IsNullOrWhiteSpace(host) || host.Length > 120) throw new ArgumentException("Choose a valid PC.");
    string q = request.Query["q"].ToString();
    if (q.Length > 200) throw new ArgumentException("Search must be at most 200 characters.");
    string type = request.Query["type"].ToString();
    if (type.Length > 40) throw new ArgumentException("Invalid event type.");
    int page = ParseNumber(request.Query["page"].ToString(), 1, 1, 1000000);
    int size = ParseNumber(request.Query["size"].ToString(), 50, 1, 200);
    var parameters = new Dictionary<string, object> { ["$host"] = host };
    string where = " WHERE host=$host";
    foreach (string key in new[] { "from", "to" }) {
        string date = request.Query[key].ToString();
        if (date.Length == 0) continue;
        if (!DateOnly.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
            throw new ArgumentException("Dates must use yyyy-MM-dd.");
        parameters["$" + key] = date;
        where += key == "from" ? " AND date_utc >= $from" : " AND date_utc <= $to";
    }
    if (parameters.TryGetValue("$from", out var from) && parameters.TryGetValue("$to", out var to) &&
        string.CompareOrdinal((string)from, (string)to) > 0) throw new ArgumentException("From date must not be after To date.");
    if (q.Length > 0) {
        parameters["$q"] = q;
        where += " AND (instr(lower(PROCESS_NAME),lower($q))>0 OR instr(SOURCE_IP,$q)>0 OR instr(RECIEVER_IP,$q)>0 OR instr(PORT_USED,$q)>0 OR instr(EVENT_ID,$q)>0)";
    }
    if (type.Length > 0) { parameters["$type"] = type; where += " AND EVENT_TYPE=$type"; }
    string destination = request.Query["destination"].ToString();
    if (destination.Length > 0) {
        if (destination.Length > 64 || !System.Net.IPAddress.TryParse(destination, out _))
            throw new ArgumentException("Destination filter must be an IP address.");
        parameters["$destination"] = destination;
        where += " AND RECIEVER_IP=$destination";
    }
    using var db = database.Open();
    var stats = ViewerDatabase.Query(db, """
        SELECT COUNT(*) AS events, COUNT(DISTINCT NULLIF(PROCESS_NAME,'')) AS processes,
          COUNT(DISTINCT NULLIF(RECIEVER_IP,'')) AS destinations,
          SUM(CASE WHEN EVENT_TYPE='TCP_CONNECTED' THEN 1 ELSE 0 END) AS connections,
          MIN(TIMESTAMP_UTC) AS firstEvent, MAX(TIMESTAMP_UTC) AS lastEvent
        FROM events
        """ + where, parameters)[0];
    var groups = new Dictionary<string, object>();
    foreach (var (name, column) in new[] { ("types", "EVENT_TYPE"), ("processes", "PROCESS_NAME"), ("destinations", "RECIEVER_IP"), ("ports", "PORT_USED") })
        groups[name] = ViewerDatabase.Query(db, $"SELECT [{column}] AS name, COUNT(*) AS count FROM events" + where +
            $" AND [{column}]<>'' GROUP BY [{column}] ORDER BY count DESC,name LIMIT 10", parameters);
    var paging = new Dictionary<string, object>(parameters) { ["$limit"] = size, ["$offset"] = (page - 1) * size };
    var events = ViewerDatabase.Query(db, "SELECT " + ViewerDatabase.EventColumns + " FROM events" + where +
        " ORDER BY TIMESTAMP_UTC DESC,EVENT_ID LIMIT $limit OFFSET $offset", paging);
    return Results.Ok(new { host, page, size, stats, groups, events });
});
app.MapGet("/api/connections", (HttpRequest request, ViewerDatabase database) => {
    string host = request.Query["host"].ToString();
    if (string.IsNullOrWhiteSpace(host) || host.Length > 120) throw new ArgumentException("Choose a valid PC.");
    string day = request.Query["day"].ToString();
    if (day.Length == 0) day = DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    if (!DateOnly.TryParseExact(day, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
        throw new ArgumentException("Day must use yyyy-MM-dd (UTC).");
    int gapDays = ParseNumber(request.Query["gapDays"].ToString(), 30, 1, 36500);
    int page = ParseNumber(request.Query["page"].ToString(), 1, 1, 1000000);
    const int size = 50;
    var parameters = new Dictionary<string, object> { ["$host"] = host, ["$day"] = day, ["$gap"] = gapDays };
    using var db = database.Open();
    // Compare against event time, never receipt order. Disconnects also prove prior
    // observation, but only a connected/startup event can create a candidate today.
    const string candidates = """
        WITH today AS (
            SELECT RECIEVER_IP AS ip, MIN(julianday(TIMESTAMP_UTC)) AS firstSeen,
                MAX(julianday(TIMESTAMP_UTC)) AS lastSeen, COUNT(*) AS observations,
                group_concat(DISTINCT NULLIF(PORT_USED,'')) AS ports,
                group_concat(DISTINCT NULLIF(PROCESS_NAME,'')) AS processes
            FROM events
            WHERE host=$host AND date_utc=$day AND RECIEVER_IP<>''
              AND EVENT_TYPE IN ('TCP_CONNECTED','ACTIVE_AT_START')
            GROUP BY RECIEVER_IP
        ), prior AS (
            SELECT history.RECIEVER_IP AS ip, MAX(julianday(history.TIMESTAMP_UTC)) AS previousSeen
            FROM events AS history JOIN today ON history.RECIEVER_IP=today.ip
            WHERE history.host=$host AND history.date_utc<=$day
              AND history.EVENT_TYPE IN ('TCP_CONNECTED','ACTIVE_AT_START','TCP_DISCONNECTED')
              AND julianday(history.TIMESTAMP_UTC)<today.firstSeen
            GROUP BY history.RECIEVER_IP
        ), compared AS (
            SELECT today.*, prior.previousSeen FROM today LEFT JOIN prior ON today.ip=prior.ip
        ), flagged AS (
            SELECT *, CASE WHEN previousSeen IS NULL THEN 'new' ELSE 'returned' END AS classification
            FROM compared WHERE previousSeen IS NULL OR firstSeen>=previousSeen+$gap
        )
        """;
    var summary = ViewerDatabase.Query(db, candidates + """
        SELECT COUNT(*) AS total,
            COUNT(CASE WHEN classification='new' THEN 1 END) AS newDestinations,
            COUNT(CASE WHEN classification='returned' THEN 1 END) AS returnedDestinations
        FROM flagged
        """, parameters)[0];
    parameters["$limit"] = size;
    parameters["$offset"] = (page - 1) * size;
    var destinations = ViewerDatabase.Query(db, candidates + """
        SELECT ip,classification,strftime('%Y-%m-%dT%H:%M:%fZ',firstSeen) AS firstSeen,
            strftime('%Y-%m-%dT%H:%M:%fZ',lastSeen) AS lastSeen,
            strftime('%Y-%m-%dT%H:%M:%fZ',previousSeen) AS previousSeen,
            round(firstSeen-previousSeen,2) AS gapDays,observations,ports,processes
        FROM flagged ORDER BY classification,firstSeen,ip LIMIT $limit OFFSET $offset
        """, parameters);
    return Results.Ok(new { host, day, gapDays, page, size, summary, destinations });
});
app.Run();

static int ParseNumber(string value, int fallback, int min, int max) {
    if (value.Length == 0) return fallback;
    if (!int.TryParse(value, out int number) || number < min || number > max) throw new ArgumentException("Invalid numeric parameter (page, size or gap days).");
    return number;
}

sealed class ViewerException(string message) : Exception(message);
sealed class ViewerDatabase(string path)
{
    public const string EventColumns = "EVENT_ID,EVENT_TYPE,TIMESTAMP_UTC,DATE_AND_TIME,SOURCE_IP,SOURCE_DOMAIN_NAME,PROCESS_NAME,PROCESS_ID,RECIEVER_IP,RECIEVER_DOMAINNAME,PORT_USED,SOFTWARE_THAT_USED,SOURCE_PORT,PROTOCOL,STATE,OPERATING_SYSTEM,MONITORED_HOST,SNAPSHOT_ID,COLLECTOR_PEER_IP,received_utc";
    public SqliteConnection Open() {
        if (!File.Exists(path)) throw new ViewerException("Database not found. Set WATCHER_DB_PATH to the collector's watcher-events.db file, then restart the dashboard.");
        var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, DefaultTimeout = 5 }.ToString());
        try {
            db.Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = "PRAGMA query_only=ON;";
            cmd.ExecuteNonQuery();
            cmd.CommandText = "SELECT " + EventColumns + ",date_utc FROM events LIMIT 0";
            try { using var reader = cmd.ExecuteReader(); }
            catch (SqliteException) { throw new ViewerException("Unsupported database schema. Start the updated collector once to migrate events into separate columns; then refresh this page."); }
            return db;
        }
        catch { db.Dispose(); throw; }
    }
    public static List<Dictionary<string, object?>> Query(SqliteConnection db, string sql, Dictionary<string, object>? parameters = null) {
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        if (parameters is not null) foreach (var pair in parameters) cmd.Parameters.AddWithValue(pair.Key, pair.Value);
        using var reader = cmd.ExecuteReader();
        var rows = new List<Dictionary<string, object?>>();
        while (reader.Read()) {
            var row = new Dictionary<string, object?>();
            for (int i = 0; i < reader.FieldCount; i++) row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            rows.Add(row);
        }
        return rows;
    }
}
