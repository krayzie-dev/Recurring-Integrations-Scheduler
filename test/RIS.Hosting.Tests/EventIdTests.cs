using Serilog.Events;
using Serilog.Parsing;

namespace RIS.Hosting.Tests;

[TestClass]
public sealed class EventIdTests
{
    private static LogEvent Event(LogEventLevel level, string template, params (string Name, object Value)[] properties) =>
        new(DateTimeOffset.Now, level, null, new MessageTemplateParser().Parse(template),
            properties.Select(p => new LogEventProperty(p.Name, new ScalarValue(p.Value))));

    [TestMethod]
    public void StableIds_ForLifecycle_ErrorCodes_AndFailedRuns()
    {
        var ids = new RisEventIds();
        Assert.AreEqual(RisEventIds.ServiceStarted, ids.ComputeEventId(Event(LogEventLevel.Information, "started", (RisEventIds.EventLogProperty, 1000))));
        Assert.AreEqual((ushort)10301, ids.ComputeEventId(Event(LogEventLevel.Error, "x", ("ErrorCode", "RIS-HTTP-001"))));
        Assert.AreEqual((ushort)10504, ids.ComputeEventId(Event(LogEventLevel.Error, "x", ("ErrorCode", "RIS-IMP-004"))));
        Assert.AreEqual((ushort)11101, ids.ComputeEventId(Event(LogEventLevel.Error, "x", ("ErrorCode", "RIS-GEN-001"))));
        Assert.AreEqual(RisEventIds.JobRunFailed, ids.ComputeEventId(Event(LogEventLevel.Warning, "Job {JobKey} finished: {Outcome}")));
        Assert.AreEqual((ushort)3000, ids.ComputeEventId(Event(LogEventLevel.Warning, "Something")));
        Assert.AreEqual((ushort)4000, ids.ComputeEventId(Event(LogEventLevel.Error, "x", ("ErrorCode", "OTHER-1"))));
    }
}
