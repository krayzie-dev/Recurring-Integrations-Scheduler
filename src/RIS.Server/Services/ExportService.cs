using System.Globalization;
using System.Text;
using ClosedXML.Excel;
using Microsoft.Extensions.Localization;
using Microsoft.JSInterop;
using RIS.Persistence;
using RIS.Server.Resources;

namespace RIS.Server.Services;

public sealed record ExportFile(string FileName, string ContentType, byte[] Content);

/// <summary>
/// Exports of lists to Excel (xlsx: header, autofilter, frozen header row, typed dates and durations) and CSV
/// (UTF-8 with BOM, separator of the user's culture so Excel opens it correctly). Headers are in the user's language,
/// times in the display time zone.
/// </summary>
public sealed class ExportService(IStringLocalizer<UiText> text, TimeProvider time)
{
    public const string XlsxType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
    public const string CsvType = "text/csv";

    private sealed record Column(string Header, Func<object?, object?> Value, string? Format = null);

    public ExportFile Jobs(IEnumerable<JobSummary> jobs, string format, TimeZoneInfo zone) => Build("ris-jobs", format, text["Nav_Jobs"], jobs.Cast<object?>(),
    [
        new(text["Job_Group"], o => ((JobSummary)o!).Group),
        new(text["Job_Name"], o => ((JobSummary)o!).Name),
        new(text["Job_Type"], o => text["JobType_" + ((JobSummary)o!).Type].Value),
        new(text["Job_Connection"], o => ((JobSummary)o!).Connection),
        new(text["Job_Schedule"], o => ((JobSummary)o!).Schedule),
        new(text["Job_State"], o => text["State_" + ((JobSummary)o!).State].Value),
        new(text["Pause_Reason"], o => ((JobSummary)o!).PauseReason),
        new(text["Job_NextRun"], o => Local(((JobSummary)o!).NextRunUtc, zone), DateFormat),
        new(text["Job_LastRun"], o => Local(((JobSummary)o!).LastRunUtc, zone), DateFormat),
        new(text["Run_Result"], o => ((JobSummary)o!).LastResult is { } r ? text["Result_" + r].Value : null),
        new(text["Job_LastSuccess"], o => Local(((JobSummary)o!).LastSucceededUtc, zone), DateFormat),
        new(text["Job_Failures"], o => ((JobSummary)o!).ConsecutiveFailures),
        new(text["Run_Summary"], o => ((JobSummary)o!).LastSummary),
        new(text["Job_Description"], o => ((JobSummary)o!).Description)
    ]);

    public ExportFile History(IEnumerable<RunDetails> runs, string format, TimeZoneInfo zone) => Build("ris-history", format, text["Nav_History"], runs.Cast<object?>(),
    [
        new(text["Run_Started"], o => Local(((RunDetails)o!).FiredAtUtc, zone), DateFormat),
        new(text["Run_Finished"], o => Local(((RunDetails)o!).EndUtc, zone), DateFormat),
        new(text["Job_Group"], o => ((RunDetails)o!).Group),
        new(text["Job_Name"], o => ((RunDetails)o!).Name),
        new(text["Run_Result"], o => text["Result_" + ((RunDetails)o!).Result].Value),
        new(text["Run_Duration"], o => ((RunDetails)o!).Duration, DurationFormat),
        new(text["Run_ErrorCode"], o => ((RunDetails)o!).ErrorCode),
        new(text["Run_Summary"], o => ((RunDetails)o!).Summary),
        new(text["Run_ExecutionId"], o => ((RunDetails)o!).ExecutionId),
        new(text["Run_Manual"], o => ((RunDetails)o!).Manual ? text["Common_Yes"].Value : text["Common_No"].Value),
        new(text["Run_Id"], o => ((RunDetails)o!).RunId)
    ]);

    /// <summary>Planned runs, one line per run (bands of frequent jobs: one line with the count).</summary>
    public ExportFile Plan(Timeline timeline, string format, TimeZoneInfo zone)
    {
        var lines = timeline.Rows.SelectMany(row => row.Items.Select(item => (row, item))).OrderBy(x => x.item.StartUtc).Cast<object?>();
        return Build("ris-plan", format, text["Nav_Plan"], lines,
        [
            new(text["Plan_Start"], o => Local(Line(o).item.StartUtc, zone), DateFormat),
            new(text["Plan_EstimatedEnd"], o => Local(Line(o).item.EndUtc, zone), DateFormat),
            new(text["Gantt_GroupBy_Group"], o => JobRef.Parse(Line(o).row.Key).Group),
            new(text["Job_Name"], o => Line(o).row.Label),
            new(text["Plan_Kind"], o => Line(o).item.Kind switch
            {
                TimelineItemKind.Band or TimelineItemKind.BandPaused => text["Gantt_Every", Line(o).item.Label ?? ""].Value,
                _ => text["Gantt_Planned"].Value
            }),
            new(text["Plan_Runs"], o => Line(o).item.Count),
            new(text["Job_State"], o => Line(o).item.Kind is TimelineItemKind.PlannedPaused or TimelineItemKind.BandPaused ? text["State_Paused"].Value : text["State_Normal"].Value)
        ]);

        static (TimelineRow row, TimelineItem item) Line(object? o) => ((TimelineRow, TimelineItem))o!;
    }

    public ExportFile Audit(IEnumerable<AuditEntry> entries, string format, TimeZoneInfo zone) => Build("ris-audit", format, text["Nav_Audit"], entries.Cast<object?>(),
    [
        new(text["Audit_Time"], o => Local(new DateTimeOffset(((AuditEntry)o!).TimestampUtc, TimeSpan.Zero), zone), DateFormat),
        new(text["Audit_User"], o => ((AuditEntry)o!).User),
        new(text["Audit_Entity"], o => ((AuditEntry)o!).EntityType),
        new(text["Audit_Key"], o => ((AuditEntry)o!).EntityKey),
        new(text["Audit_Action"], o => ((AuditEntry)o!).Action),
        new(text["Audit_Before"], o => ((AuditEntry)o!).Before),
        new(text["Audit_After"], o => ((AuditEntry)o!).After)
    ]);

    /// <summary>Sends the file to the browser (Blazor).</summary>
    public static async Task DownloadAsync(IJSRuntime js, ExportFile file)
    {
        using var stream = new MemoryStream(file.Content);
        using var reference = new DotNetStreamReference(stream);
        await js.InvokeVoidAsync("ris.downloadFile", file.FileName, file.ContentType, reference);
    }

    private const string DateFormat = "yyyy-mm-dd hh:mm:ss";
    private const string DurationFormat = "[h]:mm:ss";

    private static DateTime? Local(DateTimeOffset? utc, TimeZoneInfo zone) =>
        utc is null ? null : TimeZoneInfo.ConvertTime(utc.Value, zone).DateTime;

    private ExportFile Build(string name, string format, string sheet, IEnumerable<object?> rows, IReadOnlyList<Column> columns)
    {
        var stamp = TimeZoneInfo.ConvertTime(time.GetUtcNow(), TimeZoneInfo.Local).ToString("yyyyMMdd-HHmm", CultureInfo.InvariantCulture);
        return string.Equals(format, "csv", StringComparison.OrdinalIgnoreCase)
            ? new ExportFile($"{name}-{stamp}.csv", CsvType, Csv(rows, columns))
            : new ExportFile($"{name}-{stamp}.xlsx", XlsxType, Xlsx(sheet, rows, columns));
    }

    private static byte[] Xlsx(string sheetName, IEnumerable<object?> rows, IReadOnlyList<Column> columns)
    {
        using var workbook = new XLWorkbook();
        var sheet = workbook.Worksheets.Add(sheetName.Length > 31 ? sheetName[..31] : sheetName);
        for (var c = 0; c < columns.Count; c++)
        {
            sheet.Cell(1, c + 1).Value = columns[c].Header;
        }

        var r = 2;
        foreach (var row in rows)
        {
            for (var c = 0; c < columns.Count; c++)
            {
                var cell = sheet.Cell(r, c + 1);
                switch (columns[c].Value(row))
                {
                    case null: break;
                    case DateTime date: cell.Value = date; break;
                    case TimeSpan span: cell.Value = span; break;
                    case int number: cell.Value = number; break;
                    case var other: cell.Value = Truncate(other.ToString()); break;
                }
                if (columns[c].Format is { } numberFormat) cell.Style.NumberFormat.Format = numberFormat;
            }
            r++;
        }

        var header = sheet.Range(1, 1, 1, columns.Count);
        header.Style.Font.Bold = true;
        header.Style.Fill.BackgroundColor = XLColor.FromHtml("#DCE6F1");
        sheet.Range(1, 1, Math.Max(1, r - 1), columns.Count).SetAutoFilter();
        sheet.SheetView.FreezeRows(1);
        sheet.Columns().AdjustToContents(1, Math.Min(r, 200), 8, 80);

        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return stream.ToArray();
    }

    private static byte[] Csv(IEnumerable<object?> rows, IReadOnlyList<Column> columns)
    {
        var culture = CultureInfo.CurrentCulture;
        var separator = culture.TextInfo.ListSeparator;
        var builder = new StringBuilder();
        builder.AppendLine(string.Join(separator, columns.Select(c => Quote(c.Header, separator))));
        foreach (var row in rows)
        {
            builder.AppendLine(string.Join(separator, columns.Select(c => Quote(c.Value(row) switch
            {
                null => "",
                DateTime date => date.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                TimeSpan span => $"{(int)span.TotalHours}:{span.Minutes:00}:{span.Seconds:00}",
                IFormattable formattable => formattable.ToString(null, culture),
                var other => other.ToString() ?? ""
            }, separator))));
        }
        return [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(builder.ToString())];
    }

    private static string Quote(string value, string separator) =>
        value.Contains(separator) || value.Contains('"') || value.Contains('\n') || value.Contains('\r')
            ? "\"" + value.Replace("\"", "\"\"") + "\""
            : value;

    /// <summary>Excel cell limit is 32767 characters.</summary>
    private static string Truncate(string? value) => value is { Length: > 32000 } ? value[..32000] + "…" : value ?? "";
}
