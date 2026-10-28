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
/// Microsoft Entra ID app registration RIS signs in with: tenant, client id and secret, certificate or user. Defined once and
/// used by D365 instances (as their default) and by jobs (as an override), so a renewed secret is set in one place.
/// 4.x: "Azure App registrations" of the client.
/// </summary>
public sealed class AppRegistrationOptions : IValidatableObject
{
    /// <summary>Unique name, e.g. "RIS-PROD".</summary>
    [Required]
    public string Name { get; set; } = string.Empty;

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

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext) =>
        Credentials.Validate(ClientId, AuthMode, ClientSecret, CertificateThumbprint, UserName, UserPassword);
}

/// <summary>
/// D365 environment (instance) used by jobs, with its default app registration. Defined once and referenced by many jobs.
/// At run time it is also the effective connection: <see cref="Configuration.RisConfiguration.ResolveConnection"/> copies the
/// sign-in of the app registration (of the job or the default one) into the fields below, which the D365 client uses.
/// Configurations from before app registrations (and RIS 4.x) have the sign-in inline; they are moved to app registrations by
/// <see cref="Configuration.RisConfiguration.Normalize"/>.
/// </summary>
public sealed class D365ConnectionOptions : IValidatableObject
{
    /// <summary>Unique name of the instance (e.g. "PROD").</summary>
    [Required]
    public string Name { get; set; } = string.Empty;

    /// <summary>D365 environment URL, e.g. https://contoso.operations.dynamics.com.</summary>
    [Required]
    public Uri? AosUri { get; set; }

    /// <summary>Name of the default <see cref="AppRegistrationOptions"/> of the instance (jobs may use another one).</summary>
    public string? AppRegistration { get; set; }

    /// <summary>Entra ID tenant (domain or id). Inline sign-in, or resolved from the app registration.</summary>
    public string Tenant { get; set; } = string.Empty;

    /// <summary>Entra ID authority host. 4.x: AzureAuthEndpoint.</summary>
    public Uri Authority { get; set; } = new("https://login.microsoftonline.com");

    /// <summary>Entra ID application (client) id. Inline sign-in, or resolved from the app registration.</summary>
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

    /// <summary>True when the sign-in is in the instance itself (no app registration referenced).</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool HasInlineSignIn => string.IsNullOrEmpty(AppRegistration) && !string.IsNullOrEmpty(ClientId);

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (AosUri is { IsAbsoluteUri: false } || AosUri?.Scheme is not (null or "https"))
        {
            yield return new ValidationResult("AosUri must be an absolute https URL.", [nameof(AosUri)]);
        }

        if (!string.IsNullOrEmpty(AppRegistration))
        {
            yield break;
        }

        if (string.IsNullOrEmpty(ClientId) && string.IsNullOrEmpty(Tenant))
        {
            yield return new ValidationResult("AppRegistration is required (or the sign-in: Tenant, ClientId and secret).", [nameof(AppRegistration)]);
            yield break;
        }
        if (string.IsNullOrEmpty(Tenant))
        {
            yield return new ValidationResult("The Tenant field is required.", [nameof(Tenant)]);
        }
        foreach (var result in Credentials.Validate(ClientId, AuthMode, ClientSecret, CertificateThumbprint, UserName, UserPassword))
        {
            yield return result;
        }
    }

    /// <summary>The instance with the sign-in of <paramref name="registration"/>: the effective connection of a job.</summary>
    public D365ConnectionOptions With(AppRegistrationOptions registration) => new()
    {
        Name = Name, AosUri = AosUri, AppRegistration = registration.Name,
        Tenant = registration.Tenant, Authority = registration.Authority, ClientId = registration.ClientId, AuthMode = registration.AuthMode,
        ClientSecret = registration.ClientSecret, CertificateThumbprint = registration.CertificateThumbprint,
        UserName = registration.UserName, UserPassword = registration.UserPassword
    };
}

internal static class Credentials
{
    public static IEnumerable<ValidationResult> Validate(string clientId, D365AuthMode mode, string? secret, string? thumbprint, string? user, string? password)
    {
        if (!Guid.TryParse(clientId, out _))
        {
            yield return new ValidationResult("ClientId must be a GUID.", ["ClientId"]);
        }

#pragma warning disable CS0618 // UsernamePassword is obsolete but still supported
        switch (mode)
        {
            case D365AuthMode.ClientSecret when string.IsNullOrEmpty(secret):
                yield return new ValidationResult("ClientSecret is required for ClientSecret authentication.", ["ClientSecret"]);
                break;
            case D365AuthMode.Certificate when string.IsNullOrEmpty(thumbprint):
                yield return new ValidationResult("CertificateThumbprint is required for Certificate authentication.", ["CertificateThumbprint"]);
                break;
            case D365AuthMode.UsernamePassword when string.IsNullOrEmpty(user) || string.IsNullOrEmpty(password):
                yield return new ValidationResult("UserName and UserPassword are required for UsernamePassword authentication.", ["UserName", "UserPassword"]);
                break;
        }
#pragma warning restore CS0618
    }
}
