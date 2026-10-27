using RIS.Core.Configuration;
using RIS.Core.Options;

namespace RIS.Server.Services;

/// <summary>Steps of the job wizard (and tabs of the job editor).</summary>
public enum JobStep
{
    Type,
    D365,
    Files,
    Schedule,
    Advanced,
    Summary
}

/// <summary>
/// Which wizard step shows an option of a job type, and in which order. Options not listed here go to
/// <see cref="JobStep.Advanced"/>, so new settings are always editable.
/// </summary>
public static class JobFieldCatalog
{
    private static readonly Dictionary<string, (JobStep Step, int Order)> Fields = new(StringComparer.Ordinal)
    {
        // D365
        ["Connection"] = (JobStep.D365, 0),
        ["DataProject"] = (JobStep.D365, 1),
        ["ActivityId"] = (JobStep.D365, 2),
        ["EntityName"] = (JobStep.D365, 3),
        ["IsDataPackage"] = (JobStep.D365, 4),
        ["Company"] = (JobStep.D365, 5),
        ["InputFilesArePackages"] = (JobStep.D365, 6),
        ["PackageTemplate"] = (JobStep.D365, 7),
        ["OverwriteDataProject"] = (JobStep.D365, 8),
        ["ExecuteImport"] = (JobStep.D365, 9),
        ["GetExecutionErrors"] = (JobStep.D365, 10),
        ["GetImportTargetErrorKeysFile"] = (JobStep.D365, 11),
        ["MultiCompany"] = (JobStep.D365, 12),

        // Files and folders
        ["InputDir"] = (JobStep.Files, 0),
        ["Files"] = (JobStep.Files, 1),
        ["UploadSuccessDir"] = (JobStep.Files, 2),
        ["UploadErrorsDir"] = (JobStep.Files, 3),
        ["DownloadSuccessDir"] = (JobStep.Files, 4),
        ["DownloadErrorsDir"] = (JobStep.Files, 5),
        ["ProcessingSuccessDir"] = (JobStep.Files, 6),
        ["ProcessingErrorsDir"] = (JobStep.Files, 7),
        ["FileNamePattern"] = (JobStep.Files, 8),
        ["UnzipPackage"] = (JobStep.Files, 9),
        ["AddTimestamp"] = (JobStep.Files, 10),
        ["DeletePackage"] = (JobStep.Files, 11),
        ["WriteStatusFiles"] = (JobStep.Files, 12),
        ["StatusFileExtension"] = (JobStep.Files, 13),
        ["SftpInbound"] = (JobStep.Files, 14),
        ["SftpOutbound"] = (JobStep.Files, 15),

        // Advanced
        ["Polling"] = (JobStep.Advanced, 0),
        ["Retry"] = (JobStep.Advanced, 1),
        ["DelayBetweenFiles"] = (JobStep.Advanced, 2),
        ["ErrorKeysFileTimeout"] = (JobStep.Advanced, 3),
        ["PauseOnError"] = (JobStep.Advanced, 4),
        ["IndefinitePause"] = (JobStep.Advanced, 5),
        ["ActionPaths"] = (JobStep.Advanced, 6)
    };

    public static JobStep StepOf(string property) => Fields.TryGetValue(property, out var field) ? field.Step : JobStep.Advanced;

    public static int OrderOf(string property) => Fields.TryGetValue(property, out var field) ? field.Order : 100;

    /// <summary>Settable options of the type shown in the step, in display order.</summary>
    public static IReadOnlyList<string> PropertiesOf(Type optionsType, JobStep step) =>
        optionsType.GetProperties()
            .Where(p => p.CanWrite && p.SetMethod!.IsPublic && StepOf(p.Name) == step)
            .OrderBy(p => OrderOf(p.Name))
            .Select(p => p.Name)
            .ToList();

    public static JobOptionsBase NewOptions(JobType type) => type switch
    {
        JobType.Import => new ImportJobOptions(),
        JobType.Upload => new UploadJobOptions(),
        JobType.Download => new DownloadJobOptions(),
        JobType.ExecutionMonitor => new ExecutionMonitorJobOptions(),
        JobType.ProcessingMonitor => new ProcessingMonitorJobOptions(),
        _ => new ExportJobOptions()
    };

    /// <summary>Validation messages of the job that belong to a step (by the property path in the message).</summary>
    public static IReadOnlyList<string> ProblemsOf(IEnumerable<string> problems, Type optionsType, JobStep step)
    {
        var names = PropertiesOf(optionsType, step);
        return problems.Where(p => step switch
        {
            JobStep.Type => p.Contains(".Name", StringComparison.Ordinal) || p.Contains(".Group", StringComparison.Ordinal),
            JobStep.Schedule => p.Contains("Schedule", StringComparison.Ordinal),
            _ => names.Any(n => p.Contains("." + n, StringComparison.Ordinal))
        }).ToList();
    }
}
