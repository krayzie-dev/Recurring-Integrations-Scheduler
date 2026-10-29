using System.ComponentModel.DataAnnotations;

namespace RIS.Core.Configuration;

/// <summary>
/// Idle guard: once a day pauses jobs that ran often in the last <see cref="WindowDays"/> days but only failed
/// (<see cref="PauseFailing"/>) or never did any work (<see cref="PauseIdle"/>). Off by default: useful on test servers;
/// on production a job may wait a month for its file. Stored in ris.db (Settings), changed on the Maintenance page or with
/// "ris idle-guard".
/// </summary>
public sealed class IdleGuardSettings : IValidatableObject
{
    public const string Key = "IdleGuard";

    /// <summary>Pause jobs whose every run in the window failed.</summary>
    public bool PauseFailing { get; set; }

    /// <summary>Pause jobs that did no work in the window (only "Skipped", e.g. no files, and failures).</summary>
    public bool PauseIdle { get; set; }

    /// <summary>Days without a successful run.</summary>
    public int WindowDays { get; set; } = 7;

    /// <summary>
    /// Only jobs with at least this many planned runs in the window are checked, so the window is many times their rhythm:
    /// every 5 min = 2016 runs in 7 days (checked), daily = 7 and weekly = 1 (never paused).
    /// </summary>
    public int MinPlannedRuns { get; set; } = 20;

    /// <summary>Daily check time (server time zone): before the working day, so people see the paused jobs in the morning.</summary>
    public TimeOnly CheckAt { get; set; } = new(6, 0);

    public bool Enabled => PauseFailing || PauseIdle;

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (WindowDays is < 1 or > 90) yield return new ValidationResult("WindowDays must be 1 to 90.", [nameof(WindowDays)]);
        if (MinPlannedRuns is < 2 or > 100_000) yield return new ValidationResult("MinPlannedRuns must be 2 to 100000.", [nameof(MinPlannedRuns)]);
    }
}
