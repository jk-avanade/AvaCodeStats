using System.Globalization;
using System.Text.Json;

namespace AvaCodeStats.Core.Providers;

/// <summary>Lenient JSON property readers shared by the providers: a missing or mistyped property gives null/0.</summary>
internal static class JsonHelpers
{
    internal static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    internal static double Num(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;

    internal static long Long(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? (long)v.GetDouble() : 0;

    internal static bool TryDate(JsonElement e, string name, out DateTimeOffset value)
    {
        value = default;
        return Str(e, name) is { } s
               && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out value);
    }
}
