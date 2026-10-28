using Microsoft.Extensions.Localization;
using RIS.Jobs.Quartz;
using RIS.Server.Resources;
using RIS.Server.Services;

namespace RIS.Server.Ui;

/// <summary>Pause reasons in the UI language: reasons set by RIS are translated, reasons typed by operators are shown as typed.</summary>
public static class Pausing
{
    private static readonly Dictionary<string, string> Keys = new(StringComparer.Ordinal)
    {
        [PauseReasons.IndefinitePause] = "PauseReason_IndefinitePause",
        [PauseReasons.Configuration] = "PauseReason_Configuration",
        [PauseReasons.KeptAfterChange] = "PauseReason_KeptAfterChange",
        [PauseReasons.Error] = "PauseReason_Error"
    };

    public static string? Reason(string? reason, IStringLocalizer<UiText> text) =>
        reason is null ? null : Keys.TryGetValue(reason, out var key) ? text[key] : reason;

    /// <summary>Reason and who paused the job, e.g. "Paused in the configuration (RIS)".</summary>
    public static string? Text(JobSummary job, IStringLocalizer<UiText> text) =>
        Reason(job.PauseReason, text) is not { } reason ? null
        : string.IsNullOrEmpty(job.PausedBy) ? reason : $"{reason} ({job.PausedBy})";
}
