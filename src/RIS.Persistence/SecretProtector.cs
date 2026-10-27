using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using RIS.Core.Errors;

namespace RIS.Persistence;

/// <summary>
/// Encrypts secrets stored in the database.
/// </summary>
public interface ISecretProtector
{
    string? Protect(string? secret);

    string? Unprotect(string? protectedSecret, string what);
}

/// <summary>
/// ASP.NET Core Data Protection. Keys live in the data directory and are protected with DPAPI (machine)
/// or with a certificate (allows moving the database to another server).
/// </summary>
public sealed class DataProtectionSecretProtector(IDataProtectionProvider provider) : ISecretProtector
{
    private readonly IDataProtector _protector = provider.CreateProtector("RIS5.Secrets.v1");

    public string? Protect(string? secret) => string.IsNullOrEmpty(secret) ? null : _protector.Protect(secret);

    public string? Unprotect(string? protectedSecret, string what)
    {
        if (string.IsNullOrEmpty(protectedSecret))
        {
            return null;
        }

        try
        {
            return _protector.Unprotect(protectedSecret);
        }
        catch (CryptographicException ex)
        {
            throw new RisException(ErrorCodes.ConfigurationInvalid,
                $"Secret {what} cannot be decrypted. Data protection keys are missing or belong to another server; set the secret again (ris db set-secret).",
                new Dictionary<string, object?> { ["Secret"] = what }, ex);
        }
    }
}
