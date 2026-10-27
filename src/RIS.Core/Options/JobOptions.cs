using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace RIS.Core.Options;

/// <summary>
/// Order in which input files are processed. 4.x: OrderBy.
/// </summary>
public enum FileOrder
{
    Created,
    Modified,
    FileName,
    Size
}

/// <summary>
/// Retry of a single HTTP request or file operation. 4.x: RetryCount / RetryDelay.
/// </summary>
public sealed class RetryOptions
{
    [Range(0, 100)]
    public int Count { get; set; } = 3;

    public TimeSpan Delay { get; set; } = TimeSpan.FromSeconds(10);
}

/// <summary>
/// Which files from the input folder are taken and in which order.
/// </summary>
public sealed class FileSelectionOptions
{
    /// <summary>4.x: SearchPattern.</summary>
    public string SearchPattern { get; set; } = "*.*";

    /// <summary>4.x: OrderBy.</summary>
    public FileOrder OrderBy { get; set; } = FileOrder.Created;

    /// <summary>4.x: ReverseOrder.</summary>
    public bool Reverse { get; set; }
}

/// <summary>
/// Settings shared by all job types. JSON property "type" selects the concrete options.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(ExportJobOptions), "Export")]
[JsonDerivedType(typeof(ImportJobOptions), "Import")]
[JsonDerivedType(typeof(UploadJobOptions), "Upload")]
[JsonDerivedType(typeof(DownloadJobOptions), "Download")]
[JsonDerivedType(typeof(ExecutionMonitorJobOptions), "ExecutionMonitor")]
[JsonDerivedType(typeof(ProcessingMonitorJobOptions), "ProcessingMonitor")]
public abstract class JobOptionsBase : IValidatableObject
{
    /// <summary>Name of <see cref="D365ConnectionOptions"/> used by the job.</summary>
    [Required]
    public string Connection { get; set; } = string.Empty;

    /// <summary>Retry of single HTTP requests and file operations.</summary>
    public RetryOptions Retry { get; set; } = new();

    /// <summary>Delay between processing consecutive files/messages. 4.x: DelayBetweenFiles.</summary>
    public TimeSpan DelayBetweenFiles { get; set; } = TimeSpan.Zero;

    /// <summary>Pause the job after a failed run. 4.x: PauseJobOnException.</summary>
    public bool PauseOnError { get; set; }

    /// <summary>Job is configured but must never run. 4.x: IndefinitePause.</summary>
    public bool IndefinitePause { get; set; }

    /// <summary>Overrides of D365 OData action paths. 4.x: *ActionPath keys.</summary>
    public D365ActionPaths ActionPaths { get; set; } = new();

    public virtual IEnumerable<ValidationResult> Validate(ValidationContext validationContext) => [];

    protected static IEnumerable<ValidationResult> RequireFolder(string? folder, string memberName)
    {
        if (string.IsNullOrWhiteSpace(folder))
        {
            yield return new ValidationResult($"{memberName} is required.", [memberName]);
        }
        else if (!Path.IsPathFullyQualified(folder))
        {
            yield return new ValidationResult($"{memberName} must be an absolute path: '{folder}'.", [memberName]);
        }
    }

    protected static IEnumerable<ValidationResult> ValidatePattern(string? pattern, IReadOnlyCollection<string> allowed)
    {
        if (string.IsNullOrWhiteSpace(pattern))
        {
            yield return new ValidationResult("FileNamePattern is required.", ["FileNamePattern"]);
            yield break;
        }

        var unknown = Options.FileNamePattern.UnknownTokens(pattern, allowed);
        if (unknown.Count > 0)
        {
            yield return new ValidationResult(
                $"FileNamePattern contains unknown token(s) {string.Join(", ", unknown.Select(t => "{" + t + "}"))}. Allowed: {string.Join(" ", allowed.Select(t => "{" + t + "}"))}.",
                ["FileNamePattern"]);
        }
    }

    protected static IEnumerable<ValidationResult> RequireStatusExtension(string? extension, string memberName)
    {
        if (string.IsNullOrWhiteSpace(extension) || !extension.StartsWith('.'))
        {
            yield return new ValidationResult($"{memberName} must start with '.', e.g. '.Status'.", [memberName]);
        }
    }
}

/// <summary>
/// Export of a D365 data project to a package (Package API). 4.x: Job.Export.
/// </summary>
public sealed class ExportJobOptions : JobOptionsBase
{
    [Required]
    public string DataProject { get; set; } = string.Empty;

    [Required]
    public string Company { get; set; } = string.Empty;

    /// <summary>Folder where exported packages are saved. 4.x: DownloadSuccessDir.</summary>
    public string DownloadSuccessDir { get; set; } = string.Empty;

    /// <summary>4.x: DownloadErrorsDir (not used by 4.x Export, kept for parity).</summary>
    public string? DownloadErrorsDir { get; set; }

    public bool UnzipPackage { get; set; }
    public bool AddTimestamp { get; set; }
    public bool DeletePackage { get; set; }

    /// <summary>Package file name, tokens: {timestamp} {date} {executionId} {dataProject} {company}. Default as in 4.x.</summary>
    public string FileNamePattern { get; set; } = Options.FileNamePattern.ExportDefault;

    /// <summary>Waiting for export to finish and for package URL.</summary>
    public PollingOptions Polling { get; set; } = new();

    /// <summary>Upload exported package to SFTP. 4.x: UseSftpOutbound + SftpOutbound*.</summary>
    public SftpTransferOptions? SftpOutbound { get; set; }

    public override IEnumerable<ValidationResult> Validate(ValidationContext validationContext) =>
        RequireFolder(DownloadSuccessDir, nameof(DownloadSuccessDir))
            .Concat(ValidatePattern(FileNamePattern, Options.FileNamePattern.ExportTokens));
}

/// <summary>
/// Import of files/packages using Package API (ImportFromPackage). 4.x: Job.Import.
/// </summary>
public sealed class ImportJobOptions : JobOptionsBase
{
    [Required]
    public string DataProject { get; set; } = string.Empty;

    /// <summary>Target legal entity. Not required for multi-company import.</summary>
    public string? Company { get; set; }

    public string InputDir { get; set; } = string.Empty;
    public string UploadSuccessDir { get; set; } = string.Empty;
    public string UploadErrorsDir { get; set; } = string.Empty;

    public FileSelectionOptions Files { get; set; } = new();

    /// <summary>Input files are already data packages (zip). Otherwise each file is wrapped in <see cref="PackageTemplate"/>.</summary>
    public bool InputFilesArePackages { get; set; }

    /// <summary>Package template (zip with Manifest.xml) used to wrap single files.</summary>
    public string? PackageTemplate { get; set; }

    public bool OverwriteDataProject { get; set; }
    public bool ExecuteImport { get; set; } = true;

    /// <summary>Legal entity taken from input file location or name.</summary>
    public MultiCompanyOptions? MultiCompany { get; set; }

    /// <summary>Write status files for <see cref="ExecutionMonitorJobOptions"/>. 4.x: ExecutionJobPresent.</summary>
    public bool WriteStatusFiles { get; set; } = true;

    public string StatusFileExtension { get; set; } = ".Status";

    /// <summary>Download input files from SFTP first. 4.x: UseSftpInbound + SftpInbound*.</summary>
    public SftpTransferOptions? SftpInbound { get; set; }

    public override IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        foreach (var r in RequireFolder(InputDir, nameof(InputDir))) yield return r;
        foreach (var r in RequireFolder(UploadSuccessDir, nameof(UploadSuccessDir))) yield return r;
        foreach (var r in RequireFolder(UploadErrorsDir, nameof(UploadErrorsDir))) yield return r;
        foreach (var r in RequireStatusExtension(StatusFileExtension, nameof(StatusFileExtension))) yield return r;

        if (!InputFilesArePackages && string.IsNullOrWhiteSpace(PackageTemplate))
        {
            yield return new ValidationResult("PackageTemplate is required when input files are not packages.", [nameof(PackageTemplate)]);
        }

        if (MultiCompany is null && string.IsNullOrWhiteSpace(Company))
        {
            yield return new ValidationResult("Company is required unless multi-company import is configured.", [nameof(Company)]);
        }

        if (MultiCompany is { Source: LegalEntitySource.FileName } mc && (string.IsNullOrEmpty(mc.FileNameSeparator) || mc.TokenPosition < 1))
        {
            yield return new ValidationResult("FileNameSeparator and TokenPosition (>= 1) are required to read legal entity from file name.", [nameof(MultiCompany)]);
        }
    }
}

public enum LegalEntitySource
{
    /// <summary>Name of the folder containing the file. 4.x: GetLegalEntityFromSubfolder.</summary>
    Subfolder,

    /// <summary>Token of the file name. 4.x: GetLegalEntityFromFilename.</summary>
    FileName
}

/// <summary>
/// Multi-company import settings. 4.x: MultiCompanyImport and related keys.
/// </summary>
public sealed class MultiCompanyOptions
{
    public LegalEntitySource Source { get; set; } = LegalEntitySource.Subfolder;

    /// <summary>4.x: FilenameSeparator.</summary>
    public string? FileNameSeparator { get; set; }

    /// <summary>1-based token position. 4.x: LegalEntityTokenPosition.</summary>
    public int TokenPosition { get; set; } = 1;
}

/// <summary>
/// Upload of files to Recurring Integrations enqueue endpoint. 4.x: Job.Upload.
/// </summary>
public sealed class UploadJobOptions : JobOptionsBase
{
    /// <summary>Recurring data job (activity) id.</summary>
    public Guid ActivityId { get; set; }

    /// <summary>Target entity name (when files are not data packages).</summary>
    public string? EntityName { get; set; }

    public bool IsDataPackage { get; set; }

    public string? Company { get; set; }

    public string InputDir { get; set; } = string.Empty;
    public string UploadSuccessDir { get; set; } = string.Empty;
    public string UploadErrorsDir { get; set; } = string.Empty;

    public FileSelectionOptions Files { get; set; } = new();

    /// <summary>Write status files for <see cref="ProcessingMonitorJobOptions"/>. 4.x: ProcessingJobPresent.</summary>
    public bool WriteStatusFiles { get; set; } = true;

    public string StatusFileExtension { get; set; } = ".Status";

    public SftpTransferOptions? SftpInbound { get; set; }

    public override IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (ActivityId == Guid.Empty) yield return new ValidationResult("ActivityId is required.", [nameof(ActivityId)]);
        if (!IsDataPackage && string.IsNullOrWhiteSpace(EntityName))
        {
            yield return new ValidationResult("EntityName is required when files are not data packages.", [nameof(EntityName)]);
        }
        foreach (var r in RequireFolder(InputDir, nameof(InputDir))) yield return r;
        foreach (var r in RequireFolder(UploadSuccessDir, nameof(UploadSuccessDir))) yield return r;
        foreach (var r in RequireFolder(UploadErrorsDir, nameof(UploadErrorsDir))) yield return r;
        foreach (var r in RequireStatusExtension(StatusFileExtension, nameof(StatusFileExtension))) yield return r;
    }
}

/// <summary>
/// Download of exported messages from Recurring Integrations dequeue endpoint. 4.x: Job.Download.
/// </summary>
public sealed class DownloadJobOptions : JobOptionsBase
{
    public Guid ActivityId { get; set; }

    public string DownloadSuccessDir { get; set; } = string.Empty;
    public string DownloadErrorsDir { get; set; } = string.Empty;

    public bool UnzipPackage { get; set; }
    public bool AddTimestamp { get; set; }
    public bool DeletePackage { get; set; }

    /// <summary>Package file name, tokens: {timestamp} {date} {number} {correlationId} {activityId}. Default as in 4.x.</summary>
    public string FileNamePattern { get; set; } = Options.FileNamePattern.DownloadDefault;

    public SftpTransferOptions? SftpOutbound { get; set; }

    public override IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (ActivityId == Guid.Empty) yield return new ValidationResult("ActivityId is required.", [nameof(ActivityId)]);
        foreach (var r in RequireFolder(DownloadSuccessDir, nameof(DownloadSuccessDir))) yield return r;
        foreach (var r in RequireFolder(DownloadErrorsDir, nameof(DownloadErrorsDir))) yield return r;
        foreach (var r in ValidatePattern(FileNamePattern, Options.FileNamePattern.DownloadTokens)) yield return r;
    }
}

/// <summary>
/// Monitoring of Package API imports by execution id. 4.x: Job.ExecutionMonitor.
/// </summary>
public sealed class ExecutionMonitorJobOptions : JobOptionsBase
{
    public string UploadSuccessDir { get; set; } = string.Empty;
    public string ProcessingSuccessDir { get; set; } = string.Empty;
    public string ProcessingErrorsDir { get; set; } = string.Empty;

    public string StatusFileExtension { get; set; } = ".Status";

    /// <summary>Download error keys file per entity for failed imports.</summary>
    public bool GetImportTargetErrorKeysFile { get; set; }

    /// <summary>Package template used to read entity names (when files were wrapped).</summary>
    public string? PackageTemplate { get; set; }

    public bool GetExecutionErrors { get; set; }

    /// <summary>Unfinished import older than <see cref="PollingOptions.Timeout"/> is moved to errors.</summary>
    public PollingOptions Polling { get; set; } = new();

    public TimeSpan ErrorKeysFileTimeout { get; set; } = TimeSpan.FromMinutes(30);

    public override IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        foreach (var r in RequireFolder(UploadSuccessDir, nameof(UploadSuccessDir))) yield return r;
        foreach (var r in RequireFolder(ProcessingSuccessDir, nameof(ProcessingSuccessDir))) yield return r;
        foreach (var r in RequireFolder(ProcessingErrorsDir, nameof(ProcessingErrorsDir))) yield return r;
        foreach (var r in RequireStatusExtension(StatusFileExtension, nameof(StatusFileExtension))) yield return r;
    }
}

/// <summary>
/// Monitoring of Recurring Integrations uploads by message id. 4.x: Job.ProcessingMonitor.
/// </summary>
public sealed class ProcessingMonitorJobOptions : JobOptionsBase
{
    public Guid ActivityId { get; set; }

    public string UploadSuccessDir { get; set; } = string.Empty;
    public string ProcessingSuccessDir { get; set; } = string.Empty;
    public string ProcessingErrorsDir { get; set; } = string.Empty;

    public string StatusFileExtension { get; set; } = ".Status";

    public bool GetExecutionErrors { get; set; }

    /// <summary>Unfinished processing older than <see cref="PollingOptions.Timeout"/> is moved to errors.</summary>
    public PollingOptions Polling { get; set; } = new();

    public override IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (ActivityId == Guid.Empty) yield return new ValidationResult("ActivityId is required.", [nameof(ActivityId)]);
        foreach (var r in RequireFolder(UploadSuccessDir, nameof(UploadSuccessDir))) yield return r;
        foreach (var r in RequireFolder(ProcessingSuccessDir, nameof(ProcessingSuccessDir))) yield return r;
        foreach (var r in RequireFolder(ProcessingErrorsDir, nameof(ProcessingErrorsDir))) yield return r;
        foreach (var r in RequireStatusExtension(StatusFileExtension, nameof(StatusFileExtension))) yield return r;
    }
}
