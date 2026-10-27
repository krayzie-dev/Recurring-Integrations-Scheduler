namespace RIS.Core.Statuses;

/// <summary>
/// Status of a Data Management (Package API) execution returned by GetExecutionSummaryStatus.
/// </summary>
public enum DmfExecutionStatus
{
    /// <summary>Value not known to RIS (new D365 value or malformed response).</summary>
    Unrecognized = 0,
    NotRun,
    Executing,
    Succeeded,
    PartiallySucceeded,
    Failed,
    Canceled,
    Unknown
}

public static class DmfExecutionStatusExtensions
{
    /// <summary>
    /// Parses D365 status text. Unknown values map to <see cref="DmfExecutionStatus.Unrecognized"/> instead of throwing.
    /// </summary>
    public static DmfExecutionStatus Parse(string? value)
    {
        var text = value?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return DmfExecutionStatus.Unrecognized;
        }

        // 4.x also waited on "Bad request" (sic) - D365 returns it shortly after ExportToPackage.
        if (text.Equals("Bad request", StringComparison.OrdinalIgnoreCase))
        {
            return DmfExecutionStatus.NotRun;
        }

        return Enum.TryParse<DmfExecutionStatus>(text, ignoreCase: true, out var status) && status != DmfExecutionStatus.Unrecognized
            ? status
            : DmfExecutionStatus.Unrecognized;
    }

    /// <summary>Execution has not finished yet.</summary>
    public static bool IsInProgress(this DmfExecutionStatus status) =>
        status is DmfExecutionStatus.NotRun or DmfExecutionStatus.Executing;

    /// <summary>Execution finished successfully. PartiallySucceeded counts as success only when requested.</summary>
    public static bool IsSuccess(this DmfExecutionStatus status, bool partialIsSuccess) =>
        status == DmfExecutionStatus.Succeeded || (partialIsSuccess && status == DmfExecutionStatus.PartiallySucceeded);
}
