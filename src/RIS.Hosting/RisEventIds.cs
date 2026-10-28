using Serilog.Events;
using Serilog.Sinks.EventLog;

namespace RIS.Hosting;

/// <summary>
/// Stable Windows Event Log ids, so monitoring can filter on them (the sink would otherwise use a hash of the message):
/// <list type="bullet">
/// <item>1000 service started, 1001 service stopping (written although they are Information);</item>
/// <item>2000 job run failed / partly failed (summary of the run);</item>
/// <item>10000 + 100 × category + number for errors with a RIS code, e.g. RIS-HTTP-001 = 10301, RIS-IMP-004 = 10504
/// (categories in the order of <see cref="Categories"/>);</item>
/// <item>3000 other warnings, 4000 other errors, 1100 other information.</item>
/// </list>
/// </summary>
public sealed class RisEventIds : IEventIdProvider
{
    public const ushort ServiceStarted = 1000;
    public const ushort ServiceStopping = 1001;
    public const ushort JobRunFailed = 2000;

    /// <summary>Property that sends an Information event to the Event Log and chooses its id.</summary>
    public const string EventLogProperty = "EventLogId";

    public static readonly string[] Categories = ["CFG", "AUTH", "HTTP", "EXP", "IMP", "UPL", "DWN", "MON", "FS", "SFTP", "GEN"];

    public ushort ComputeEventId(LogEvent logEvent)
    {
        if (logEvent.Properties.TryGetValue(EventLogProperty, out var explicitId) && explicitId is ScalarValue { Value: int id })
        {
            return (ushort)id;
        }

        if (logEvent.Properties.TryGetValue("ErrorCode", out var code) && code is ScalarValue { Value: string text } && ForErrorCode(text) is { } fromCode)
        {
            return fromCode;
        }

        if (logEvent.MessageTemplate.Text.StartsWith("Job {JobKey} finished", StringComparison.Ordinal))
        {
            return JobRunFailed;
        }

        return logEvent.Level switch
        {
            >= LogEventLevel.Error => 4000,
            LogEventLevel.Warning => 3000,
            _ => 1100
        };
    }

    /// <summary>RIS-HTTP-001 → 10301; null for anything else.</summary>
    public static ushort? ForErrorCode(string code)
    {
        var parts = code.Split('-');
        if (parts.Length != 3 || parts[0] != "RIS" || !int.TryParse(parts[2], out var number) || number is < 0 or > 99) return null;
        var category = Array.IndexOf(Categories, parts[1]);
        return category < 0 ? null : (ushort)(10000 + 100 * (category + 1) + number);
    }
}
