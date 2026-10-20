using System.Globalization;
using AgentIsland.Localization;

namespace AgentIsland.Model;

public enum UsageDisplayPeriod
{
    Today,
    Week,
    Month,
}

/// Display copy only: dates describe the stored summary, not the wall clock
/// at the moment a window redraws. No preferences or statistics are changed.
public static class TokenDisplayText
{
    public static string ModeLabel(TokenCountMode mode) => L10n.Tr(mode == TokenCountMode.Billable
        ? "Input + output (excluding cache reads)"
        : "All tokens (including cache)");

    public static string PeriodRange(UsageDisplayPeriod period, DateTimeOffset asOf, TimeZoneInfo? zone = null)
    {
        var date = Local(asOf, zone).Date;
        var start = period switch
        {
            UsageDisplayPeriod.Week => date.AddDays(-6),
            UsageDisplayPeriod.Month => new DateTime(date.Year, date.Month, 1),
            _ => date,
        };
        var endLabel = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        return start == date ? endLabel : $"{start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)} – {endLabel}";
    }

    public static string TimeZoneLabel(DateTimeOffset asOf, TimeZoneInfo? zone = null)
    {
        zone ??= TimeZoneInfo.Local;
        var local = Local(asOf, zone);
        var name = zone.IsDaylightSavingTime(asOf) ? zone.DaylightName : zone.StandardName;
        var offset = local.Offset;
        return $"{name} (UTC{(offset < TimeSpan.Zero ? "−" : "+")}{offset.Duration().ToString(@"hh\:mm", CultureInfo.InvariantCulture)})";
    }

    private static DateTimeOffset Local(DateTimeOffset asOf, TimeZoneInfo? zone) =>
        TimeZoneInfo.ConvertTime(asOf, zone ?? TimeZoneInfo.Local);
}
