using System.Text;
using TrackMeBaby.Services;

namespace TrackMeBaby.Ui;

/// <summary>
/// Hand-built inline SVG. No chart library, for three reasons that all matter here: the container
/// has no network, an exported report has to be one self-contained file, and a mark drawn with a
/// presentation attribute survives being pulled out of the page and rasterised to PNG.
///
/// Every chart returns a &lt;figure&gt; carrying the plot, a PNG button, and a table view — so no
/// value is ever locked inside a picture.
/// </summary>
public static class Charts
{
    private const int Width = 900;

    /// <summary>Invariant number formatting: SVG coordinates are not localised.</summary>
    private static string N(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);

    /// <summary>
    /// Columns over time — the form for "how much, when". One series, so no legend: the caption
    /// names what is plotted.
    /// </summary>
    public static string Columns(
        string title, string? caption, List<TimeBucket> buckets, string unit, string pngName,
        int slot = 0)
    {
        if (buckets.Count == 0) return Empty(title, caption, $"No {unit} in this period.");

        const int height = 210, top = 14, bottom = 176, left = 46, right = Width - 12;
        var peak = Math.Max(1, buckets.Max(b => b.Count));
        var ceiling = Ceiling(peak);
        var band = (right - left) / (double)buckets.Count;
        var barWidth = Math.Min(24, band * 0.66);

        var svg = new StringBuilder();
        svg.Append($"<svg viewBox=\"0 0 {Width} {height}\" role=\"img\" aria-label=\"{Theme.Escape(title)}\">");
        svg.Append(YAxis(left, right, top, bottom, ceiling));

        var peakIndex = buckets.FindIndex(b => b.Count == peak);
        for (var i = 0; i < buckets.Count; i++)
        {
            var bucket = buckets[i];
            var centre = left + band * (i + 0.5);
            var x = centre - barWidth / 2;
            var barHeight = ceiling == 0 ? 0 : (bottom - top) * (bucket.Count / (double)ceiling);
            var y = bottom - barHeight;
            var tip = $"<b>{Theme.EscapeTip(bucket.Label)}</b><br>{Theme.Fmt(bucket.Count)} {Theme.EscapeTip(unit)}";

            svg.Append($"<rect class=\"hit\" x=\"{N(left + band * i)}\" y=\"{top}\" width=\"{N(band)}\" "
                       + $"height=\"{bottom - top}\" data-tip=\"{tip}\"></rect>");

            if (bucket.Count > 0)
                svg.Append(RoundedColumn(x, y, barWidth, Math.Max(barHeight, 2), slot));

            // Label the peak and the final bucket only. A number on every column goes unread.
            if (bucket.Count > 0 && (i == peakIndex || i == buckets.Count - 1))
                svg.Append($"<text class=\"mark-label{(i == peakIndex ? "" : " dim")}\" "
                           + $"{(i == peakIndex ? LabelInk : DimInk)} x=\"{N(centre)}\" "
                           + $"y=\"{N(y - 6)}\" text-anchor=\"middle\">{Theme.Fmt(bucket.Count)}</text>");
        }

        svg.Append(XLabels(buckets.Select(b => b.Label).ToList(), left, band, bottom + 18));
        svg.Append("</svg>");

        return Figure(title, caption, svg.ToString(), pngName,
            Table([("Period", false), (Capitalise(unit), true)],
                buckets.Select(b => new[] { b.Label, Theme.Fmt(b.Count) })));
    }

    /// <summary>
    /// A line over time, for a rate or a duration. Gaps are drawn as gaps: a week with no sample
    /// breaks the line rather than dropping it to zero.
    /// </summary>
    public static string Line(
        string title, string? caption, List<TrendPoint> points, string unit, string pngName, int slot = 0)
    {
        var known = points.Where(p => p.Value is not null).ToList();
        if (known.Count == 0) return Empty(title, caption, "Nothing to plot in this period.");

        const int height = 210, top = 14, bottom = 176, left = 52, right = Width - 12;
        var ceiling = Ceiling(known.Max(p => p.Value!.Value));
        var band = points.Count == 1 ? 0 : (right - left) / (double)(points.Count - 1);
        var colour = Theme.SeriesColor(slot);

        double X(int index) => points.Count == 1 ? (left + right) / 2.0 : left + band * index;
        double Y(double value) => bottom - (bottom - top) * (value / ceiling);

        var svg = new StringBuilder();
        svg.Append($"<svg viewBox=\"0 0 {Width} {height}\" role=\"img\" aria-label=\"{Theme.Escape(title)}\">");
        svg.Append(YAxis(left, right, top, bottom, ceiling, hours: unit == "hours"));

        // One path per unbroken run of samples.
        var run = new List<(double X, double Y)>();
        var areas = new StringBuilder();
        var lines = new StringBuilder();
        for (var i = 0; i <= points.Count; i++)
        {
            var value = i < points.Count ? points[i].Value : null;
            if (value is not null)
            {
                run.Add((X(i), Y(value.Value)));
                continue;
            }

            if (run.Count > 0)
            {
                var path = string.Join(" ", run.Select((p, index) => $"{(index == 0 ? "M" : "L")}{N(p.X)},{N(p.Y)}"));
                if (run.Count > 1)
                    areas.Append($"<path d=\"{path} L{N(run[^1].X)},{bottom} L{N(run[0].X)},{bottom} Z\" "
                                 + $"fill=\"{colour}\" fill-opacity=\"0.10\"></path>");
                lines.Append($"<path class=\"{Theme.SeriesClass(slot)}-stroke\" d=\"{path}\" fill=\"none\" "
                             + $"stroke=\"{colour}\" stroke-width=\"2\" stroke-linejoin=\"round\" stroke-linecap=\"round\"></path>");
                run.Clear();
            }
        }

        svg.Append(areas).Append(lines);

        // Markers: the extremes and the last point, ringed in the surface colour so they stay
        // legible where they sit on the line.
        var last = known[^1];
        var highest = known.MaxBy(p => p.Value);
        foreach (var point in new[] { highest, last }.Distinct())
        {
            var index = points.IndexOf(point!);
            svg.Append($"<circle cx=\"{N(X(index))}\" cy=\"{N(Y(point!.Value!.Value))}\" r=\"4.5\" "
                       + $"fill=\"{colour}\" stroke=\"#fcfcfb\" stroke-width=\"2\"></circle>");
        }

        svg.Append($"<text class=\"mark-label\" {LabelInk} x=\"{N(X(points.IndexOf(last)))}\" "
                   + $"y=\"{N(Y(last.Value!.Value) - 11)}\" text-anchor=\"end\">"
                   + $"{Theme.Escape(DurationStats.Format(last.Value))}</text>");

        for (var i = 0; i < points.Count; i++)
        {
            var point = points[i];
            var tip = $"<b>{Theme.EscapeTip(point.Label)}</b><br>"
                      + (point.Value is null
                          ? "no merges"
                          : $"median {Theme.Escape(DurationStats.Format(point.Value))} over {point.Sample} change(s)");
            svg.Append($"<rect class=\"hit\" x=\"{N(X(i) - band / 2)}\" y=\"{top}\" width=\"{N(Math.Max(band, 8))}\" "
                       + $"height=\"{bottom - top}\" data-tip=\"{tip}\"></rect>");
        }

        svg.Append(XLabels(points.Select(p => p.Label).ToList(), left, band, bottom + 18, centred: false));
        svg.Append("</svg>");

        return Figure(title, caption, svg.ToString(), pngName,
            Table([("Week of", false), ("Median", true), ("Changes", true)],
                points.Select(p => new[]
                {
                    p.Label,
                    p.Value is null ? "—" : DurationStats.Format(p.Value),
                    Theme.Fmt(p.Sample)
                })));
    }

    /// <summary>Horizontal bars, longest first — the form for comparing named things.</summary>
    public static string Bars(
        string title, string? caption, List<Slice> slices, string unit, int slot = 0, int max = 10,
        string secondaryLabel = "lines changed")
    {
        if (slices.Count == 0) return Empty(title, caption, $"No {unit} in this period.");

        var rows = slices.OrderByDescending(s => s.Count).Take(max).ToList();
        var peak = Math.Max(1, rows.Max(r => r.Count));
        var html = new StringBuilder("<div class=\"bars\">");

        foreach (var row in rows)
        {
            var width = Math.Max(2, row.Count * 100.0 / peak);
            var tip = $"<b>{Theme.EscapeTip(row.Label)}</b><br>{Theme.Fmt(row.Count)} {Theme.EscapeTip(unit)}"
                      + (row.Secondary is null
                          ? ""
                          : $"<br>{Theme.Fmt(row.Secondary.Value)} {Theme.EscapeTip(secondaryLabel)}");
            html.Append($"""
                <div class="bar-row" data-tip="{tip}">
                  <span class="bar-label">{Theme.Escape(row.Label)}</span>
                  <span class="bar-track"><span class="bar-fill {Theme.SeriesClass(slot)}-bg"
                        style="width:{N(width)}%;background:{Theme.SeriesColor(slot)}"></span></span>
                  <span class="bar-value">{Theme.Fmt(row.Count)}</span>
                </div>
                """);
        }

        html.Append("</div>");
        if (slices.Count > max)
            html.Append($"<p class=\"tile-foot\">Showing the top {max} of {slices.Count}.</p>");

        return Figure(title, caption, html.ToString(), null,
            Table([("Name", false), (Capitalise(unit), true)],
                slices.OrderByDescending(s => s.Count).Select(s => new[] { s.Label, Theme.Fmt(s.Count) })));
    }

    /// <summary>
    /// A single 100% stacked bar for composition. Segments are separated by a 2px surface gap
    /// rather than a stroke, and the legend always carries identity.
    /// </summary>
    public static string Composition(string title, string? caption, List<Slice> slices, string unit)
    {
        if (slices.Count == 0) return Empty(title, caption, $"No {unit} in this period.");

        var total = Math.Max(1, slices.Sum(s => s.Count));
        var bar = new StringBuilder("<div class=\"stack\">");
        var legend = new StringBuilder("<div class=\"legend\">");

        for (var i = 0; i < slices.Count; i++)
        {
            var slice = slices[i];
            var share = slice.Count * 100.0 / total;
            var colour = Theme.SeriesColor(i);
            var tip = $"<b>{Theme.EscapeTip(slice.Label)}</b><br>{Theme.Fmt(slice.Count)} {Theme.EscapeTip(unit)} "
                      + $"({N(share)}%)";
            bar.Append($"<span class=\"{Theme.SeriesClass(i)}-bg\" style=\"width:{N(share)}%;background:{colour}\" "
                       + $"data-tip=\"{tip}\"></span>");
            legend.Append($"<span class=\"key\"><span class=\"swatch {Theme.SeriesClass(i)}-bg\" "
                          + $"style=\"background:{colour}\"></span>{Theme.Escape(slice.Label)} "
                          + $"<span class=\"muted\">{N(share)}%</span></span>");
        }

        bar.Append("</div>");
        legend.Append("</div>");

        return Figure(title, caption, bar + legend.ToString(), null,
            Table([("Category", false), (Capitalise(unit), true), ("Share", true)],
                slices.Select(s => new[]
                {
                    s.Label, Theme.Fmt(s.Count), $"{N(s.Count * 100.0 / total)}%"
                })));
    }

    /// <summary>
    /// The activity calendar: one cell per day, weeks as columns. Magnitude gets one hue,
    /// light to dark, and an empty day is the track colour rather than the palest step.
    /// </summary>
    public static string Heatmap(string title, string? caption, List<DayCount> days, string pngName)
    {
        if (days.Count == 0) return Empty(title, caption, "No days in this period.");

        const int cell = 13, gap = 3, labels = 30;
        var parsed = days
            .Select(d => (Date: DateOnly.ParseExact(d.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture), d.Count))
            .ToList();

        // Column 0 starts on the Monday of the first week, so rows are always Mon..Sun.
        var first = parsed[0].Date;
        var offset = ((int)first.DayOfWeek + 6) % 7;
        var start = first.AddDays(-offset);
        var weeks = (int)Math.Ceiling((parsed[^1].Date.DayNumber - start.DayNumber + 1) / 7.0);

        var peak = Math.Max(1, parsed.Max(p => p.Count));
        var byDate = parsed.ToDictionary(p => p.Date, p => p.Count);

        var width = labels + weeks * (cell + gap);
        var height = 7 * (cell + gap) + 22;
        var svg = new StringBuilder();
        svg.Append($"<svg viewBox=\"0 0 {width} {height}\" role=\"img\" aria-label=\"{Theme.Escape(title)}\" "
                   + $"style=\"max-width:{width}px\">");

        string[] rowLabels = ["Mon", "", "Wed", "", "Fri", "", "Sun"];
        for (var row = 0; row < 7; row++)
            if (rowLabels[row].Length > 0)
                svg.Append($"<text class=\"tick\" {TickInk} x=\"0\" y=\"{row * (cell + gap) + cell - 2}\">"
                           + $"{rowLabels[row]}</text>");

        var monthMarks = new List<(int Week, string Label)>();
        for (var week = 0; week < weeks; week++)
        {
            for (var row = 0; row < 7; row++)
            {
                var date = start.AddDays(week * 7 + row);
                if (!byDate.TryGetValue(date, out var count))
                {
                    if (date < parsed[0].Date || date > parsed[^1].Date) continue;
                    count = 0;
                }

                var step = count == 0 ? -1 : Level(count, peak);
                var fill = step < 0 ? "#eceae4" : Theme.Sequential[step];
                var x = labels + week * (cell + gap);
                var y = row * (cell + gap);
                var tip = $"<b>{date:ddd d MMM yyyy}</b><br>{Theme.Fmt(count)} event(s)";
                svg.Append($"<rect class=\"{(step < 0 ? "cell-0" : "")}\" x=\"{x}\" y=\"{y}\" width=\"{cell}\" "
                           + $"height=\"{cell}\" rx=\"3\" fill=\"{fill}\" data-tip=\"{tip}\"></rect>");

                if (date.Day <= 7 && row == 0) monthMarks.Add((week, date.ToString("MMM")));
            }
        }

        foreach (var (week, label) in monthMarks)
            svg.Append($"<text class=\"tick\" {TickInk} x=\"{labels + week * (cell + gap)}\" "
                       + $"y=\"{7 * (cell + gap) + 14}\">{label}</text>");

        svg.Append("</svg>");

        var active = parsed.Count(p => p.Count > 0);
        var ramp = new StringBuilder("<div class=\"legend\"><span class=\"key\">Quieter");
        foreach (var step in Theme.Sequential)
            ramp.Append($"<span class=\"swatch\" style=\"background:{step}\"></span>");
        ramp.Append($"Busier</span><span class=\"key muted\">{active} active of {parsed.Count} days</span></div>");

        return Figure(title, caption, svg + ramp.ToString(), pngName,
            Table([("Date", false), ("Events", true)],
                parsed.Where(p => p.Count > 0)
                    .OrderByDescending(p => p.Count)
                    .Take(40)
                    .Select(p => new[] { p.Date.ToString("yyyy-MM-dd"), Theme.Fmt(p.Count) })));
    }

    /// <summary>A 12-point sparkline for a stat tile. No axes, no labels — shape only.</summary>
    public static string Sparkline(List<int> values, int slot = 0)
    {
        var tail = values.TakeLast(14).ToList();
        if (tail.Count < 2) return "";

        const int width = 96, height = 22;
        var peak = Math.Max(1, tail.Max());
        var step = (double)width / (tail.Count - 1);
        var path = string.Join(" ", tail.Select((v, i) =>
            $"{(i == 0 ? "M" : "L")}{N(i * step)},{N(height - (height - 3) * (v / (double)peak))}"));

        return $"<svg viewBox=\"0 0 {width} {height}\" style=\"width:{width}px\" aria-hidden=\"true\">"
               + $"<path class=\"{Theme.SeriesClass(slot)}-stroke\" d=\"{path}\" fill=\"none\" "
               + $"stroke=\"{Theme.SeriesColor(slot)}\" stroke-width=\"2\" stroke-linecap=\"round\" "
               + $"stroke-linejoin=\"round\"></path></svg>";
    }

    /// <summary>A stat tile: label, value, and an optional foot line carrying the denominator.</summary>
    public static string Tile(string label, string value, string? foot = null, string? badge = null, string? spark = null)
    {
        var head = badge is null
            ? $"<p class=\"label\">{Theme.Escape(label)}</p>"
            : $"<div class=\"tile-head\"><p class=\"label\">{Theme.Escape(label)}</p>{badge}</div>";

        return $"<div class=\"card tile\">{head}<p class=\"tile-value\">{value}</p>"
               + (spark is null ? "" : spark)
               + (foot is null ? "" : $"<p class=\"tile-foot\">{foot}</p>")
               + "</div>";
    }

    /// <summary>
    /// Distribution of a duration, as labelled buckets. A median alone hides the tail, and the
    /// tail is usually the interesting part.
    /// </summary>
    public static string DurationBars(string title, string? caption, DurationStats stats, string unit)
    {
        if (stats.Count == 0) return Empty(title, caption, $"No {unit} to measure in this period.");

        var rows = new List<(string Label, double? Hours)>
        {
            ("Fastest", stats.MinHours),
            ("Median", stats.MedianHours),
            ("75th percentile", stats.P75Hours),
            ("90th percentile", stats.P90Hours),
            ("Slowest", stats.MaxHours)
        };

        var peak = Math.Max(0.01, stats.MaxHours ?? 0.01);
        var html = new StringBuilder("<div class=\"bars\">");
        foreach (var (label, hours) in rows)
        {
            var width = hours is null ? 0 : Math.Max(2, hours.Value * 100 / peak);
            html.Append($"""
                <div class="bar-row">
                  <span class="bar-label">{Theme.Escape(label)}</span>
                  <span class="bar-track"><span class="bar-fill"
                        style="width:{N(width)}%;background:{Theme.SeriesColor(0)}"></span></span>
                  <span class="bar-value">{Theme.Escape(DurationStats.Format(hours))}</span>
                </div>
                """);
        }
        html.Append("</div>");
        html.Append($"<p class=\"tile-foot\">Measured across {stats.Count} {Theme.Escape(unit)}.</p>");

        return Figure(title, caption, html.ToString(), null, null);
    }

    // ---- plumbing ----------------------------------------------------------------

    private static string RoundedColumn(double x, double y, double width, double height, int slot)
    {
        // 4px rounded cap, square on the baseline: a rect with rx would round all four corners.
        var radius = Math.Min(4, Math.Min(width / 2, height));
        var colour = Theme.SeriesColor(slot);
        var path = $"M{N(x)},{N(y + height)} L{N(x)},{N(y + radius)} Q{N(x)},{N(y)} {N(x + radius)},{N(y)} "
                   + $"L{N(x + width - radius)},{N(y)} Q{N(x + width)},{N(y)} {N(x + width)},{N(y + radius)} "
                   + $"L{N(x + width)},{N(y + height)} Z";
        return $"<path class=\"{Theme.SeriesClass(slot)}\" d=\"{path}\" fill=\"{colour}\"></path>";
    }

    /// <summary>
    /// Chrome carries its light-mode value as a presentation attribute as well as its class. In the
    /// page the stylesheet wins, so dark mode still repaints it; pulled out into a standalone SVG or
    /// a PNG there is no stylesheet, and the attribute is what keeps the chart legible.
    /// </summary>
    private const string TickInk = "fill=\"#898781\" font-size=\"11\" font-family=\"system-ui, sans-serif\"";
    private const string LabelInk = "fill=\"#0b0b0b\" font-size=\"11.5\" font-weight=\"600\" font-family=\"system-ui, sans-serif\"";
    private const string DimInk = "fill=\"#898781\" font-size=\"11.5\" font-family=\"system-ui, sans-serif\"";

    private static string YAxis(int left, int right, int top, int bottom, double ceiling, bool hours = false)
    {
        var svg = new StringBuilder();
        for (var i = 0; i <= 2; i++)
        {
            var value = ceiling * i / 2;
            var y = bottom - (bottom - top) * (i / 2.0);
            var isAxis = i == 0;
            svg.Append($"<line class=\"{(isAxis ? "axis-line" : "grid-line")}\" x1=\"{left}\" y1=\"{N(y)}\" "
                       + $"x2=\"{right}\" y2=\"{N(y)}\" stroke=\"{(isAxis ? "#c3c2b7" : "#e1e0d9")}\" "
                       + "stroke-width=\"1\"></line>");
            // Escaped because a duration label can be "<1 min", which would otherwise open a tag
            // and take the rest of the chart with it.
            var label = Theme.Escape(hours ? DurationStats.Format(value) : Theme.Compact(value));
            svg.Append($"<text class=\"tick num\" {TickInk} x=\"{left - 8}\" y=\"{N(y + 4)}\" "
                       + $"text-anchor=\"end\">{label}</text>");
        }
        return svg.ToString();
    }

    /// <summary>
    /// X labels, thinned until they fit. Rotating them would be worse than showing every third
    /// one — the table view has all of them anyway.
    /// </summary>
    private static string XLabels(List<string> labels, double left, double band, int y, bool centred = true)
    {
        if (labels.Count == 0) return "";
        var perLabel = centred ? band : band;
        var every = Math.Max(1, (int)Math.Ceiling(52 / Math.Max(perLabel, 1)));
        var svg = new StringBuilder();

        for (var i = 0; i < labels.Count; i++)
        {
            if (i % every != 0 && i != labels.Count - 1) continue;
            var x = centred ? left + band * (i + 0.5) : left + band * i;
            var anchor = i == labels.Count - 1 && !centred ? "end" : "middle";
            svg.Append($"<text class=\"tick\" {TickInk} x=\"{N(x)}\" y=\"{y}\" text-anchor=\"{anchor}\">"
                       + $"{Theme.Escape(labels[i])}</text>");
        }

        return svg.ToString();
    }

    /// <summary>Rounds an axis maximum up to something a reader can divide by two in their head.</summary>
    private static double Ceiling(double peak)
    {
        if (peak <= 0) return 1;
        var magnitude = Math.Pow(10, Math.Floor(Math.Log10(peak)));
        // Fine-grained steps, so a peak of 56 scales to 60 rather than 100 and the marks use the
        // height they have. Every candidate still halves cleanly for the midpoint gridline.
        foreach (var step in new[] { 1.0, 1.2, 1.4, 1.6, 2, 2.4, 3, 4, 5, 6, 8, 10 })
        {
            var candidate = step * magnitude;
            if (candidate >= peak) return candidate;
        }
        return 10 * magnitude;
    }

    private static int Level(int count, int peak)
    {
        var share = count / (double)peak;
        return share switch
        {
            <= 0.10 => 0,
            <= 0.25 => 1,
            <= 0.45 => 2,
            <= 0.70 => 3,
            <= 0.90 => 4,
            _ => 5
        };
    }

    private static string Figure(string title, string? caption, string body, string? pngName, string? table)
    {
        var download = pngName is null
            ? ""
            : $"<button class=\"png no-print\" data-png=\"{Theme.Escape(pngName)}\" type=\"button\">PNG</button>";

        return $"""
            <figure>
              <figcaption>
                <div><h3>{Theme.Escape(title)}</h3>{(caption is null ? "" : $"<span class=\"cap\">{Theme.Escape(caption)}</span>")}</div>
                {download}
              </figcaption>
              {body}
              {table ?? ""}
            </figure>
            """;
    }

    private static string Empty(string title, string? caption, string message) =>
        Figure(title, caption, $"<p class=\"muted\">{Theme.Escape(message)}</p>", null, null);

    private static string Table((string Label, bool Numeric)[] columns, IEnumerable<string[]> rows)
    {
        var list = rows.ToList();
        if (list.Count == 0) return "";

        var html = new StringBuilder("<details class=\"table-view\"><summary>Table view</summary><table><tr>");
        foreach (var (label, numeric) in columns)
            html.Append($"<th{(numeric ? " class=\"num\"" : "")}>{Theme.Escape(label)}</th>");
        html.Append("</tr>");

        foreach (var row in list)
        {
            html.Append("<tr>");
            for (var i = 0; i < row.Length; i++)
                html.Append($"<td{(i < columns.Length && columns[i].Numeric ? " class=\"num\"" : "")}>"
                            + $"{Theme.Escape(row[i])}</td>");
            html.Append("</tr>");
        }

        html.Append("</table></details>");
        return html.ToString();
    }

    private static string Capitalise(string value) =>
        value.Length == 0 ? value : char.ToUpperInvariant(value[0]) + value[1..];
}
