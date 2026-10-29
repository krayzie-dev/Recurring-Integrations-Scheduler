using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using RIS.Core.Errors;
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

    /// <summary>
    /// Job is scheduled but paused. The one place of the pause state: pause/resume in the UI, API and CLI, PauseOnError and
    /// the switch in the job form all change it; the scheduler follows it.
    /// </summary>
    public bool Paused { get; set; }

    /// <summary>Why the job is paused (typed by the operator, or one of the reasons set by RIS). Only when paused.</summary>
    public string? PauseReason { get; set; }

    /// <summary>Who paused the job ("RIS" for PauseOnError). Only when paused.</summary>
    public string? PausedBy { get; set; }

    /// <summary>When the job was paused. Only when paused.</summary>
    public DateTimeOffset? PausedAtUtc { get; set; }

    /// <summary>Pauses (with who, when and why) or resumes; resuming clears the details.</summary>
    public void SetPaused(bool paused, string? reason = null, string? by = null, DateTimeOffset? at = null)
    {
        Paused = paused;
        PauseReason = paused ? reason : null;
        PausedBy = paused ? by : null;
        PausedAtUtc = paused ? at : null;
    }

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

    /// <summary>
    /// One pause state: the 4.x "IndefinitePause" option becomes a pause of the schedule, and details of a pause are kept only
    /// while the job is paused. Returns true when something changed.
    /// </summary>
    public bool NormalizePause()
    {
        var changed = false;
        if (Options is { IndefinitePause: true } options)
        {
            options.IndefinitePause = false;
            if (!Schedule.Paused) Schedule.SetPaused(true, PauseReasons.IndefinitePause);
            else Schedule.PauseReason ??= PauseReasons.IndefinitePause;
            changed = true;
        }

        if (!Schedule.Paused && (Schedule.PauseReason ?? Schedule.PausedBy ?? (object?)Schedule.PausedAtUtc) is not null)
        {
            Schedule.SetPaused(false);
            changed = true;
        }

        return changed;
    }
}

/// <summary>Pause reasons set by RIS (the UI shows them translated). Operators type their own reasons.</summary>
public static class PauseReasons
{
    /// <summary>4.x IndefinitePause (job disabled).</summary>
    public const string IndefinitePause = "IndefinitePause";
    public const string Configuration = "Paused in configuration";
    /// <summary>Before RIS 5.0.0-local.30 an operator pause kept when the job changed; no longer set.</summary>
    public const string KeptAfterChange = "Paused by operator (kept after configuration change)";
    public const string Error = "Paused after a failed run (PauseOnError)";
    public const string Operator = "Paused by operator";
    /// <summary>Idle guard: every run in its window failed.</summary>
    public const string IdleFailing = "Paused by the idle guard (only failed runs)";
    /// <summary>Idle guard: no run in its window did any work.</summary>
    public const string IdleNoWork = "Paused by the idle guard (no work done)";
}

/// <summary>
/// Root of RIS configuration file (jobs.json).
/// </summary>
public sealed class RisConfiguration
{
    public List<AppRegistrationOptions> AppRegistrations { get; set; } = [];

    public List<D365ConnectionOptions> Connections { get; set; } = [];

    public List<SftpServerOptions> SftpServers { get; set; } = [];

    public List<JobDefinition> Jobs { get; set; } = [];

    public D365ConnectionOptions? FindConnection(string name) =>
        Connections.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));

    public AppRegistrationOptions? FindAppRegistration(string? name) => string.IsNullOrEmpty(name)
        ? null
        : AppRegistrations.FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Effective connection of a job: the D365 instance with the sign-in of the job's app registration, otherwise of the
    /// instance's default one (or the instance's inline sign-in of an older configuration).
    /// </summary>
    /// <exception cref="RisException">RIS-CFG-002 when the instance or an app registration is not defined.</exception>
    public D365ConnectionOptions ResolveConnection(string connection, string? appRegistration = null)
    {
        var instance = FindConnection(connection)
                       ?? throw new RisException(ErrorCodes.ConnectionNotFound, $"D365 instance '{connection}' is not defined.");
        var name = string.IsNullOrEmpty(appRegistration) ? instance.AppRegistration : appRegistration;
        if (string.IsNullOrEmpty(name)) return instance;
        var registration = FindAppRegistration(name)
                           ?? throw new RisException(ErrorCodes.ConnectionNotFound, $"App registration '{name}' (used by D365 instance '{connection}') is not defined.");
        return instance.With(registration);
    }

    /// <summary>
    /// Moves the inline sign-in of D365 instances (configurations from before app registrations, RIS 4.x import) to app
    /// registrations; instances with the same sign-in share one. Names: "APP-" and the start of the client id. Returns the
    /// number of instances changed.
    /// </summary>
    public int Normalize()
    {
        var changed = 0;
        foreach (var instance in Connections.Where(c => c.HasInlineSignIn))
        {
            var registration = AppRegistrations.FirstOrDefault(a => SameSignIn(a, instance));
            if (registration is null)
            {
                var baseName = "APP-" + (instance.ClientId.Length > 8 ? instance.ClientId[..8] : instance.ClientId).ToUpperInvariant();
                var name = baseName;
                for (var i = 2; FindAppRegistration(name) is not null; i++) name = $"{baseName}-{i}";
#pragma warning disable CS0618
                registration = new AppRegistrationOptions
                {
                    Name = name, Tenant = instance.Tenant, Authority = instance.Authority, ClientId = instance.ClientId, AuthMode = instance.AuthMode,
                    ClientSecret = instance.ClientSecret, CertificateThumbprint = instance.CertificateThumbprint,
                    UserName = instance.UserName, UserPassword = instance.UserPassword
                };
#pragma warning restore CS0618
                AppRegistrations.Add(registration);
            }

            instance.AppRegistration = registration.Name;
            ClearSignIn(instance);
            changed++;
        }
        return changed;
    }

    /// <summary>Removes the inline sign-in fields of an instance (they are in its app registration).</summary>
    public static void ClearSignIn(D365ConnectionOptions instance)
    {
        instance.Tenant = string.Empty;
        instance.ClientId = string.Empty;
        instance.Authority = new Uri("https://login.microsoftonline.com");
        instance.AuthMode = D365AuthMode.ClientSecret;
        instance.ClientSecret = null;
        instance.CertificateThumbprint = null;
        instance.UserName = null;
        instance.UserPassword = null;
    }

    private static bool SameSignIn(AppRegistrationOptions a, D365ConnectionOptions c) =>
        string.Equals(a.Tenant, c.Tenant, StringComparison.OrdinalIgnoreCase) && a.Authority == c.Authority
        && string.Equals(a.ClientId, c.ClientId, StringComparison.OrdinalIgnoreCase) && a.AuthMode == c.AuthMode
        && SameSecret(a.ClientSecret, c.ClientSecret) && string.Equals(a.CertificateThumbprint, c.CertificateThumbprint, StringComparison.OrdinalIgnoreCase)
        && string.Equals(a.UserName, c.UserName, StringComparison.OrdinalIgnoreCase) && SameSecret(a.UserPassword, c.UserPassword);

    /// <summary>A placeholder (export without secrets, secret not set yet) matches any secret, so re-imports do not split registrations.</summary>
    private static bool SameSecret(string? a, string? b) => a == b || a == SecretPlaceholder.Value || b == SecretPlaceholder.Value;

    public SftpServerOptions? FindSftpServer(string name) =>
        SftpServers.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));

    public JobDefinition? FindJob(string nameOrKey) =>
        Jobs.FirstOrDefault(j => string.Equals(j.Key, nameOrKey, StringComparison.OrdinalIgnoreCase))
        ?? Jobs.FirstOrDefault(j => string.Equals(j.Name, nameOrKey, StringComparison.OrdinalIgnoreCase));
}
