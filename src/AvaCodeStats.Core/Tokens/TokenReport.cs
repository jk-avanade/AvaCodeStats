namespace AvaCodeStats.Core.Tokens;

public sealed record TokenTotals(long Input, long CacheRead, long CacheWrite, long Output, CostParts Cost)
{
    public long Total => Input + CacheRead + CacheWrite + Output;
    public long CachedInput => CacheRead;
    public long UncachedInput => Input + CacheWrite;

    public static TokenTotals Zero { get; } = new(0, 0, 0, 0, CostParts.Zero);

    public TokenTotals Add(TokenEvent e, CostParts c) =>
        new(Input + e.Input, CacheRead + e.CacheRead, CacheWrite + e.CacheWrite, Output + e.Output, Cost + c);
}

public sealed record GroupTotals(string Key, string ProviderId, int Sessions, TokenTotals Totals);

public sealed record Bucket(DateTimeOffset StartUtc, IReadOnlyDictionary<string, TokenTotals> ByProvider);

/// <summary>Aggregates token events for the Cost and Tokens views.</summary>
public sealed class TokenReport
{
    public required TokenTotals Totals { get; init; }
    public required int Sessions { get; init; }
    public required IReadOnlyList<GroupTotals> ByProvider { get; init; }
    public required IReadOnlyList<GroupTotals> ByModel { get; init; }
    public required IReadOnlyList<Bucket> Buckets { get; init; }
    public required CostParts StandardSpeed { get; init; }
    public required CostParts FastSpeed { get; init; }
    public required IReadOnlyList<string> UnpricedModels { get; init; }

    /// <param name="bucket">Bucket width; buckets are aligned to local midnight/hour.</param>
    public static TokenReport Build(IReadOnlyList<TokenEvent> events, PriceTable prices,
        DateTimeOffset fromUtc, DateTimeOffset toUtc, TimeSpan bucket, TimeZoneInfo zone)
    {
        var totals = TokenTotals.Zero;
        var standard = CostParts.Zero;
        var fast = CostParts.Zero;
        var byProvider = new Dictionary<string, (TokenTotals t, HashSet<string> s)>();
        var byModel = new Dictionary<string, (string provider, TokenTotals t, HashSet<string> s)>();
        var buckets = new SortedDictionary<DateTimeOffset, Dictionary<string, TokenTotals>>();
        var unpriced = new SortedSet<string>();

        var first = AlignLocal(fromUtc, bucket, zone);
        for (var b = first; b < toUtc; b = Next(b, bucket, zone)) buckets[b] = [];

        foreach (var e in events)
        {
            var c = prices.Cost(e);
            if (!c.Priced) unpriced.Add(e.Model);
            totals = totals.Add(e, c);
            if (e.Speed == "fast") fast += c; else standard += c;

            var p = byProvider.TryGetValue(e.ProviderId, out var pv) ? pv : (TokenTotals.Zero, new HashSet<string>());
            p.Item2.Add(e.SessionId);
            byProvider[e.ProviderId] = (p.Item1.Add(e, c), p.Item2);

            var m = byModel.TryGetValue(e.Model, out var mv) ? mv : (e.ProviderId, TokenTotals.Zero, new HashSet<string>());
            m.Item3.Add(e.SessionId);
            byModel[e.Model] = (m.Item1, m.Item2.Add(e, c), m.Item3);

            var key = AlignLocal(e.TimestampUtc, bucket, zone);
            if (!buckets.TryGetValue(key, out var slot)) buckets[key] = slot = [];
            slot[e.ProviderId] = slot.GetValueOrDefault(e.ProviderId, TokenTotals.Zero).Add(e, c);
        }

        return new TokenReport
        {
            Totals = totals,
            Sessions = events.Select(e => (e.Source, e.SessionId)).Distinct().Count(),
            ByProvider = byProvider.Select(kv => new GroupTotals(kv.Key, kv.Key, kv.Value.s.Count, kv.Value.t))
                .OrderByDescending(g => g.Totals.Cost.Total).ThenByDescending(g => g.Totals.Total).ToList(),
            ByModel = byModel.Select(kv => new GroupTotals(kv.Key, kv.Value.provider, kv.Value.s.Count, kv.Value.t))
                .OrderByDescending(g => g.Totals.Cost.Total).ThenByDescending(g => g.Totals.Total).ToList(),
            Buckets = buckets.Select(kv => new Bucket(kv.Key, kv.Value)).ToList(),
            StandardSpeed = standard,
            FastSpeed = fast,
            UnpricedModels = unpriced.ToList(),
        };
    }

    private static DateTimeOffset AlignLocal(DateTimeOffset utc, TimeSpan bucket, TimeZoneInfo zone)
    {
        var local = TimeZoneInfo.ConvertTime(utc, zone);
        var aligned = bucket >= TimeSpan.FromDays(1)
            ? new DateTime(local.Year, local.Month, local.Day)
            : new DateTime(local.Year, local.Month, local.Day, local.Hour, 0, 0);
        return new DateTimeOffset(aligned, zone.GetUtcOffset(aligned)).ToUniversalTime();
    }

    private static DateTimeOffset Next(DateTimeOffset bucketStartUtc, TimeSpan bucket, TimeZoneInfo zone) =>
        AlignLocal(bucketStartUtc + bucket + TimeSpan.FromHours(bucket >= TimeSpan.FromDays(1) ? 2 : 0), bucket, zone);
}
