using System.Globalization;
using Microsoft.Data.Sqlite;

namespace AvaCodeStats.Core.Storage;

/// <summary>SQLite history of usage samples. One row per observed change (plus an hourly heartbeat).</summary>
public sealed class UsageStore
{
    private readonly string _connectionString;

    /// <summary>Unchanged readings are only stored again after this long, to keep the DB small.</summary>
    public TimeSpan HeartbeatInterval { get; init; } = TimeSpan.FromHours(1);

    public UsageStore(string databasePath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(databasePath))!);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString();
        Initialise();
    }

    private SqliteConnection Open()
    {
        var c = new SqliteConnection(_connectionString);
        c.Open();
        return c;
    }

    private void Initialise()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            PRAGMA journal_mode = WAL;
            CREATE TABLE IF NOT EXISTS samples (
                id               INTEGER PRIMARY KEY AUTOINCREMENT,
                provider         TEXT    NOT NULL,
                account          TEXT    NOT NULL,
                metric           TEXT    NOT NULL,
                metric_name      TEXT    NOT NULL,
                ts_utc           TEXT    NOT NULL,
                used             REAL    NOT NULL,
                entitlement      REAL    NOT NULL,
                unlimited        INTEGER NOT NULL,
                period_start_utc TEXT    NOT NULL,
                period_end_utc   TEXT    NOT NULL,
                plan             TEXT
            );
            CREATE INDEX IF NOT EXISTS ix_samples_key_ts ON samples (provider, account, metric, ts_utc);
            """;
        cmd.ExecuteNonQuery();
    }

    /// <summary>Stores the snapshot unless it repeats the previous reading within the heartbeat interval.</summary>
    /// <returns>True when a row was written.</returns>
    public bool Add(UsageSnapshot s)
    {
        var last = GetLatest(s.Key);
        if (last is not null
            && last.Used.Equals(s.Used)
            && last.Entitlement.Equals(s.Entitlement)
            // An unused rolling window (Antigravity) reports "reset = now + window" on every read;
            // consecutive 0% readings are the same reading even though the period moved.
            && (last.SamePeriodAs(s) || (last.Used == 0 && s.Used == 0))
            && s.TimestampUtc - last.TimestampUtc < HeartbeatInterval)
        {
            return false;
        }

        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            INSERT INTO samples (provider, account, metric, metric_name, ts_utc, used, entitlement,
                                 unlimited, period_start_utc, period_end_utc, plan)
            VALUES ($p, $a, $m, $mn, $ts, $u, $e, $un, $ps, $pe, $plan);
            """;
        cmd.Parameters.AddWithValue("$p", s.ProviderId);
        cmd.Parameters.AddWithValue("$a", s.AccountId);
        cmd.Parameters.AddWithValue("$m", s.MetricId);
        cmd.Parameters.AddWithValue("$mn", s.MetricName);
        cmd.Parameters.AddWithValue("$ts", Iso(s.TimestampUtc));
        cmd.Parameters.AddWithValue("$u", s.Used);
        cmd.Parameters.AddWithValue("$e", s.Entitlement);
        cmd.Parameters.AddWithValue("$un", s.Unlimited ? 1 : 0);
        cmd.Parameters.AddWithValue("$ps", Iso(s.PeriodStartUtc));
        cmd.Parameters.AddWithValue("$pe", Iso(s.PeriodEndUtc));
        cmd.Parameters.AddWithValue("$plan", (object?)s.PlanName ?? DBNull.Value);
        cmd.ExecuteNonQuery();
        return true;
    }

    public UsageSnapshot? GetLatest(SubscriptionKey key)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"{SelectColumns} WHERE provider = $p AND account = $a AND metric = $m ORDER BY ts_utc DESC LIMIT 1;";
        AddKey(cmd, key);
        return Read(cmd).FirstOrDefault();
    }

    public IReadOnlyList<UsageSnapshot> GetSamples(SubscriptionKey key, DateTimeOffset fromUtc, DateTimeOffset toUtc)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"{SelectColumns} WHERE provider = $p AND account = $a AND metric = $m AND ts_utc >= $from AND ts_utc < $to ORDER BY ts_utc;";
        AddKey(cmd, key);
        cmd.Parameters.AddWithValue("$from", Iso(fromUtc));
        cmd.Parameters.AddWithValue("$to", Iso(toUtc));
        return Read(cmd).ToList();
    }

    /// <summary>All keys ever recorded, with the latest snapshot of each.</summary>
    public IReadOnlyList<UsageSnapshot> GetLatestPerKey()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"""
            {SelectColumns} WHERE id IN (
                SELECT MAX(id) FROM samples GROUP BY provider, account, metric);
            """;
        return Read(cmd).ToList();
    }

    /// <summary>One row per billing period: the highest usage seen in it. Starts within <see cref="UsageSnapshot.PeriodJitter"/> are one period.</summary>
    public IReadOnlyList<PeriodSummary> GetPeriodSummaries(SubscriptionKey key)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT period_start_utc, MAX(period_end_utc), MAX(used), MAX(entitlement), COUNT(*)
            FROM samples
            WHERE provider = $p AND account = $a AND metric = $m
            GROUP BY period_start_utc
            ORDER BY period_start_utc;
            """;
        AddKey(cmd, key);
        var list = new List<PeriodSummary>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var row = new PeriodSummary(ParseIso(r.GetString(0)), ParseIso(r.GetString(1)), r.GetDouble(2), r.GetDouble(3), r.GetInt32(4));
            if (list.Count > 0 && UsageSnapshot.SamePeriod(list[^1].PeriodStartUtc, row.PeriodStartUtc))
            {
                var last = list[^1];
                list[^1] = last with
                {
                    PeriodEndUtc = Max(last.PeriodEndUtc, row.PeriodEndUtc),
                    Used = Math.Max(last.Used, row.Used),
                    Entitlement = Math.Max(last.Entitlement, row.Entitlement),
                    SampleCount = last.SampleCount + row.SampleCount,
                };
            }
            else
            {
                list.Add(row);
            }
        }
        return list;

        static DateTimeOffset Max(DateTimeOffset a, DateTimeOffset b) => a > b ? a : b;
    }

    private const string SelectColumns = """
        SELECT provider, account, metric, metric_name, ts_utc, used, entitlement, unlimited,
               period_start_utc, period_end_utc, plan
        FROM samples
        """;

    private static void AddKey(SqliteCommand cmd, SubscriptionKey key)
    {
        cmd.Parameters.AddWithValue("$p", key.ProviderId);
        cmd.Parameters.AddWithValue("$a", key.AccountId);
        cmd.Parameters.AddWithValue("$m", key.MetricId);
    }

    private static IEnumerable<UsageSnapshot> Read(SqliteCommand cmd)
    {
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            yield return new UsageSnapshot(
                r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3),
                r.GetDouble(5), r.GetDouble(6), r.GetInt32(7) != 0,
                ParseIso(r.GetString(8)), ParseIso(r.GetString(9)), ParseIso(r.GetString(4)),
                r.IsDBNull(10) ? null : r.GetString(10));
        }
    }

    // Fixed-width UTC ISO strings sort correctly as text.
    private static string Iso(DateTimeOffset d) =>
        d.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseIso(string s) =>
        DateTimeOffset.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
}
