using Microsoft.Extensions.Logging;
using RIS.Core.Configuration;
using RIS.Core.Options;

namespace RIS.Jobs;

/// <summary>
/// Source of the current RIS configuration (connections, SFTP servers, jobs).
/// Stage 2: JSON file; stage 3: database.
/// </summary>
public interface IRisConfigurationStore
{
    RisConfiguration Current { get; }
}

public sealed class StaticConfigurationStore(RisConfiguration configuration) : IRisConfigurationStore
{
    public RisConfiguration Current { get; } = configuration;
}

/// <summary>
/// Everything a job handler needs to know about the current run.
/// </summary>
public sealed class JobRunContext
{
    public required string JobKey { get; init; }

    public required JobType JobType { get; init; }

    /// <summary>Quartz fire instance id (same value as in Quartz execution history).</summary>
    public required string RunId { get; init; }

    public required D365ConnectionOptions Connection { get; init; }

    public required RisConfiguration Configuration { get; init; }

    public required ILogger Logger { get; init; }

    /// <summary>Progress reported to Quartz (dashboard).</summary>
    public Action<int, string>? Progress { get; init; }

    public SftpServerOptions GetSftpServer(string name) =>
        Configuration.FindSftpServer(name)
        ?? throw new Core.Errors.RisException(Core.Errors.ErrorCodes.ConfigurationInvalid, $"SFTP server '{name}' is not defined.");

    public void ReportProgress(int percent, string message) => Progress?.Invoke(Math.Clamp(percent, 0, 100), message);
}

public enum JobOutcomeStatus
{
    Succeeded,

    /// <summary>Run finished but some items failed (e.g. 1 of 5 files). Reported to Quartz as failed so it is visible.</summary>
    Failed,

    /// <summary>Nothing to do or job disabled.</summary>
    Skipped
}

/// <summary>
/// Result of a run: summary line and metrics shown in Quartz execution history and logged at the end of the run.
/// </summary>
public sealed class JobOutcome
{
    public required JobOutcomeStatus Status { get; init; }

    public required string Summary { get; init; }

    public string? ErrorCode { get; init; }

    public Dictionary<string, object> Metrics { get; init; } = [];

    public static JobOutcome Succeeded(string summary) => new() { Status = JobOutcomeStatus.Succeeded, Summary = summary };

    public static JobOutcome Skipped(string summary) => new() { Status = JobOutcomeStatus.Skipped, Summary = summary };

    public static JobOutcome Failed(string errorCode, string summary) => new() { Status = JobOutcomeStatus.Failed, Summary = summary, ErrorCode = errorCode };

    public JobOutcome With(string key, object? value)
    {
        if (value is not null)
        {
            Metrics[key] = value;
        }
        return this;
    }
}

/// <summary>
/// Counts processed items of a run (files, messages).
/// </summary>
public sealed class ItemCounters
{
    public int Succeeded { get; set; }
    public int Failed { get; set; }
    public int Skipped { get; set; }
    public int Pending { get; set; }
    public string? FirstErrorCode { get; set; }

    public void Fail(string errorCode)
    {
        Failed++;
        FirstErrorCode ??= errorCode;
    }

    public JobOutcome ToOutcome(string what)
    {
        var summary = $"{what}: {Succeeded} succeeded, {Failed} failed" +
                      (Skipped > 0 ? $", {Skipped} skipped" : string.Empty) +
                      (Pending > 0 ? $", {Pending} still in progress" : string.Empty);
        var outcome = Failed > 0
            ? JobOutcome.Failed(FirstErrorCode ?? Core.Errors.ErrorCodes.Unexpected, summary)
            : Succeeded + Pending + Skipped == 0 ? JobOutcome.Skipped($"{what}: nothing to process") : JobOutcome.Succeeded(summary);
        return outcome.With("Succeeded", Succeeded).With("Failed", Failed).With("Skipped", Skipped).With("Pending", Pending);
    }
}

/// <summary>
/// Executes one job type.
/// </summary>
public interface IJobHandler<in TOptions> where TOptions : JobOptionsBase
{
    Task<JobOutcome> RunAsync(JobRunContext run, TOptions options, CancellationToken cancellationToken);
}
