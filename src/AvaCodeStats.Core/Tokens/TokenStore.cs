using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;

namespace AvaCodeStats.Core.Tokens;

/// <summary>
/// Keeps token events copied out of tool logs, so history survives the tools cleaning up their own logs,
/// and remembers how far each log file has been read so every scan only parses new lines.
/// </summary>
public sealed class TokenStore
{
    private readonly string _connectionString;

    public TokenStore(string databasePath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(databasePath))!);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString();
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            PRAGMA journal_mode = WAL;
            CREATE TABLE IF NOT EXISTS token_events (
                source TEXT NOT NULL, event_id TEXT NOT NULL, ts_utc TEXT NOT NULL, session_id TEXT NOT NULL,
                model TEXT NOT NULL, input INTEGER NOT NULL, cache_read INTEGER NOT NULL,
                cache_write_5m INTEGER NOT NULL, cache_write_1h INTEGER NOT NULL, output INTEGER NOT NULL,
                speed TEXT NOT NULL,
                PRIMARY KEY (source, event_id));
            CREATE INDEX IF NOT EXISTS ix_token_events_ts ON token_events (ts_utc);
            CREATE TABLE IF NOT EXISTS scan_state (
                path TEXT PRIMARY KEY, offset INTEGER NOT NULL, context TEXT);
            CREATE TABLE IF NOT EXISTS kv (key TEXT PRIMARY KEY, value TEXT NOT NULL);
            """;
        cmd.ExecuteNonQuery();
    }

    private SqliteConnection Open()
    {
        var c = new SqliteConnection(_connectionString);
        c.Open();
        return c;
    }

    /// <summary>Inserts events; a repeated event keeps the larger counts (logs repeat a message while it streams).</summary>
    public void Upsert(IReadOnlyCollection<TokenEvent> events)
    {
        if (events.Count == 0) return;
        using var c = Open();
        using var tx = c.BeginTransaction();
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO token_events VALUES ($s, $id, $ts, $sid, $m, $in, $cr, $cw5, $cw1, $out, $sp)
            ON CONFLICT (source, event_id) DO UPDATE SET
                input = MAX(input, excluded.input), cache_read = MAX(cache_read, excluded.cache_read),
                cache_write_5m = MAX(cache_write_5m, excluded.cache_write_5m),
                cache_write_1h = MAX(cache_write_1h, excluded.cache_write_1h),
                output = MAX(output, excluded.output);
            """;
        var p = new[] { "$s", "$id", "$ts", "$sid", "$m", "$in", "$cr", "$cw5", "$cw1", "$out", "$sp" }
            .Select(n => cmd.Parameters.Add(n, SqliteType.Text)).ToArray();
        foreach (var e in events)
        {
            p[0].Value = e.Source; p[1].Value = e.EventId; p[2].Value = Iso(e.TimestampUtc); p[3].Value = e.SessionId;
            p[4].Value = e.Model; p[5].Value = e.Input; p[6].Value = e.CacheRead; p[7].Value = e.CacheWrite5m;
            p[8].Value = e.CacheWrite1h; p[9].Value = e.Output; p[10].Value = e.Speed;
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    public IReadOnlyList<TokenEvent> GetEvents(DateTimeOffset fromUtc, DateTimeOffset toUtc, IReadOnlySet<string>? sources = null)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT * FROM token_events WHERE ts_utc >= $from AND ts_utc < $to ORDER BY ts_utc;";
        cmd.Parameters.AddWithValue("$from", Iso(fromUtc));
        cmd.Parameters.AddWithValue("$to", Iso(toUtc));
        var list = new List<TokenEvent>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var source = r.GetString(0);
            if (sources is not null && !sources.Contains(source)) continue;
            list.Add(new TokenEvent(source, r.GetString(1), ParseIso(r.GetString(2)), r.GetString(3), r.GetString(4),
                r.GetInt64(5), r.GetInt64(6), r.GetInt64(7), r.GetInt64(8), r.GetInt64(9), r.GetString(10)));
        }
        return list;
    }

    public string? GetValue(string key)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT value FROM kv WHERE key = $k;";
        cmd.Parameters.AddWithValue("$k", key);
        return cmd.ExecuteScalar() as string;
    }

    public void SetValue(string key, string value)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO kv VALUES ($k, $v) ON CONFLICT (key) DO UPDATE SET value = excluded.value;";
        cmd.Parameters.AddWithValue("$k", key);
        cmd.Parameters.AddWithValue("$v", value);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Reads the complete lines appended to <paramref name="path"/> since the last scan and hands them to
    /// <paramref name="processLines"/> with the per-file context string it returned last time.
    /// A file that shrank is read again from the start.
    /// </summary>
    public void ScanAppended(string path, Func<IEnumerable<string>, string?, string?> processLines)
    {
        long offset = 0;
        string? context = null;
        using (var c = Open())
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "SELECT offset, context FROM scan_state WHERE path = $p;";
            cmd.Parameters.AddWithValue("$p", path);
            using var r = cmd.ExecuteReader();
            if (r.Read()) { offset = r.GetInt64(0); context = r.IsDBNull(1) ? null : r.GetString(1); }
        }

        byte[] bytes;
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            if (fs.Length < offset) { offset = 0; context = null; }
            if (fs.Length == offset) return;
            fs.Seek(offset, SeekOrigin.Begin);
            bytes = new byte[fs.Length - offset];
            fs.ReadExactly(bytes);
        }

        var end = Array.LastIndexOf(bytes, (byte)'\n');
        if (end < 0) return; // no complete line yet
        var text = Encoding.UTF8.GetString(bytes, 0, end + 1);
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        context = processLines(lines, context);

        using (var c = Open())
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "INSERT INTO scan_state VALUES ($p, $o, $c) ON CONFLICT (path) DO UPDATE SET offset = excluded.offset, context = excluded.context;";
            cmd.Parameters.AddWithValue("$p", path);
            cmd.Parameters.AddWithValue("$o", offset + end + 1);
            cmd.Parameters.AddWithValue("$c", (object?)context ?? DBNull.Value);
            cmd.ExecuteNonQuery();
        }
    }

    internal static string Iso(DateTimeOffset d) =>
        d.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    internal static DateTimeOffset ParseIso(string s) =>
        DateTimeOffset.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
}
