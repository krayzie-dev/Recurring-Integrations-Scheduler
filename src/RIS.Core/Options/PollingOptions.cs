using System.ComponentModel.DataAnnotations;

namespace RIS.Core.Options;

/// <summary>
/// How long and how often RIS waits for long running D365 operations.
/// Delay grows by <see cref="GrowthFactor"/> from <see cref="InitialDelay"/> up to <see cref="MaxDelay"/>.
/// </summary>
public sealed class PollingOptions
{
    public static readonly TimeSpan DefaultInitialDelay = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan DefaultMaxDelay = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromHours(12);

    /// <summary>First delay between checks. 4.x: DelayBetweenStatusCheck.</summary>
    public TimeSpan InitialDelay { get; set; } = DefaultInitialDelay;

    /// <summary>Maximum delay between checks. 4.x: MaxDelayBetweenStatusCheck.</summary>
    public TimeSpan MaxDelay { get; set; } = DefaultMaxDelay;

    /// <summary>Time after which unfinished operation is reported as an error. 4.x: StatusCheckTimeoutMinutes.</summary>
    public TimeSpan Timeout { get; set; } = DefaultTimeout;

    [Range(1.0, 10.0)]
    public double GrowthFactor { get; set; } = 1.5;
}
