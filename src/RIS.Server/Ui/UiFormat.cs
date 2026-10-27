using System.Globalization;

namespace RIS.Server.Ui;

public static class UiFormat
{
    /// <summary>Compact duration: "850 ms", "42 s", "3 min 05 s", "2 h 10 min", "1 d 4 h".</summary>
    public static string Duration(TimeSpan value) => value switch
    {
        { TotalSeconds: < 1 } => $"{value.TotalMilliseconds:0} ms",
        { TotalMinutes: < 1 } => $"{value.TotalSeconds:0} s",
        { TotalHours: < 1 } => $"{(int)value.TotalMinutes} min {value.Seconds:00} s",
        { TotalDays: < 1 } => $"{(int)value.TotalHours} h {value.Minutes:00} min",
        _ => $"{(int)value.TotalDays} d {value.Hours} h"
    };

    public static string Number(int value) => value.ToString("N0", CultureInfo.CurrentCulture);
}
