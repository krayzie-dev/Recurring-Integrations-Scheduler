using System.ComponentModel.DataAnnotations;

namespace RIS.Core.Options;

/// <summary>
/// SFTP server with credentials. Defined once in configuration and referenced by name from jobs,
/// so secrets never become part of job data (which Quartz stores and records in execution history).
/// </summary>
public sealed class SftpServerOptions : IValidatableObject
{
    [Required]
    public string Name { get; set; } = string.Empty;

    [Required]
    public string Host { get; set; } = string.Empty;

    [Range(1, 65535)]
    public int Port { get; set; } = 22;

    [Required]
    public string UserName { get; set; } = string.Empty;

    /// <summary>Password (when not using key). Never logged.</summary>
    public string? Password { get; set; }

    public bool UseKey { get; set; }

    public string? KeyPath { get; set; }

    /// <summary>Private key passphrase. Never logged.</summary>
    public string? KeyPassphrase { get; set; }

    public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(30);

    public TimeSpan OperationTimeout { get; set; } = TimeSpan.FromMinutes(10);

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (UseKey && string.IsNullOrEmpty(KeyPath))
        {
            yield return new ValidationResult("KeyPath is required when UseKey is set.", [nameof(KeyPath)]);
        }

        if (!UseKey && string.IsNullOrEmpty(Password))
        {
            yield return new ValidationResult("Password is required when key authentication is not used.", [nameof(Password)]);
        }
    }

    public override string ToString() => $"sftp://{UserName}@{Host}:{Port}";
}

/// <summary>
/// SFTP transfer of a job: which server (by name), remote folder and file mask.
/// 4.x: UseSftpInbound/UseSftpOutbound + Sftp*RemoteFolder, Sftp*FileMask, Sftp*ServerName.
/// </summary>
public sealed class SftpTransferOptions
{
    /// <summary>Name of <see cref="SftpServerOptions"/>.</summary>
    [Required]
    public string Server { get; set; } = string.Empty;

    [Required]
    public string RemoteFolder { get; set; } = "/";

    public string FileMask { get; set; } = "*.*";
}
