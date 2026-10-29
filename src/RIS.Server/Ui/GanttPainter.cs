using RIS.Server.Services;

namespace RIS.Server.Ui;

public enum GanttShapeKind
{
    Rect,
    Circle,
    Line,
    Text,
    Polygon
}

/// <summary>
/// One SVG element of a Gantt row. Circle: (X, Y) is the centre and W the radius; Line: (X, Y) to (X2, Y2).
/// <paramref name="Item"/> makes the shape clickable (opens the run or the job).
/// </summary>
public sealed record GanttShape(GanttShapeKind Kind, string Css, double X, double Y, double W = 0, double H = 0, double X2 = 0, double Y2 = 0,
    string? Text = null, string? Fill = null, string? Title = null, TimelineItem? Item = null, double Rx = 2, double Opacity = 1, string? Points = null);

/// <summary>What the painter needs from the chart: texts, pattern ids, the current time.</summary>
public sealed record GanttPaintContext(
    DateTimeOffset NowUtc,
    string HatchId,
    string PausedHatchId,
    Func<TimelineItem, string> Tooltip,
    Func<TimelineCell, string> CellTooltip,
    Func<string, string, string> Text);

/// <summary>Tile of the heatmap style: runs (history) or planned runs (schedule) in one time slot.</summary>
public sealed record TimelineCell(DateTimeOffset StartUtc, DateTimeOffset EndUtc, string Worst, int Count,
    IReadOnlyDictionary<string, int> ByResult, TimelineItem? Newest, bool Paused);

/// <summary>
/// Draws the items of one Gantt row in the chosen <see cref="ChartStyle"/>. Colours come from CSS classes (ris.css), so the
/// light and dark themes and the PNG export need no extra code; this class only decides shapes and positions.
/// </summary>
public static class GanttPainter
{
    public static readonly double[] CellMinutes = [5, 10, 15, 30, 60, 120, 240, 360, 720, 1440];
    public const double MinCellWidth = 12;

    /// <summary>Result classes, worst first (heatmap tile colour, Calm alarm).</summary>
    private static readonly Dictionary<string, int> Severity = new(StringComparer.Ordinal)
    {
        ["failed"] = 6, ["partial"] = 5, ["running"] = 4, ["cancelled"] = 3, ["vetoed"] = 3, ["missed"] = 2, ["succeeded"] = 1, ["skipped"] = 0
    };

    public static IReadOnlyList<GanttShape> Paint(ChartStyle style, TimelineRow row, double rowY, GanttLayout geo, GanttPaintContext ctx) =>
        style switch
        {
            ChartStyle.TypeColor => TypeColor(row, rowY, geo, ctx).ToList(),
            ChartStyle.Heatmap => Heatmap(row, rowY, geo, ctx).ToList(),
            ChartStyle.Calm => Calm(row, rowY, geo, ctx).ToList(),
            _ => Neon(row, rowY, geo, ctx).ToList()
        };

    /// <summary>CSS class of an item: the result of a run ("failed", "skipped", …) or the kind of a planned item.</summary>
    public static string Css(TimelineItem item) => item.Kind switch
    {
        TimelineItemKind.Planned => "planned",
        TimelineItemKind.PlannedPaused => "paused",
        TimelineItemKind.Band => "band",
        TimelineItemKind.BandPaused => "band paused",
        TimelineItemKind.Running => "running",
        TimelineItemKind.Missed => "missed",
        _ => (item.Status ?? "").ToLowerInvariant()
    };

    public static string TypeCss(RIS.Core.Configuration.JobType? type) => type is null ? "" : "t-" + type.Value.ToString().ToLowerInvariant();

    /// <summary>
    /// Runs (and running) whose bars touch at the current scale and have the same result become one item with a count: a job
    /// running every minute is ~10 000 bars in a week, which the browser would have to draw (and Blazor send) one by one.
    /// Clicking the merged item opens the newest run in it.
    /// </summary>
    public static IEnumerable<TimelineItem> Merge(IReadOnlyList<TimelineItem> items, GanttLayout geo)
    {
        TimelineItem? pending = null;
        double pendingEnd = 0;
        foreach (var item in items)
        {
            // Planned runs too: a job every 30 min with a 10 min median duration is a solid bar in the week view otherwise.
            var bar = item.Kind is TimelineItemKind.Run or TimelineItemKind.Planned or TimelineItemKind.PlannedPaused ? geo.Bar(item.StartUtc, item.EndUtc) : null;
            if (pending is not null && bar is not null && Css(pending) == Css(item) && geo.X(item.StartUtc) <= pendingEnd + 1)
            {
                pending = pending with { EndUtc = item.EndUtc > pending.EndUtc ? item.EndUtc : pending.EndUtc, Count = pending.Count + item.Count, RunId = item.RunId, Label = null };
                pendingEnd = Math.Max(pendingEnd, bar.Value.X + bar.Value.Width);
                continue;
            }
            if (pending is not null) yield return pending;
            if (bar is not null)
            {
                pending = item;
                pendingEnd = bar.Value.X + bar.Value.Width;
            }
            else
            {
                pending = null;
                yield return item;
            }
        }
        if (pending is not null) yield return pending;
    }

    private static double Mid(double rowY) => rowY + GanttLayout.RowHeight / 2;

    private static GanttShape Missed(TimelineItem item, double rowY, GanttLayout geo, GanttPaintContext ctx)
    {
        var x = geo.X(item.StartUtc);
        return new GanttShape(GanttShapeKind.Polygon, "misfire", x, rowY,
            Points: $"{F(x)},{F(rowY + 4)} {F(x + 5)},{F(rowY + 13)} {F(x - 5)},{F(rowY + 13)}", Title: ctx.Tooltip(item), Item: item);
    }

    private static bool Visible(TimelineItem item, GanttLayout geo) =>
        item.Kind == TimelineItemKind.Missed
            ? geo.X(item.StartUtc) >= GanttLayout.LabelWidth && geo.X(item.StartUtc) <= geo.Width
            : geo.Bar(item.StartUtc, item.EndUtc) is not null;

    private static bool IsPlan(TimelineItem item) =>
        item.Kind is TimelineItemKind.Planned or TimelineItemKind.PlannedPaused or TimelineItemKind.Band or TimelineItemKind.BandPaused;

    private static bool IsPaused(TimelineItem item) => item.Kind is TimelineItemKind.PlannedPaused or TimelineItemKind.BandPaused;

    private static bool IsBand(TimelineItem item) => item.Kind is TimelineItemKind.Band or TimelineItemKind.BandPaused;

    /// <summary>A band, or planned runs merged because they touch at this scale: drawn as a series.</summary>
    private static bool IsSeries(TimelineItem item) => IsBand(item) || (IsPlan(item) && item.Count > 1);

    private static string Faded(TimelineItem item, GanttPaintContext ctx) => IsPlan(item) && item.EndUtc < ctx.NowUtc ? " faded" : "";

    private static GanttShape? BandLabel(TimelineItem item, double x, double width, double rowY, GanttPaintContext ctx) =>
        width > 90 ? new GanttShape(GanttShapeKind.Text, "band-label", x + width - 6, rowY + 9, Text: ctx.Text("Gantt_Every", item.Label ?? Period(item))) : null;

    private static string Period(TimelineItem item) =>
        item.Count > 1 ? UiFormat.Duration((item.EndUtc - item.StartUtc) / item.Count) : "";

    /// <summary>Ticks of a frequent job on its line, at least 8 px apart (every n-th run when they are denser).</summary>
    private static IEnumerable<GanttShape> BandTicks(TimelineItem item, double x, double width, double y, double height, string css)
    {
        if (item.Count < 2) yield break;
        var step = width / (item.Count - 1);
        var every = Math.Max(1, (int)Math.Ceiling(8 / Math.Max(step, 0.01)));
        for (var i = 0; i < item.Count; i += every)
        {
            yield return new GanttShape(GanttShapeKind.Rect, css, x + i * step - .75, y, 1.5, height, Rx: 0);
        }
    }

    /* A · Neon status: colour = result, the job type only as the mark next to the name. */
    private static IEnumerable<GanttShape> Neon(TimelineRow row, double rowY, GanttLayout geo, GanttPaintContext ctx)
    {
        var type = TypeCss(row.Type);
        var mid = Mid(rowY);
        foreach (var item in Merge(row.Items, geo).Where(i => Visible(i, geo)))
        {
            if (item.Kind == TimelineItemKind.Missed) { yield return Missed(item, rowY, geo, ctx); continue; }
            var (x, width) = geo.Bar(item.StartUtc, item.EndUtc)!.Value;
            var title = ctx.Tooltip(item);
            var css = Css(item);
            if (IsPlan(item))
            {
                if (IsPaused(item))
                {
                    yield return new GanttShape(GanttShapeKind.Rect, "hatch" + Faded(item, ctx), x, mid - 4, width, 8, Fill: $"url(#{ctx.PausedHatchId})", Title: title, Item: item);
                }
                else if (IsSeries(item))
                {
                    yield return new GanttShape(GanttShapeKind.Line, $"comb ts {type}{Faded(item, ctx)}", x, mid, X2: x + width, Y2: mid, Title: title, Item: item);
                    foreach (var tick in BandTicks(item, x, width, mid - 5, 10, $"tf {type}{Faded(item, ctx)}")) yield return tick;
                }
                else
                {
                    yield return new GanttShape(GanttShapeKind.Rect, $"bar tf {type}{Faded(item, ctx)}", x, mid - 5, width, 10, Rx: 3, Title: title, Item: item);
                }
                if (IsSeries(item) && BandLabel(item, x, width, rowY, ctx) is { } label) yield return label;
                continue;
            }

            switch (css)
            {
                case "skipped" when width > 8:
                    yield return new GanttShape(GanttShapeKind.Line, "dots", x + 1, mid, X2: x + width - 1, Y2: mid, Title: title, Item: item);
                    break;
                case "skipped":
                    yield return new GanttShape(GanttShapeKind.Circle, "bar skipped", x + 2, mid, 2.2, Title: title, Item: item);
                    break;
                case "cancelled" or "vetoed":
                    yield return new GanttShape(GanttShapeKind.Rect, "bar hatch", x, mid - 6, width, 12, Rx: 3, Fill: $"url(#{ctx.HatchId})", Title: title, Item: item);
                    break;
                default:
                    yield return new GanttShape(GanttShapeKind.Rect, $"bar {css}{(css is "failed" ? " glow" : css is "running" ? " glow-run pulse" : "")}",
                        x, mid - 6, width, 12, Rx: 3, Title: title, Item: item);
                    break;
            }
        }
    }

    /* B · Colour of the job type, shape of the result. */
    private static IEnumerable<GanttShape> TypeColor(TimelineRow row, double rowY, GanttLayout geo, GanttPaintContext ctx)
    {
        var type = TypeCss(row.Type);
        var mid = Mid(rowY);
        foreach (var item in Merge(row.Items, geo).Where(i => Visible(i, geo)))
        {
            if (item.Kind == TimelineItemKind.Missed) { yield return Missed(item, rowY, geo, ctx); continue; }
            var (x, width) = geo.Bar(item.StartUtc, item.EndUtc)!.Value;
            var title = ctx.Tooltip(item);
            var css = Css(item);
            if (IsPlan(item))
            {
                if (IsPaused(item))
                {
                    yield return new GanttShape(GanttShapeKind.Rect, "hatch" + Faded(item, ctx), x, mid - 6, width, 12, Fill: $"url(#{ctx.PausedHatchId})", Title: title, Item: item);
                }
                else if (IsSeries(item))
                {
                    yield return new GanttShape(GanttShapeKind.Rect, $"band-fill tf {type}{Faded(item, ctx)}", x, mid - 6, width, 12, Title: title, Item: item);
                    yield return new GanttShape(GanttShapeKind.Rect, $"band-edge ts {type}{Faded(item, ctx)}", x, mid - 6, width, 12, Title: title, Item: item);
                    if (BandLabel(item, x, width, rowY, ctx) is { } label) yield return label;
                }
                else
                {
                    yield return new GanttShape(GanttShapeKind.Rect, $"bar tf {type}{Faded(item, ctx)}", x, mid - 6, width, 12, Title: title, Item: item);
                }
                continue;
            }

            switch (css)
            {
                case "skipped":
                    yield return new GanttShape(GanttShapeKind.Rect, $"bar low tf {type}", x, rowY + GanttLayout.RowHeight - 7, width, 3, Rx: 1, Title: title, Item: item);
                    break;
                case "cancelled" or "vetoed":
                    yield return new GanttShape(GanttShapeKind.Rect, "bar hatch", x, mid - 7, width, 14, Fill: $"url(#{ctx.HatchId})", Title: title, Item: item);
                    break;
                case "running":
                    yield return new GanttShape(GanttShapeKind.Rect, $"bar tf {type} pulse", x, mid - 7, width, 14, Title: title, Item: item);
                    break;
                default:
                    yield return new GanttShape(GanttShapeKind.Rect, $"bar tf {type}", x, mid - 7, width, 14, Title: title, Item: item);
                    if (css is "failed" or "partial")
                    {
                        yield return new GanttShape(GanttShapeKind.Rect, $"edge {css}", x, mid - 7, width, 4, Rx: 1, Title: title, Item: item);
                        yield return new GanttShape(GanttShapeKind.Polygon, $"edge {css}", x, mid,
                            Points: $"{F(x + width / 2 - 3)},{F(mid + 7)} {F(x + width / 2)},{F(mid + 3)} {F(x + width / 2 + 3)},{F(mid + 7)}", Title: title, Item: item);
                    }
                    break;
            }
        }
    }

    /// <summary>Time slots of the heatmap: the smallest of <see cref="CellMinutes"/> that is at least <see cref="MinCellWidth"/> wide.</summary>
    public static TimeSpan CellSize(GanttLayout geo)
    {
        var perMinute = geo.TimeWidth / (geo.ToUtc - geo.FromUtc).TotalMinutes;
        var minutes = CellMinutes.FirstOrDefault(m => m * perMinute >= MinCellWidth, CellMinutes[^1]);
        return TimeSpan.FromMinutes(minutes);
    }

    /// <summary>Runs or planned runs of a row grouped into time slots (from the start of the window).</summary>
    public static IReadOnlyList<TimelineCell> Cells(TimelineRow row, GanttLayout geo)
    {
        var size = CellSize(geo);
        var cells = new SortedDictionary<long, (Dictionary<string, int> ByResult, TimelineItem? Newest, bool Paused, double Planned)>();
        long Index(DateTimeOffset t) => (long)Math.Floor((t - geo.FromUtc) / size);

        void Add(long index, string result, TimelineItem? item, double count, bool paused)
        {
            if (!cells.TryGetValue(index, out var cell)) cell = (new Dictionary<string, int>(StringComparer.Ordinal), null, false, 0);
            if (result.Length > 0) cell.ByResult[result] = cell.ByResult.GetValueOrDefault(result) + (int)Math.Max(1, Math.Round(count));
            if (item is not null && (cell.Newest is null || item.StartUtc >= cell.Newest.StartUtc)) cell.Newest = item;
            cells[index] = (cell.ByResult, cell.Newest, cell.Paused || paused, cell.Planned + count);
        }

        foreach (var item in row.Items)
        {
            if (item.EndUtc < geo.FromUtc || item.StartUtc >= geo.ToUtc) continue;
            if (IsBand(item))
            {
                // A band stands for Count runs spread evenly over its length.
                var first = Math.Max(0, Index(item.StartUtc));
                var last = Index(item.EndUtc >= geo.ToUtc ? geo.ToUtc.AddTicks(-1) : item.EndUtc);
                var length = Math.Max(1, (item.EndUtc - item.StartUtc).TotalMinutes);
                for (var i = first; i <= last; i++)
                {
                    var from = geo.FromUtc + size * i;
                    var to = from + size;
                    var overlap = (Min(to, item.EndUtc) - Max(from, item.StartUtc)).TotalMinutes;
                    if (overlap > 0) Add(i, "planned", item, item.Count * overlap / length, IsPaused(item));
                }
                continue;
            }

            var index = Index(item.StartUtc < geo.FromUtc ? geo.FromUtc : item.StartUtc);
            Add(index, IsPlan(item) ? "planned" : Css(item), item, Math.Max(1, item.Count), IsPaused(item));
        }

        return cells.Select(c =>
        {
            var worst = c.Value.ByResult.Count == 0 ? "planned"
                : c.Value.ByResult.Keys.OrderByDescending(k => Severity.GetValueOrDefault(k, -1)).First();
            var start = geo.FromUtc + size * c.Key;
            var count = worst == "planned" ? (int)Math.Round(c.Value.Planned) : c.Value.ByResult.Values.Sum();
            return new TimelineCell(start, start + size, worst, Math.Max(1, count),
                c.Value.ByResult, c.Value.Newest, c.Value.Paused);
        }).ToList();
    }

    /* C · Heatmap: one tile per time slot. */
    private static IEnumerable<GanttShape> Heatmap(TimelineRow row, double rowY, GanttLayout geo, GanttPaintContext ctx)
    {
        var type = TypeCss(row.Type);
        var mid = Mid(rowY);
        foreach (var cell in Cells(row, geo))
        {
            var x1 = Math.Max(GanttLayout.LabelWidth, geo.X(cell.StartUtc)) + .75;
            var x2 = Math.Min(geo.Width, geo.X(cell.EndUtc)) - .75;
            if (x2 <= x1) continue;
            var title = ctx.CellTooltip(cell);
            if (cell.Worst == "planned")
            {
                var faded = cell.EndUtc < ctx.NowUtc ? " faded" : "";
                yield return cell.Paused
                    ? new GanttShape(GanttShapeKind.Rect, "tile hatch" + faded, x1, mid - 7, x2 - x1, 14, Rx: 2.5, Fill: $"url(#{ctx.PausedHatchId})", Title: title, Item: cell.Newest)
                    : new GanttShape(GanttShapeKind.Rect, $"tile tf {type}{faded}", x1, mid - 7, x2 - x1, 14, Rx: 2.5,
                        Opacity: .3 + .7 * Math.Min(1, cell.Count / 5.0), Title: title, Item: cell.Newest);
                continue;
            }

            var opacity = cell.Worst == "skipped" ? .3 + .35 * Math.Min(1, cell.Count / 5.0) : .6 + .4 * Math.Min(1, cell.Count / 2.0);
            yield return new GanttShape(GanttShapeKind.Rect, $"tile {cell.Worst}{(cell.Worst == "running" ? " pulse" : "")}", x1, mid - 7, x2 - x1, 14, Rx: 2.5,
                Opacity: opacity, Title: title, Item: cell.Newest);
            var failures = cell.ByResult.GetValueOrDefault("failed");
            if (failures > 1 && x2 - x1 >= 14)
            {
                yield return new GanttShape(GanttShapeKind.Text, "tile-count", (x1 + x2) / 2, mid + 4, Text: failures.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
        }
    }

    /* D · Calm and alarm: thin quiet marks, failures loud. */
    private static IEnumerable<GanttShape> Calm(TimelineRow row, double rowY, GanttLayout geo, GanttPaintContext ctx)
    {
        var type = TypeCss(row.Type);
        var mid = Mid(rowY);
        var items = Merge(row.Items, geo).Where(i => Visible(i, geo)).ToList();
        if (items.Any(i => i.Kind is TimelineItemKind.Run && Css(i) == "failed"))
        {
            yield return new GanttShape(GanttShapeKind.Circle, "alarm glow", GanttLayout.LabelWidth - 10, mid, 3.5);
        }

        var streak = 0;
        double streakEnd = 0;
        GanttShape? Flush()
        {
            var badge = streak > 1 ? new GanttShape(GanttShapeKind.Text, "streak", streakEnd + 3, mid + 4, Text: "×" + streak) : null;
            streak = 0;
            return badge;
        }

        foreach (var item in items)
        {
            var css = Css(item);
            if (item.Kind is TimelineItemKind.Run && css == "failed")
            {
                var (fx, fw) = geo.Bar(item.StartUtc, item.EndUtc)!.Value;
                yield return new GanttShape(GanttShapeKind.Rect, "bar failed glow", fx, mid - 8, fw, 16, Title: ctx.Tooltip(item), Item: item);
                streak += item.Count;
                streakEnd = fx + fw;
                continue;
            }
            if (item.Kind is TimelineItemKind.Run or TimelineItemKind.Running && Flush() is { } badge) yield return badge;

            if (item.Kind == TimelineItemKind.Missed) { yield return Missed(item, rowY, geo, ctx); continue; }
            var (x, width) = geo.Bar(item.StartUtc, item.EndUtc)!.Value;
            var title = ctx.Tooltip(item);
            if (IsPlan(item))
            {
                if (IsPaused(item))
                    yield return new GanttShape(GanttShapeKind.Line, "paused-line" + Faded(item, ctx), x, mid, X2: x + width, Y2: mid, Title: title, Item: item);
                else if (IsSeries(item))
                {
                    yield return new GanttShape(GanttShapeKind.Line, $"calm-band ts {type}{Faded(item, ctx)}", x, mid, X2: x + width, Y2: mid, Title: title, Item: item);
                    if (BandLabel(item, x, width, rowY, ctx) is { } label) yield return label;
                }
                else
                    yield return new GanttShape(GanttShapeKind.Rect, $"bar tf {type}{Faded(item, ctx)}", x, mid - 5, 3, 10, Rx: 1, Title: title, Item: item);
                continue;
            }

            switch (css)
            {
                case "running":
                    yield return new GanttShape(GanttShapeKind.Rect, "bar running glow-run pulse", x, mid - 5, width, 10, Title: title, Item: item);
                    break;
                case "succeeded":
                    yield return new GanttShape(GanttShapeKind.Rect, "bar succeeded", x, mid - 5, Math.Min(width, item.Count > 1 ? width : 3), 10, Rx: 1, Title: title, Item: item);
                    break;
                case "cancelled" or "vetoed" or "partial":
                    yield return new GanttShape(GanttShapeKind.Rect, $"bar {(css == "partial" ? "partial" : "cancelled")}", x, mid - 5, Math.Min(width, 5), 10, Rx: 1, Title: title, Item: item);
                    break;
                default: // skipped: a hairline in the colour of the type
                    yield return item.Count > 1 && width > 8
                        ? new GanttShape(GanttShapeKind.Line, $"calm-skip ts {type}", x, mid, X2: x + width, Y2: mid, Title: title, Item: item)
                        : new GanttShape(GanttShapeKind.Rect, $"calm-tick tf {type}", x, mid - 3, 1.2, 6, Rx: 0, Title: title, Item: item);
                    break;
            }
        }
        if (Flush() is { } last) yield return last;
    }

    private static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a < b ? a : b;
    private static DateTimeOffset Max(DateTimeOffset a, DateTimeOffset b) => a > b ? a : b;
    private static string F(double value) => value.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);
}
