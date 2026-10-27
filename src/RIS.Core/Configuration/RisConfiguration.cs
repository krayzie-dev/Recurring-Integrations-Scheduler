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
/// When a job runs. Exactly one of <see cref="Cron"/> or <see cref="Interval"/> must be set.
/// </summary>
public sealed class ScheduleOptions : IValidatableObject
{
    /// <summary>Quartz cron expression (seconds first), e.g. "0 0 2 * * ?".</summary>
    public string? Cron { get; set; }

    /// <summary>Fixed interval between runs.</summary>
    public TimeSpan? Interval { get; set; }

    /// <summary>Windows or IANA time zone id for cron. Null means scheduler default.</summary>
    public string? TimeZone { get; set; }

    /// <summary>First run time for interval schedules.</summary>
    public DateTimeOffset? StartAt { get; set; }

    /// <summary>Job is scheduled but paused.</summary>
    public bool Paused { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (string.IsNullOrWhiteSpace(Cron) == (Interval is null))
        {
            yield return new ValidationResult("Exactly one of Cron or Interval must be set.", [nameof(Cron), nameof(Interval)]);
        }

        if (Interval is { } interval && interval <= TimeSpan.Zero)
        {
            yield return new ValidationResult("Interval must be positive.", [nameof(Interval)]);
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
