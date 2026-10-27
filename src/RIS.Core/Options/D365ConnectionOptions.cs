using System.ComponentModel.DataAnnotations;

namespace RIS.Core.Options;

/// <summary>
/// How RIS authenticates to Microsoft Entra ID when calling D365.
/// </summary>
public enum D365AuthMode
{
    /// <summary>Application (client credentials) with client secret.</summary>
    ClientSecret,

    /// <summary>Application (client credentials) with certificate from Windows certificate store.</summary>
    Certificate,

    /// <summary>User name and password (ROPC). Kept for 4.x compatibility, does not work with MFA.</summary>
    [Obsolete("Username/password flow does not support MFA. Use ClientSecret or Certificate.")]
    UsernamePassword
}

/// <summary>
/// D365 environment and credentials used by a job. Defined once and referenced by many jobs.
/// </summary>
public sealed class D365ConnectionOptions : IValidatableObject
{
    /// <summary>Unique name of the connection (e.g. "PROD").</summary>
    [Required]
    public string Name { get; set; } = string.Empty;

    /// <summary>D365 environment URL, e.g. https://contoso.operations.dynamics.com.</summary>
    [Required]
    public Uri? AosUri { get; set; }

    /// <summary>Entra ID tenant (domain or id).</summary>
    [Required]
    public string Tenant { get; set; } = string.Empty;

    /// <summary>Entra ID authority host. 4.x: AzureAuthEndpoint.</summary>
    public Uri Authority { get; set; } = new("https://login.microsoftonline.com");

    /// <summary>Entra ID application (client) id.</summary>
    [Required]
    public string ClientId { get; set; } = string.Empty;

    public D365AuthMode AuthMode { get; set; } = D365AuthMode.ClientSecret;

    /// <summary>Client secret (ClientSecret mode). Never logged.</summary>
    public string? ClientSecret { get; set; }

    /// <summary>Certificate thumbprint (Certificate mode), looked up in LocalMachine\My then CurrentUser\My.</summary>
    public string? CertificateThumbprint { get; set; }

    /// <summary>User name (UsernamePassword mode).</summary>
    public string? UserName { get; set; }

    /// <summary>User password (UsernamePassword mode). Never logged.</summary>
    public string? UserPassword { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (!Guid.TryParse(ClientId, out _))
        {
            yield return new ValidationResult("ClientId must be a GUID.", [nameof(ClientId)]);
        }

        if (AosUri is { IsAbsoluteUri: false } || AosUri?.Scheme is not (null or "https"))
        {
            yield return new ValidationResult("AosUri must be an absolute https URL.", [nameof(AosUri)]);
        }

#pragma warning disable CS0618 // UsernamePassword is obsolete but still supported
        switch (AuthMode)
        {
            case D365AuthMode.ClientSecret when string.IsNullOrEmpty(ClientSecret):
                yield return new ValidationResult("ClientSecret is required for ClientSecret authentication.", [nameof(ClientSecret)]);
                break;
            case D365AuthMode.Certificate when string.IsNullOrEmpty(CertificateThumbprint):
                yield return new ValidationResult("CertificateThumbprint is required for Certificate authentication.", [nameof(CertificateThumbprint)]);
                break;
            case D365AuthMode.UsernamePassword when string.IsNullOrEmpty(UserName) || string.IsNullOrEmpty(UserPassword):
                yield return new ValidationResult("UserName and UserPassword are required for UsernamePassword authentication.", [nameof(UserName), nameof(UserPassword)]);
                break;
        }
#pragma warning restore CS0618
    }
}
