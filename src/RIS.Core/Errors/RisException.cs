namespace RIS.Core.Errors;

/// <summary>
/// Error codes reported by RIS. Each code is described in docs/ris5/error-codes.md.
/// </summary>
public static class ErrorCodes
{
    // Configuration
    public const string ConfigurationInvalid = "RIS-CFG-001";
    public const string ConnectionNotFound = "RIS-CFG-002";
    public const string FolderNotAccessible = "RIS-CFG-003";
    public const string PackageTemplateInvalid = "RIS-CFG-004";

    // Authentication
    public const string AuthenticationFailed = "RIS-AUTH-001";

    // D365 HTTP
    public const string HttpRequestFailed = "RIS-HTTP-001";
    public const string HttpTransient = "RIS-HTTP-002";
    public const string HttpUnexpectedResponse = "RIS-HTTP-003";

    // Export
    public const string ExportTimeout = "RIS-EXP-001";
    public const string ExportFailed = "RIS-EXP-002";
    public const string ExportPackageUrlTimeout = "RIS-EXP-003";
    public const string ExportDownloadFailed = "RIS-EXP-004";

    // Import / Upload
    public const string ImportRequestFailed = "RIS-IMP-001";
    public const string LegalEntityNotResolved = "RIS-IMP-002";
    public const string DataProjectNotFound = "RIS-IMP-003";
    public const string PackageFilesMismatch = "RIS-IMP-004";
    public const string UploadFailed = "RIS-UPL-001";

    // Download
    public const string DequeueFailed = "RIS-DWN-001";
    public const string AckFailed = "RIS-DWN-002";

    // Monitors
    public const string MonitorTimeout = "RIS-MON-001";
    public const string MonitorExecutionFailed = "RIS-MON-002";

    // Files / SFTP
    public const string FileLocked = "RIS-FS-001";
    public const string FileOperationFailed = "RIS-FS-002";
    public const string SftpFailed = "RIS-SFTP-001";

    // Unexpected
    public const string Unexpected = "RIS-GEN-001";
    public const string StatusUnrecognized = "RIS-GEN-002";
}

/// <summary>
/// Expected RIS failure with error code and diagnostic context (execution id, file, ...).
/// </summary>
public class RisException : Exception
{
    public RisException(string errorCode, string message, IReadOnlyDictionary<string, object?>? context = null, Exception? innerException = null)
        : base(message, innerException)
    {
        ErrorCode = errorCode;
        Context = context ?? new Dictionary<string, object?>();
    }

    public string ErrorCode { get; }

    /// <summary>Values helping to diagnose the error, logged as structured properties.</summary>
    public IReadOnlyDictionary<string, object?> Context { get; }

    public override string ToString() => $"[{ErrorCode}] {base.ToString()}";
}
