using RIS.Core.Options;

namespace RIS.Core.Polling;

/// <summary>
/// Time source used when waiting for D365. Replaced by a fake in tests.
/// </summary>
public interface IPollingClock
{
    DateTime UtcNow { get; }

    Task Delay(TimeSpan delay, CancellationToken cancellationToken);
}

public sealed class SystemPollingClock : IPollingClock
{
    public static SystemPollingClock Instance { get; } = new();

    public DateTime UtcNow => DateTime.UtcNow;

    public Task Delay(TimeSpan delay, CancellationToken cancellationToken) => Task.Delay(delay, cancellationToken);
}

/// <summary>
/// Growing delay between status checks: initial, initial*factor, ... up to maximum.
/// </summary>
public sealed class StatusCheckBackoff
{
    private readonly TimeSpan _max;
    private readonly double _factor;
    private TimeSpan _next;

    public StatusCheckBackoff(PollingOptions options)
        : this(options.InitialDelay, options.MaxDelay, options.GrowthFactor)
    {
    }

    public StatusCheckBackoff(TimeSpan initial, TimeSpan max, double factor = 1.5)
    {
        var start = initial > TimeSpan.Zero ? initial : PollingOptions.DefaultInitialDelay;
        var limit = max > TimeSpan.Zero ? max : PollingOptions.DefaultMaxDelay;
        _max = limit < start ? start : limit;
        _factor = factor >= 1 ? factor : 1;
        _next = start;
    }

    public TimeSpan Next()
    {
        var current = _next;
        var grown = TimeSpan.FromTicks((long)Math.Min(_next.Ticks * _factor, _max.Ticks));
        _next = grown;
        return current;
    }
}
