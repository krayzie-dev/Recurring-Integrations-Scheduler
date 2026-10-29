using RIS.Core.Configuration;
using RIS.Server.Services;
using RIS.Server.Ui;

namespace RIS.Server.Tests;

/// <summary>Chart styles: what the four styles draw for the cases seen on TEST (29.10.2026).</summary>
[TestClass]
public sealed class GanttPainterTests
{
    private static readonly DateTimeOffset Day = new(2026, 10, 29, 0, 0, 0, TimeSpan.Zero);

    private static GanttPaintContext Context() =>
        new(Day.AddHours(11), "h", "p", _ => "tip", _ => "cell", (key, arg) => $"{key} {arg}");

    private static TimelineItem Run(int minute, string result, double seconds = 0.5) =>
        new(Day.AddMinutes(minute), Day.AddMinutes(minute).AddSeconds(seconds), TimelineItemKind.Run, result, $"run-{minute}");

    private static TimelineRow Row(params TimelineItem[] items) =>
        new("Import jobs.TETA", "TETA", "Import jobs", "/jobs/x", items, JobType.Import);

    // Day view: 1400 px for 24 h, so 3 minutes are ~2.9 px.
    private static GanttLayout DayLayout() => new(Day, Day.AddDays(1), 1400, TimeZoneInfo.Utc);

    [TestMethod]
    public void SkippedEveryThreeMinutes_IsNoSolidBar_InAnyStyle()
    {
        var row = Row(Enumerable.Range(0, 220).Select(i => Run(i * 3, "Skipped")).ToArray());

        foreach (var style in Enum.GetValues<ChartStyle>())
        {
            var shapes = GanttPainter.Paint(style, row, 0, DayLayout(), Context());
            Assert.IsFalse(shapes.Any(s => s.Kind == GanttShapeKind.Rect && s.Css.Split(' ').Contains("skipped") && s.H >= 10 && s.W > 20),
                $"{style}: skipped runs must not look like one long run.");
            Assert.IsTrue(shapes.Count < 60, $"{style}: {shapes.Count} shapes for 220 runs (series are merged or tiled).");
        }

        var neon = GanttPainter.Paint(ChartStyle.Neon, row, 0, DayLayout(), Context());
        Assert.IsTrue(neon.Any(s => s.Kind == GanttShapeKind.Line && s.Css == "dots"), "Neon: a dotted line.");
    }

    [TestMethod]
    public void Heatmap_ColoursTileByWorstResult_AndCountsRuns()
    {
        var row = Row(Run(0, "Skipped"), Run(5, "Failed"), Run(10, "Skipped"), Run(20, "Succeeded"));

        var cells = GanttPainter.Cells(row, DayLayout());

        Assert.AreEqual(TimeSpan.FromMinutes(15), GanttPainter.CellSize(DayLayout()), "Smallest slot at least 12 px wide.");
        Assert.AreEqual(2, cells.Count);
        Assert.AreEqual("failed", cells[0].Worst);
        Assert.AreEqual(3, cells[0].Count);
        Assert.AreEqual(2, cells[0].ByResult["skipped"]);
        Assert.AreEqual("run-10", cells[0].Newest!.RunId, "Clicking a tile opens its newest run.");
        Assert.AreEqual("succeeded", cells[1].Worst);
    }

    [TestMethod]
    public void Calm_MarksFailingRow_AndCountsFailureStreak()
    {
        var row = Row(Enumerable.Range(0, 4).Select(i => Run(i * 30, "Failed", 570)).Append(Run(200, "Skipped")).ToArray());

        var shapes = GanttPainter.Paint(ChartStyle.Calm, row, 0, DayLayout(), Context());

        Assert.IsTrue(shapes.Any(s => s.Css.Contains("alarm")), "Red dot next to the job name.");
        Assert.AreEqual("×4", shapes.Single(s => s.Css == "streak").Text);
    }

    [TestMethod]
    public void PlannedRunsThatTouch_AreDrawnAsSeries()
    {
        // Week view: every 30 min with a 10 min median duration would otherwise be a solid bar.
        var week = new GanttLayout(Day, Day.AddDays(7), 1400, TimeZoneInfo.Utc);
        var planned = Enumerable.Range(0, 48).Select(i => new TimelineItem(Day.AddMinutes(i * 30), Day.AddMinutes(i * 30 + 10), TimelineItemKind.Planned)).ToArray();

        var merged = GanttPainter.Merge(planned, week).ToList();
        var shapes = GanttPainter.Paint(ChartStyle.Neon, Row(planned), 0, week, Context());

        Assert.AreEqual(1, merged.Count);
        Assert.AreEqual(48, merged[0].Count);
        Assert.IsTrue(shapes.Any(s => s.Kind == GanttShapeKind.Line && s.Css.StartsWith("comb")), "A comb, not a solid bar.");
    }

    [TestMethod]
    public void ChartStyle_IsKeptInCookie_AndOldCookiesGetDefault()
    {
        var prefs = new UiPreferences(ThemeMode.Dark, DisplayTimeZone.Utc, ChartStyle.Heatmap);

        Assert.AreEqual(prefs, UiPreferences.FromCookie(prefs.ToCookie()));
        Assert.AreEqual(ChartStyle.Neon, UiPreferences.FromCookie("Dark|Utc").Chart);
        Assert.AreEqual(ChartStyle.Neon, UiPreferences.FromCookie("Dark|Utc|Rainbow").Chart);
    }
}
