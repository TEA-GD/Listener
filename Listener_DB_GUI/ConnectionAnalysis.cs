using System.Globalization;
using Microsoft.Data.Sqlite;

internal sealed class ConnectionAnalysis(string path)
{
    private SqliteConnection Open()
    {
        if (!File.Exists(path)) throw new ViewerException("Connection analysis is not ready. Run Listener_ConnectionAnalyzer and configure WATCHER_ANALYSIS_DB_PATH to its connection-analysis.db file.");
        var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, DefaultTimeout = 5 }.ToString());
        try
        {
            db.Open(); using var c = db.CreateCommand();
            c.CommandText = "PRAGMA query_only=ON; SELECT value FROM analysis_meta WHERE key='schema_version'";
            if (c.ExecuteScalar() as string != "1") throw new ViewerException("Unsupported connection analysis database. Run the current Listener_ConnectionAnalyzer.");
            return db;
        }
        catch (SqliteException) { db.Dispose(); throw new ViewerException("Cannot read the connection analysis database. Check its schema and read permissions, or rebuild it using Listener_ConnectionAnalyzer."); }
        catch { db.Dispose(); throw; }
    }
    internal IResult Devices()
    {
        using var db = Open();
        return Results.Ok(ViewerDatabase.Query(db, "SELECT host,COUNT(*) AS connections FROM analysis_connections GROUP BY host ORDER BY host"));
    }
    internal IResult Read(HttpRequest request)
    {
        string host = request.Query["host"].ToString(), direction = request.Query["direction"].ToString(), q = request.Query["q"].ToString();
        if (host.Length > 120 || q.Length > 200) throw new ArgumentException("Device or search value is too long");
        if (direction == "") direction = "outgoing";
        if (direction is not ("incoming" or "outgoing" or "unknown")) throw new ArgumentException("Invalid direction");
        string novelty = request.Query["novelty"].ToString();
        if (novelty is not ("" or "connections" or "peers")) throw new ArgumentException("Invalid first-seen filter");
        string zoneId = request.Query["tz"].ToString(); if (zoneId == "") zoneId = "UTC";
        TimeZoneInfo zone;
        try { zone = TimeZoneInfo.FindSystemTimeZoneById(zoneId); }
        catch (Exception e) when (e is TimeZoneNotFoundException or InvalidTimeZoneException) { throw new ArgumentException("Unknown time zone"); }
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, zone).DateTime);
        string date = request.Query["day"].ToString();
        if (date == "") date = today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (!DateOnly.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day) || day > today) throw new ArgumentException("Choose a valid day, no later than today");
        DateTimeOffset Midnight(DateOnly d)
        {
            var local = d.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
            for (int n = 0; zone.IsInvalidTime(local) && n < 1440; n++) local = local.AddMinutes(1);
            return zone.IsAmbiguousTime(local) ? new DateTimeOffset(local, zone.GetAmbiguousTimeOffsets(local).Max()) : new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, zone));
        }
        int page = 1;
        if (request.Query.ContainsKey("page") && (!int.TryParse(request.Query["page"], out page) || page is < 1 or > 1000000)) throw new ArgumentException("Invalid page");
        var parameters = new Dictionary<string, object> { ["$host"] = host, ["$q"] = q, ["$start"] = Midnight(day).ToUnixTimeMilliseconds(), ["$end"] = Midnight(day.AddDays(1)).ToUnixTimeMilliseconds() };
        using var db = Open();
        using var snapshot = db.BeginTransaction(deferred: true);
        // Counts, rows and progress describe the same completed analyzer commit.
        // Compare first event times across BOTH directions, including historical
        // Unknown evidence. Receipt order never decides whether a peer is new.
        const string common = """
            WITH profiles AS (
                SELECT c.*,
                    (SELECT MIN(h.first_seen_ms) FROM analysis_connections h WHERE h.host=c.host AND h.peer_ip=c.peer_ip AND h.protocol=c.protocol AND h.process_name=c.process_name AND h.software=c.software
                        AND (h.identity=c.identity OR h.direction='unknown' OR c.direction='unknown')) AS first_any_ms,
                    (SELECT MIN(h.first_seen_ms) FROM analysis_connections h WHERE h.host=c.host AND h.peer_ip=c.peer_ip) AS first_peer_ms
                FROM analysis_connections c
                WHERE ($host='' OR c.host=$host) AND ($q='' OR instr(lower(c.peer_ip||' '||c.local_ip||' '||c.process_name||' '||c.service_port||' '||c.host),lower($q))>0)
            )
            """;
        var summary = ViewerDatabase.Query(db, common + """
            SELECT COUNT(CASE WHEN direction='incoming' THEN 1 END) AS incoming,
                COUNT(CASE WHEN direction='outgoing' THEN 1 END) AS outgoing,
                COUNT(CASE WHEN direction='unknown' THEN 1 END) AS unknown,
                COUNT(CASE WHEN first_any_ms >= $start AND first_any_ms < $end THEN 1 END) AS new_connections,
                COUNT(DISTINCT CASE WHEN first_peer_ms >= $start AND first_peer_ms < $end THEN host||char(31)||peer_ip END) AS new_peers
            FROM profiles
            """, parameters, snapshot)[0];
        parameters["$direction"] = direction;
        string where = " WHERE direction=$direction" + (novelty == "connections" ? " AND first_any_ms >= $start AND first_any_ms < $end" : novelty == "peers" ? " AND first_peer_ms >= $start AND first_peer_ms < $end" : "");
        long total = Convert.ToInt64(ViewerDatabase.Query(db, common + "SELECT COUNT(*) AS n FROM profiles" + where, parameters, snapshot)[0]["n"]);
        parameters["$limit"] = 50; parameters["$offset"] = (page - 1) * 50;
        var rows = ViewerDatabase.Query(db, common + "SELECT * FROM profiles" + where + " ORDER BY first_any_ms DESC,host,signature LIMIT $limit OFFSET $offset", parameters, snapshot);
        var meta = ViewerDatabase.Query(db, "SELECT key,value FROM analysis_meta", transaction: snapshot).ToDictionary(r => r["key"]!.ToString()!, r => r["value"]!.ToString()!);
        return Results.Ok(new { host, direction, day = date, timezone = zoneId, novelty, page, size = 50, total, summary, rows,
            status = new { last_processed_utc = meta.GetValueOrDefault("last_processed_utc"), cursor = meta.GetValueOrDefault("cursor", "0"), source_max_rowid = meta.GetValueOrDefault("source_max_rowid", "0"), catching_up = meta.GetValueOrDefault("scan_complete") != "True" } });
    }
}
