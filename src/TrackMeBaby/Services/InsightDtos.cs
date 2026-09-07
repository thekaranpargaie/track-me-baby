namespace TrackMeBaby.Services;

/// <summary>
/// A distribution, not an average. Averages hide the tail, and the tail is where the story is:
/// a median of four hours with a p90 of nine days is a different working life from a flat two days.
/// </summary>
public record DurationStats(
    int Count,
    double? MedianHours,
    double? P75Hours,
    double? P90Hours,
    double? MeanHours,
    double? MinHours,
    double? MaxHours)
{
    public static readonly DurationStats Empty = new(0, null, null, null, null, null, null);

    public string Median => Format(MedianHours);
    public string P90 => Format(P90Hours);

    /// <summary>Hours as something readable: "3.4 h", "2.1 days", "18 min".</summary>
    public static string Format(double? hours)
    {
        if (hours is null) return "—";
        var value = hours.Value;
        return value switch
        {
            < 1.0 / 60 => "<1 min",
            < 1 => $"{value * 60:0} min",
            < 48 => $"{value:0.#} h",
            _ => $"{value / 24:0.#} days"
        };
    }
}

/// <summary>One bucket of a time series — a week or a day of counts.</summary>
public record TimeBucket(string Label, string Start, int Count);

/// <summary>
/// One point of a continuous trend. Null is a real answer — a week with nothing merged has no
/// median lead time, and drawing a zero there would invent a fact.
/// </summary>
public record TrendPoint(string Label, string Start, double? Value, int Sample);

/// <summary>A named slice of a total, for a composition chart.</summary>
public record Slice(string Key, string Label, int Count, int? Secondary = null);

/// <summary>
/// Where a metric lands against the published DORA bands. These are team-level benchmarks being
/// applied to one person's slice of the work, so they are context rather than a score — which is
/// why the band travels with a caveat wherever it is displayed.
/// </summary>
public enum DoraBand
{
    Unknown,
    Low,
    Medium,
    High,
    Elite
}

public record DeployEventDto(
    string Environment,
    string Repository,
    string Reference,
    string? Title,
    string? Url,
    string At,
    string Date,
    int Changes,
    double? LeadTimeHours,
    bool Failed,
    bool Restore);

public record EnvironmentReport(
    string Environment,
    string Trigger,
    string TriggerDescription,

    int Deployments,
    double PerWeek,
    double PerDay,
    string FrequencyLabel,
    DoraBand FrequencyBand,

    DurationStats LeadTime,
    DoraBand LeadTimeBand,

    int FailedDeployments,
    double? ChangeFailureRate,
    DoraBand ChangeFailureBand,

    DurationStats TimeToRestore,
    DoraBand RestoreBand,

    List<TimeBucket> ByWeek,
    List<DeployEventDto> Recent,
    List<string> Notes);

public record DeliveryReport(
    string Period,
    string From,
    string To,
    List<EnvironmentReport> Environments,
    List<string> Notes);

public record WipItem(
    string Kind,
    string Title,
    string? Repository,
    int? Number,
    string? Status,
    string? Url,
    double AgeDays,
    string LastMoved);

public record ReviewPartner(string Author, int Reviews);

public record FlowReport(
    string Period,
    string From,
    string To,

    // LeadTime: first commit to merge, the whole life of a change.
    // ReviewTime: open to merge, the part spent waiting on review and CI.
    // BoardCycleTime: column entry to exit, across every tracked board.
    DurationStats LeadTime,
    DurationStats ReviewTime,
    DurationStats BoardCycleTime,

    int Merged,
    int Abandoned,
    double? MergeRate,
    DurationStats BranchLife,

    List<TimeBucket> ThroughputByWeek,
    List<TimeBucket> ChurnByWeek,
    List<TrendPoint> LeadTimeByWeek,
    List<Slice> WorkTypeMix,
    List<Slice> SizeMix,
    List<Slice> RepositoryMix,
    List<Slice> BoardTimeByStatus,

    int ReviewsGiven,
    List<ReviewPartner> ReviewPartners,

    List<WipItem> OpenWork,
    double? OldestOpenDays,

    int ActiveDays,
    int PeriodDays,
    List<DayCount> Calendar,
    int WeekendDays,
    List<string> Notes);

/// <summary>One day of the activity calendar. Local dates, so the heatmap matches your week.</summary>
public record DayCount(string Date, int Count, bool Weekend);

public static class Stats
{
    public static DurationStats Durations(IEnumerable<TimeSpan> values)
    {
        var hours = values.Select(v => v.TotalHours).Where(h => h >= 0).OrderBy(h => h).ToList();
        if (hours.Count == 0) return DurationStats.Empty;

        return new DurationStats(
            hours.Count,
            Percentile(hours, 0.50),
            Percentile(hours, 0.75),
            Percentile(hours, 0.90),
            hours.Average(),
            hours[0],
            hours[^1]);
    }

    /// <summary>Linear interpolation between the two neighbouring order statistics.</summary>
    public static double? Percentile(List<double> sorted, double p)
    {
        if (sorted.Count == 0) return null;
        if (sorted.Count == 1) return sorted[0];

        var rank = p * (sorted.Count - 1);
        var lower = (int)Math.Floor(rank);
        var upper = (int)Math.Ceiling(rank);
        if (lower == upper) return sorted[lower];
        return sorted[lower] + (rank - lower) * (sorted[upper] - sorted[lower]);
    }

    /// <summary>
    /// Deployment frequency bands from the DORA reports: on-demand (daily or better), weekly,
    /// monthly, and less than monthly.
    /// </summary>
    public static DoraBand FrequencyBand(double perDay, int deployments)
    {
        if (deployments == 0) return DoraBand.Unknown;
        if (perDay >= 1) return DoraBand.Elite;
        if (perDay >= 1.0 / 7) return DoraBand.High;
        if (perDay >= 1.0 / 30) return DoraBand.Medium;
        return DoraBand.Low;
    }

    /// <summary>Lead time bands: under a day, under a week, under a month, beyond.</summary>
    public static DoraBand LeadTimeBand(double? medianHours) => medianHours switch
    {
        null => DoraBand.Unknown,
        < 24 => DoraBand.Elite,
        < 24 * 7 => DoraBand.High,
        < 24 * 30 => DoraBand.Medium,
        _ => DoraBand.Low
    };

    /// <summary>Change failure rate bands: 5%, 10%, 15%.</summary>
    public static DoraBand ChangeFailureBand(double? rate) => rate switch
    {
        null => DoraBand.Unknown,
        <= 0.05 => DoraBand.Elite,
        <= 0.10 => DoraBand.High,
        <= 0.15 => DoraBand.Medium,
        _ => DoraBand.Low
    };

    /// <summary>Time-to-restore bands: an hour, a day, a week, beyond.</summary>
    public static DoraBand RestoreBand(double? medianHours) => medianHours switch
    {
        null => DoraBand.Unknown,
        < 1 => DoraBand.Elite,
        < 24 => DoraBand.High,
        < 24 * 7 => DoraBand.Medium,
        _ => DoraBand.Low
    };

    public static string BandLabel(DoraBand band) => band switch
    {
        DoraBand.Elite => "Elite",
        DoraBand.High => "High",
        DoraBand.Medium => "Medium",
        DoraBand.Low => "Low",
        _ => "No data"
    };

    /// <summary>
    /// A deployment cadence in words. "2.4 per day" is easier to place than "0.34 per day",
    /// and "every 12 days" easier still once it drops below one a week.
    /// </summary>
    public static string FrequencyLabel(double perDay)
    {
        if (perDay <= 0) return "none";
        if (perDay >= 1) return $"{perDay:0.#} per day";
        var perWeek = perDay * 7;
        if (perWeek >= 1) return $"{perWeek:0.#} per week";
        var days = 1 / perDay;
        return days <= 60 ? $"every {days:0} days" : $"every {days / 30:0.#} months";
    }
}
