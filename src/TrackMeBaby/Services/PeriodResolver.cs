using Microsoft.Extensions.Options;

namespace TrackMeBaby.Services;

public record Period(DateTimeOffset From, DateTimeOffset To, string Label)
{
    public override string ToString() => $"{Label} ({From:yyyy-MM-dd} to {To:yyyy-MM-dd})";
}

/// <summary>
/// Turns whatever Claude passes as a date range into a concrete UTC window. Accepts ISO
/// dates and the relative keywords people actually say ("last week", "this month",
/// "last 6 months"), resolved against the configured time zone so day boundaries are local.
/// </summary>
public class PeriodResolver(IOptions<TrackerOptions> options)
{
    private readonly TimeZoneInfo _tz = ResolveTimeZone(options.Value.TimeZone);

    public static readonly string[] Keywords =
    [
        "today", "yesterday", "this_week", "last_week", "this_month", "last_month",
        "this_quarter", "last_quarter", "this_year", "last_7_days", "last_30_days",
        "last_90_days", "last_3_months", "last_6_months", "last_12_months", "all_time"
    ];

    private static TimeZoneInfo ResolveTimeZone(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return TimeZoneInfo.Local;
        try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
        catch (TimeZoneNotFoundException) { return TimeZoneInfo.Local; }
        catch (InvalidTimeZoneException) { return TimeZoneInfo.Local; }
    }

    public DateTimeOffset NowLocal() => TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, _tz);

    /// <summary>
    /// Resolves a range. <paramref name="period"/> wins if given; otherwise from/to are parsed;
    /// otherwise it falls back to <paramref name="fallback"/>.
    /// </summary>
    public Period Resolve(string? period, string? from, string? to, string fallback = "last_7_days")
    {
        if (!string.IsNullOrWhiteSpace(period)) return FromKeyword(period);

        if (string.IsNullOrWhiteSpace(from) && string.IsNullOrWhiteSpace(to))
            return FromKeyword(fallback);

        var now = NowLocal();
        var start = ParseDate(from, StartOfDay(now.AddDays(-7)));
        // A bare date as the end of a range means "through the end of that day".
        var end = string.IsNullOrWhiteSpace(to) ? now : ParseDate(to, now, endOfDayIfDateOnly: true);
        if (end < start) (start, end) = (end, start);
        return new Period(start.ToUniversalTime(), end.ToUniversalTime(), $"{start:yyyy-MM-dd} to {end:yyyy-MM-dd}");
    }

    private DateTimeOffset ParseDate(string? value, DateTimeOffset fallback, bool endOfDayIfDateOnly = false)
    {
        if (string.IsNullOrWhiteSpace(value)) return fallback;
        value = value.Trim();

        if (DateTimeOffset.TryParse(value, null, DateTimeStyles.RoundtripKind, out var dto)
            && (value.Contains('Z') || value.Contains('+') || value.Count(c => c == ':') >= 2))
            return dto;

        if (DateOnly.TryParse(value, out var d))
        {
            var time = endOfDayIfDateOnly ? new TimeOnly(23, 59, 59) : TimeOnly.MinValue;
            return ToLocal(d.ToDateTime(time));
        }

        // Not a date: maybe Claude passed a keyword into from/to.
        var normalized = Normalize(value);
        if (Keywords.Contains(normalized)) return FromKeyword(normalized).From;
        return fallback;
    }

    private static string Normalize(string s) =>
        s.Trim().ToLowerInvariant().Replace(' ', '_').Replace('-', '_');

    public Period FromKeyword(string keyword)
    {
        var now = NowLocal();
        var today = StartOfDay(now);

        switch (Normalize(keyword))
        {
            case "today":
                return Make(today, now, "today");
            case "yesterday":
                return Make(today.AddDays(-1), today.AddTicks(-1), "yesterday");
            case "this_week":
                return Make(StartOfWeek(today), now, "this week");
            case "last_week":
                var lastWeekStart = StartOfWeek(today).AddDays(-7);
                return Make(lastWeekStart, lastWeekStart.AddDays(7).AddTicks(-1), "last week");
            case "this_month":
                return Make(StartOfMonth(today), now, "this month");
            case "last_month":
                var lastMonth = StartOfMonth(today).AddMonths(-1);
                return Make(lastMonth, StartOfMonth(today).AddTicks(-1), "last month");
            case "this_quarter":
                return Make(StartOfQuarter(today), now, "this quarter");
            case "last_quarter":
                var lastQuarter = StartOfQuarter(today).AddMonths(-3);
                return Make(lastQuarter, StartOfQuarter(today).AddTicks(-1), "last quarter");
            case "this_year":
                return Make(ToLocal(new DateTime(today.Year, 1, 1)), now, "this year");
            case "last_7_days":
                return Make(today.AddDays(-6), now, "last 7 days");
            case "last_30_days":
                return Make(today.AddDays(-29), now, "last 30 days");
            case "last_90_days":
                return Make(today.AddDays(-89), now, "last 90 days");
            case "last_3_months":
                return Make(StartOfDay(now.AddMonths(-3)), now, "last 3 months");
            case "last_6_months":
                return Make(StartOfDay(now.AddMonths(-6)), now, "last 6 months");
            case "last_12_months":
                return Make(StartOfDay(now.AddMonths(-12)), now, "last 12 months");
            case "all_time":
                return Make(ToLocal(new DateTime(2000, 1, 1)), now, "all time");
            default:
                // Unrecognised: treat as last 7 days rather than failing the tool call.
                return Make(today.AddDays(-6), now, $"last 7 days (unrecognised period '{keyword}')");
        }
    }

    private static Period Make(DateTimeOffset from, DateTimeOffset to, string label) =>
        new(from.ToUniversalTime(), to.ToUniversalTime(), label);

    private DateTimeOffset ToLocal(DateTime unspecified)
    {
        var offset = _tz.GetUtcOffset(unspecified);
        return new DateTimeOffset(unspecified, offset);
    }

    private DateTimeOffset StartOfDay(DateTimeOffset value) => ToLocal(value.Date);
    private static DateTimeOffset StartOfWeek(DateTimeOffset day) =>
        day.AddDays(-(((int)day.DayOfWeek + 6) % 7)); // weeks start Monday
    private DateTimeOffset StartOfMonth(DateTimeOffset day) => ToLocal(new DateTime(day.Year, day.Month, 1));
    private DateTimeOffset StartOfQuarter(DateTimeOffset day) =>
        ToLocal(new DateTime(day.Year, ((day.Month - 1) / 3) * 3 + 1, 1));

    /// <summary>The same instant in the configured zone, for callers that need to bucket by day.</summary>
    public DateTimeOffset ToLocalTime(DateTimeOffset value) => TimeZoneInfo.ConvertTime(value, _tz);

    /// <summary>Formats a UTC instant in the configured zone, for display in tool output.</summary>
    public string Local(DateTimeOffset value) =>
        TimeZoneInfo.ConvertTime(value, _tz).ToString("yyyy-MM-dd HH:mm");

    public string LocalDate(DateTimeOffset value) =>
        TimeZoneInfo.ConvertTime(value, _tz).ToString("yyyy-MM-dd");
}
