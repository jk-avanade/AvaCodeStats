namespace AvaCodeStats.Core.Tokens;

/// <summary>USD per million tokens. Cache writes default to 1.25× (5 min) and 2× (1 h) input.</summary>
public sealed class ModelPrice
{
    /// <summary>Model id prefix, e.g. "claude-opus-5-5". The longest matching prefix wins.</summary>
    public string Model { get; set; } = "";
    public double Input { get; set; }
    public double Output { get; set; }
    public double CacheRead { get; set; }
    public double? CacheWrite5m { get; set; }
    public double? CacheWrite1h { get; set; }
    /// <summary>Price multiplier when the call ran in fast mode.</summary>
    public double FastMultiplier { get; set; } = 2;

    public double Write5m => CacheWrite5m ?? Input * 1.25;
    public double Write1h => CacheWrite1h ?? Input * 2;
}

/// <param name="FastPremium">The part of the other figures that is the fast-mode surcharge (already included in <see cref="Total"/>).</param>
public sealed record CostParts(double Input, double CacheRead, double CacheWrite, double Output, double CacheSavings, bool Priced,
    double FastPremium = 0)
{
    public double Total => Input + CacheRead + CacheWrite + Output;
    public static CostParts Zero { get; } = new(0, 0, 0, 0, 0, true);

    public static CostParts operator +(CostParts a, CostParts b) =>
        new(a.Input + b.Input, a.CacheRead + b.CacheRead, a.CacheWrite + b.CacheWrite, a.Output + b.Output,
            a.CacheSavings + b.CacheSavings, a.Priced && b.Priced, a.FastPremium + b.FastPremium);
}

/// <summary>
/// API list prices used to put a dollar figure on subscription usage ("API estimate").
/// Claude prices are Anthropic first-party list prices (Nov 2026). Other vendors' models have no
/// default price: add them under "pricing" in settings.json.
/// </summary>
public sealed class PriceTable
{
    public static IReadOnlyList<ModelPrice> Defaults { get; } =
    [
        P("claude-fable-5-2", 8, 40, 0.20), P("claude-mythos-5-2", 8, 40, 0.20),
        P("claude-fable-5-1", 10, 50, 1.00), P("claude-mythos-5-1", 10, 50, 1.00),
        P("claude-fable-5", 10, 50, 1.00), P("claude-mythos-5", 10, 50, 1.00),
        P("claude-opus-5-5", 4, 20, 0.20), P("claude-opus-5", 5, 25, 0.50),
        P("claude-opus-4-8", 5, 25, 0.50), P("claude-opus-4-7", 5, 25, 0.50),
        P("claude-opus-4-6", 5, 25, 0.50), P("claude-opus-4-5", 5, 25, 0.50),
        P("claude-opus-4", 15, 75, 1.50),
        P("claude-sonnet-5-5", 2, 10, 0.10), P("claude-sonnet-5", 2, 10, 0.20),
        P("claude-sonnet-4", 3, 15, 0.30),
        P("claude-haiku-5-5", 0.10, 0.50, 0.01), P("claude-haiku-4-5", 1, 5, 0.10),
    ];

    private readonly List<ModelPrice> _prices;

    public PriceTable(IEnumerable<ModelPrice>? overrides = null)
    {
        // Overrides first so an equal-length prefix from settings beats the default.
        _prices = (overrides ?? []).Concat(Defaults).ToList();
    }

    public ModelPrice? Find(string model) => _prices
        .Where(p => model.StartsWith(p.Model, StringComparison.OrdinalIgnoreCase))
        .OrderByDescending(p => p.Model.Length)
        .FirstOrDefault();

    public CostParts Cost(TokenEvent e)
    {
        if (Find(e.Model) is not { } p) return CostParts.Zero with { Priced = false };
        var m = e.Speed == "fast" ? p.FastMultiplier : 1;
        const double M = 1_000_000;
        var input = e.Input * p.Input / M;
        var cacheRead = e.CacheRead * p.CacheRead / M;
        var cacheWrite = (e.CacheWrite5m * p.Write5m + e.CacheWrite1h * p.Write1h) / M;
        var output = e.Output * p.Output / M;
        var baseTotal = input + cacheRead + cacheWrite + output;
        return new CostParts(
            input * m, cacheRead * m, cacheWrite * m, output * m,
            e.CacheRead * (p.Input - p.CacheRead) * m / M,
            true,
            m > 1 ? baseTotal * (m - 1) : 0);
    }

    private static ModelPrice P(string model, double input, double output, double cacheRead) =>
        new() { Model = model, Input = input, Output = output, CacheRead = cacheRead };
}
