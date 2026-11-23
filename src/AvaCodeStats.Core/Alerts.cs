namespace AvaCodeStats.Core;

public enum AlertLevel { Warning, Critical, FarOverPace }

public sealed record Alert(SubscriptionKey Key, AlertLevel Level, string Title, string Message);

/// <summary>
/// Threshold notifications: each level fires at most once per quota per period, and the
/// "already fired" marks are stored so a restart does not repeat them.
/// </summary>
public static class AlertRules
{
    public static IReadOnlyList<Alert> Evaluate(
        IEnumerable<UsageSnapshot> snapshots, AppSettings settings, DateTimeOffset now,
        Func<string, bool> alreadyFired, Action<string> markFired)
    {
        var alerts = new List<Alert>();
        foreach (var s in snapshots.Where(s => !s.Unlimited && s.Entitlement > 0))
        {
            var used = s.UsedFraction * 100;
            var pace = PaceCalculator.Calculate(s, now, settings.Thresholds);
            var name = $"{ProviderFactory.DisplayName(s.ProviderId)} {s.MetricName}";
            var profile = Profiles.Label(settings.ProfileOf(s.ProviderId, s.AccountId));
            var resets = $"Resets {s.PeriodEndUtc.ToLocalTime():ddd d MMM HH:mm}.";

            string Mark(AlertLevel level) => $"alert:{s.Key}:{s.PeriodStartUtc.UtcTicks}:{level}";
            bool Fire(AlertLevel level, string title, string message)
            {
                if (alreadyFired(Mark(level))) return false;
                markFired(Mark(level));
                alerts.Add(new Alert(s.Key, level, title, message));
                return true;
            }

            if (settings.CriticalAtPercent > 0 && used >= settings.CriticalAtPercent)
            {
                markFired(Mark(AlertLevel.Warning)); // skip the softer one once critical
                Fire(AlertLevel.Critical, $"{name}: {used:0}% used ({profile})", $"{100 - used:0}% left. {resets}");
            }
            else if (settings.WarnAtPercent > 0 && used >= settings.WarnAtPercent)
            {
                Fire(AlertLevel.Warning, $"{name}: {used:0}% used ({profile})", $"{100 - used:0}% left. {resets}");
            }

            if (settings.NotifyWhenFarOverPace && pace.Status == PaceStatus.FarOver)
            {
                var runsOut = pace.ProjectedExhaustionUtc is { } ex ? $" At this rate it runs out {ex.ToLocalTime():ddd d MMM HH:mm}." : "";
                Fire(AlertLevel.FarOverPace, $"{name}: {PaceCalculator.Describe(pace)} ({profile})",
                    $"{used:0}% used, {pace.PaceFraction * 100:0}% of the period gone.{runsOut}");
            }
        }
        return alerts;
    }

    /// <summary>How alarming a quota is, for picking the one the tray shows.</summary>
    public static int Severity(PaceStatus s) => s switch
    {
        PaceStatus.Exhausted => 5,
        PaceStatus.FarOver => 4,
        PaceStatus.SlightlyOver => 3,
        PaceStatus.OnPace => 2,
        PaceStatus.UnderPace => 1,
        _ => 0,
    };
}
