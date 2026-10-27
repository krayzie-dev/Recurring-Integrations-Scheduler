using System.ComponentModel.DataAnnotations;

namespace RIS.Core.Options;

/// <summary>
/// SFTP endpoint used to download input files (inbound) or upload results (outbound).
/// </summary>
public sealed class SftpOptions : IValidatableObject
{
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

    [Required]
    public string RemoteFolder { get; set; } = "/";

    public string FileMask { get; set; } = "*.*";

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

    public override string ToString() => $"sftp://{UserName}@{Host}:{Port}{RemoteFolder}";
}
