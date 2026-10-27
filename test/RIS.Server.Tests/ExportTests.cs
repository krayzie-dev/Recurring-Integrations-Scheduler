using System.Globalization;
using System.Text;
using ClosedXML.Excel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using RIS.Server.Resources;
using RIS.Server.Services;

namespace RIS.Server.Tests;

[TestClass]
public sealed class ExportTests
{
    private static ServiceProvider _services = null!;

    [ClassInitialize]
    public static void Initialize(TestContext _) =>
        _services = new ServiceCollection().AddLogging().AddLocalization().BuildServiceProvider();

    [ClassCleanup]
    public static void Cleanup() => _services.Dispose();

    private static ExportService Service() => new(_services.GetRequiredService<IStringLocalizer<UiText>>(), TimeProvider.System);

    private static readonly RunDetails[] Runs =
    [
        new("run-1", "e1", "Export jobs.CustInvoiceJour", new DateTimeOffset(2026, 10, 27, 1, 0, 0, TimeSpan.Zero), TimeSpan.FromMinutes(95), "Failed",
            "[RIS-EXP-001] Export did not finish; \"quoted\"; text", null, null, false, null),
        new("run-2", "e2", "Warehouse.Shipments", new DateTimeOffset(2026, 10, 27, 2, 5, 0, TimeSpan.Zero), TimeSpan.FromSeconds(42), "Succeeded",
            null, null, null, true, null)
    ];

    [TestMethod]
    public void HistoryXlsx_HasTypedColumnsAndPolishHeaders()
    {
        using var _ = new CultureScope("pl-PL");
        var file = Service().History(Runs, "xlsx", TimeZoneInfo.Utc);

        Assert.AreEqual(ExportService.XlsxType, file.ContentType);
        StringAssert.StartsWith(file.FileName, "ris-history-");
        using var workbook = new XLWorkbook(new MemoryStream(file.Content));
        var sheet = workbook.Worksheet(1);
        Assert.AreEqual("Start", sheet.Cell(1, 1).GetString());
        Assert.AreEqual("Wynik", sheet.Cell(1, 5).GetString());
        Assert.AreEqual(new DateTime(2026, 10, 27, 1, 0, 0), sheet.Cell(2, 1).GetDateTime());
        Assert.AreEqual(TimeSpan.FromMinutes(95), sheet.Cell(2, 6).GetTimeSpan());
        Assert.AreEqual("Błąd", sheet.Cell(2, 5).GetString());
        Assert.AreEqual("RIS-EXP-001", sheet.Cell(2, 7).GetString());
        Assert.AreEqual(3, sheet.LastRowUsed()!.RowNumber());
        Assert.IsTrue(sheet.AutoFilter.IsEnabled);
    }

    [TestMethod]
    public void HistoryCsv_UsesCultureSeparator_BomAndQuoting()
    {
        string Csv(string culture)
        {
            using var _ = new CultureScope(culture);
            var file = Service().History(Runs, "csv", TimeZoneInfo.Utc);
            CollectionAssert.AreEqual(Encoding.UTF8.GetPreamble(), file.Content.Take(3).ToArray(), "UTF-8 BOM for Excel.");
            return Encoding.UTF8.GetString(file.Content, 3, file.Content.Length - 3);
        }

        var polish = Csv("pl-PL").Split(Environment.NewLine);
        StringAssert.StartsWith(polish[0], "Start;Koniec;Grupa;");
        StringAssert.Contains(polish[1], "2026-10-27 01:00:00;2026-10-27 02:35:00;Export jobs;CustInvoiceJour;Błąd;1:35:00;RIS-EXP-001;");
        StringAssert.Contains(polish[1], "\"[RIS-EXP-001] Export did not finish; \"\"quoted\"\"; text\"");

        var english = Csv("en-US").Split(Environment.NewLine);
        StringAssert.StartsWith(english[0], "Started,Finished,Group,");
    }

    [TestMethod]
    public void PlanExport_ListsBandsWithCount()
    {
        var from = new DateTimeOffset(2026, 10, 27, 0, 0, 0, TimeSpan.Zero);
        var timeline = new Timeline(from, from.AddDays(1),
        [
            new TimelineRow("Warehouse.Shipments", "Shipments", "Warehouse", null,
                [new TimelineItem(from, from.AddDays(1), TimelineItemKind.Band, Label: "5 min 00 s", Count: 288)]),
            new TimelineRow("Export jobs.CustInvoiceJour", "CustInvoiceJour", "Export jobs", null,
                [new TimelineItem(from.AddHours(2), from.AddHours(2).AddMinutes(20), TimelineItemKind.PlannedPaused)])
        ]);
        using var _ = new CultureScope("en-US");
        using var workbook = new XLWorkbook(new MemoryStream(Service().Plan(timeline, "xlsx", TimeZoneInfo.Utc).Content));
        var sheet = workbook.Worksheet(1);

        Assert.AreEqual("every 5 min 00 s", sheet.Cell(2, 5).GetString());
        Assert.AreEqual(288, sheet.Cell(2, 6).GetValue<int>());
        Assert.AreEqual("Paused", sheet.Cell(3, 7).GetString());
    }

    private sealed class CultureScope : IDisposable
    {
        private readonly CultureInfo _culture = CultureInfo.CurrentCulture;
        private readonly CultureInfo _uiCulture = CultureInfo.CurrentUICulture;

        public CultureScope(string name) => CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = new CultureInfo(name);

        public void Dispose()
        {
            CultureInfo.CurrentCulture = _culture;
            CultureInfo.CurrentUICulture = _uiCulture;
        }
    }
}
