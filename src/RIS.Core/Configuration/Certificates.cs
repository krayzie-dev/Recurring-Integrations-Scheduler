using System.Security.Cryptography.X509Certificates;
using RIS.Core.Errors;

namespace RIS.Core.Configuration;

/// <summary>Certificates from the Windows certificate store (secret keys, HTTPS, D365 certificate authentication).</summary>
public static class Certificates
{
    /// <summary>
    /// Finds a certificate with a private key by thumbprint (spaces ignored) in the "My" store of the given locations
    /// (default LocalMachine). Throws <see cref="RisException"/> with <see cref="ErrorCodes.ConfigurationInvalid"/> when it is missing.
    /// </summary>
    public static X509Certificate2 FindByThumbprint(string thumbprint, params StoreLocation[] locations)
    {
        var normalized = thumbprint.Replace(" ", string.Empty).Replace("‎", string.Empty).Trim();
        if (locations.Length == 0) locations = [StoreLocation.LocalMachine];
        foreach (var location in locations)
        {
            using var store = new X509Store(StoreName.My, location);
            store.Open(OpenFlags.ReadOnly);
            var found = store.Certificates.Find(X509FindType.FindByThumbprint, normalized, validOnly: false);
            if (found.Count == 0) continue;

            var certificate = found[0];
            if (!certificate.HasPrivateKey)
            {
                throw new RisException(ErrorCodes.ConfigurationInvalid,
                    $"Certificate {normalized} in {location}\\My has no private key (or the service account cannot read it).");
            }
            return certificate;
        }

        throw new RisException(ErrorCodes.ConfigurationInvalid,
            $"Certificate with thumbprint {normalized} was not found in {string.Join(" or ", locations.Select(l => l + "\\My"))}.");
    }
}
