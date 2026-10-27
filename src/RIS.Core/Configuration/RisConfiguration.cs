using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using RIS.Core.Options;

namespace RIS.Core.Configuration;

/// <summary>
/// Type of a RIS job.
/// </summary>
public enum JobType
{
    Export,
    Import,
    Upload,
    Download,
    ExecutionMonitor,
    ProcessingMonitor
}

/// <summary>
/// What happens with a run that was due while the service was not running.
/// </summary>
public enum MissedRunPolicy
{
    /// <summary>Run once after the service starts, then continue with the regular rhythm.</summary>
    RunOnce,

    /// <summary>Skip missed runs and wait for the next regular time.</summary>
    Skip
}

/// <summary>
/// When a job runs. Exactly one of <see cref="Cron"/> or <see cref="Interval"/> must be set.
/// The rhythm depends only on configuration (start time, interval, time zone), never on the moment the service started.
/// </summary>
public sealed class ScheduleOptions : IValidatableObject
{
    public static readonly TimeSpan MinimumInterval = TimeSpan.FromMinutes(1);

    /// <summary>Quartz cron expression (seconds first), e.g. "0 0 2 * * ?".</summary>
    public string? Cron { get; set; }

    /// <summary>Fixed interval between runs, aligned to <see cref="StartAt"/>.</summary>
    public TimeSpan? Interval { get; set; }

    /// <summary>
    /// Reference point of an interval schedule, e.g. 2026-10-01T12:00 with interval 2 h means 12:00, 14:00, 16:00 ...
    /// Required for interval schedules.
    /// </summary>
    public DateTimeOffset? StartAt { get; set; }

    /// <summary>
    /// Interval schedules: daily window, e.g. 08:00–16:00 every 10 min = 08:00, 08:10 … 16:00 (both ends included).
    /// The rhythm starts at <see cref="ActiveFrom"/> every day. Both or none.
    /// </summary>
    public TimeOnly? ActiveFrom { get; set; }

    /// <inheritdoc cref="ActiveFrom"/>
    public TimeOnly? ActiveTo { get; set; }

    /// <summary>Interval schedules: only on these days of the week (null = every day).</summary>
    public List<DayOfWeek>? Days { get; set; }

    /// <summary>Interval schedule restricted to a daily window and/or days of the week.</summary>
    [JsonIgnore]
    public bool HasWindow => ActiveFrom is not null || Days is { Count: > 0 and < 7 };

    /// <summary>Windows or IANA time zone id (cron and interval). Null means time zone of the server.</summary>
    public string? TimeZone { get; set; }

    /// <summary>What happens with a run missed while the service was not running.</summary>
    public MissedRunPolicy MissedRuns { get; set; } = MissedRunPolicy.RunOnce;

    /// <summary>Job is scheduled but paused.</summary>
    public bool Paused { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (string.IsNullOrWhiteSpace(Cron) == (Interval is null))
        {
            yield return new ValidationResult("Exactly one of Cron or Interval must be set.", [nameof(Cron), nameof(Interval)]);
        }

        if (Interval is { } interval)
        {
            if (interval < MinimumInterval || interval.Ticks % TimeSpan.TicksPerSecond != 0)
            {
                yield return new ValidationResult("Interval must be at least 1 minute and in whole seconds.", [nameof(Interval)]);
            }

            if (StartAt is null)
            {
                yield return new ValidationResult(
                    "StartAt is required for interval schedules: it is the reference time of the rhythm (e.g. 12:00 for 'every 2 hours from 12:00').",
                    [nameof(StartAt)]);
            }
        }

        if ((ActiveFrom is null) != (ActiveTo is null))
        {
            yield return new ValidationResult("ActiveFrom and ActiveTo must be set together.", [nameof(ActiveFrom), nameof(ActiveTo)]);
        }
        else if (ActiveFrom is { } from && ActiveTo is { } to && from >= to)
        {
            yield return new ValidationResult("ActiveFrom must be earlier than ActiveTo.", [nameof(ActiveFrom), nameof(ActiveTo)]);
        }

        if ((ActiveFrom is not null || Days is not null) && Interval is null)
        {
            yield return new ValidationResult("ActiveFrom/ActiveTo and Days apply to interval schedules only (use the cron for other schedules).",
                [nameof(ActiveFrom), nameof(Days)]);
        }
        else if (ActiveFrom is not null || Days is not null)
        {
            if (Interval >= TimeSpan.FromDays(1) || Interval!.Value.Ticks % TimeSpan.TicksPerMinute != 0)
            {
                yield return new ValidationResult("With a daily window or days of the week the interval must be whole minutes, shorter than a day.", [nameof(Interval)]);
            }
            if (Days is { Count: 0 })
            {
                yield return new ValidationResult("Days must contain at least one day (or be omitted for every day).", [nameof(Days)]);
            }
        }

        if (!string.IsNullOrWhiteSpace(TimeZone) && !TimeZoneInfo.TryFindSystemTimeZoneById(TimeZone, out _))
        {
            yield return new ValidationResult($"Time zone '{TimeZone}' is not known on this machine.", [nameof(TimeZone)]);
        }
    }
}

/// <summary>
/// A configured job: identity, schedule and type specific options.
/// </summary>
public sealed class JobDefinition
{
    [Required]
    public string Name { get; set; } = string.Empty;

    public string Group { get; set; } = "Default";

    public string? Description { get; set; }

    [Required]
    public ScheduleOptions Schedule { get; set; } = new();

    /// <summary>Type specific options; JSON property "type" selects the job type.</summary>
    [Required]
    public JobOptionsBase? Options { get; set; }

    /// <summary>Name of the related job (import/upload and its monitor). Replaces 4.x "-Execution monitor" naming convention.</summary>
    public string? PairedJob { get; set; }

    [JsonIgnore]
    public JobType Type => Options switch
    {
        ExportJobOptions => JobType.Export,
        ImportJobOptions => JobType.Import,
        UploadJobOptions => JobType.Upload,
        DownloadJobOptions => JobType.Download,
        ExecutionMonitorJobOptions => JobType.ExecutionMonitor,
        ProcessingMonitorJobOptions => JobType.ProcessingMonitor,
        _ => throw new InvalidOperationException($"Job '{Name}' has no options.")
    };

    /// <summary>Key used in logs and in the scheduler: "Group.Name" (same as 4.x JobKey).</summary>
    [JsonIgnore]
    public string Key => $"{Group}.{Name}";
}

/// <summary>
/// Root of RIS configuration file (jobs.json).
/// </summary>
public sealed class RisConfiguration
{
    public List<D365ConnectionOptions> Connections { get; set; } = [];

    public List<SftpServerOptions> SftpServers { get; set; } = [];

    public List<JobDefinition> Jobs { get; set; } = [];

    public D365ConnectionOptions? FindConnection(string name) =>
        Connections.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));

    public SftpServerOptions? FindSftpServer(string name) =>
        SftpServers.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));

    public JobDefinition? FindJob(string nameOrKey) =>
        Jobs.FirstOrDefault(j => string.Equals(j.Key, nameOrKey, StringComparison.OrdinalIgnoreCase))
        ?? Jobs.FirstOrDefault(j => string.Equals(j.Name, nameOrKey, StringComparison.OrdinalIgnoreCase));
}
