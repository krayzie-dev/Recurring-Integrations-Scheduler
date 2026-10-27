using System.Collections.Concurrent;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;
using Microsoft.Identity.Client;
using RIS.Core.Errors;
using RIS.Core.Options;

namespace RIS.D365;

/// <summary>
/// Provides "Authorization" header value for a D365 connection.
/// </summary>
public interface ID365TokenProvider
{
    Task<string> GetAuthorizationHeaderAsync(D365ConnectionOptions connection, CancellationToken cancellationToken);
}

/// <summary>
/// MSAL based token provider. One MSAL application per connection, so tokens are cached across job runs (4.x requested a new token every run).
/// </summary>
public sealed class MsalTokenProvider(ILogger<MsalTokenProvider> logger) : ID365TokenProvider
{
    private readonly ConcurrentDictionary<string, Lazy<object>> _applications = new();

    public async Task<string> GetAuthorizationHeaderAsync(D365ConnectionOptions connection, CancellationToken cancellationToken)
    {
        var scopes = new[] { new Uri(connection.AosUri!, ".default").ToString() };
        try
        {
            var application = _applications.GetOrAdd(CacheKey(connection), _ => new Lazy<object>(() => CreateApplication(connection))).Value;
            AuthenticationResult result;
            switch (application)
            {
                case IConfidentialClientApplication confidential:
                    result = await confidential.AcquireTokenForClient(scopes).ExecuteAsync(cancellationToken);
                    break;
                case IPublicClientApplication publicClient:
                    var account = (await publicClient.GetAccountsAsync()).FirstOrDefault();
                    if (account is not null)
                    {
                        try
                        {
                            result = await publicClient.AcquireTokenSilent(scopes, account).ExecuteAsync(cancellationToken);
                            break;
                        }
                        catch (MsalUiRequiredException)
                        {
                            // fall back to username/password below
                        }
                    }
#pragma warning disable CS0618 // ROPC kept for 4.x compatibility
                    logger.LogWarning("Connection {Connection} uses username/password authentication, which does not support MFA. Use client secret or certificate", connection.Name);
                    result = await publicClient.AcquireTokenByUsernamePassword(scopes, connection.UserName, connection.UserPassword).ExecuteAsync(cancellationToken);
#pragma warning restore CS0618
                    break;
                default:
                    throw new InvalidOperationException("Unsupported MSAL application.");
            }

            return result.CreateAuthorizationHeader();
        }
        catch (Exception ex) when (ex is MsalException or InvalidOperationException or System.Security.Cryptography.CryptographicException)
        {
            throw new RisException(ErrorCodes.AuthenticationFailed,
                $"Authentication to Entra ID failed for connection '{connection.Name}' (tenant {connection.Tenant}, client {connection.ClientId}): {ex.Message}",
                new Dictionary<string, object?> { ["Connection"] = connection.Name, ["Tenant"] = connection.Tenant, ["ClientId"] = connection.ClientId }, ex);
        }
    }

    private static string CacheKey(D365ConnectionOptions c) =>
        string.Join('|', c.Name, c.Authority, c.Tenant, c.ClientId, c.AuthMode, c.CertificateThumbprint, c.UserName, c.ClientSecret?.GetHashCode());

    private static object CreateApplication(D365ConnectionOptions connection)
    {
        var authority = new Uri(connection.Authority, connection.Tenant).ToString();
#pragma warning disable CS0618
        return connection.AuthMode switch
        {
            D365AuthMode.ClientSecret => ConfidentialClientApplicationBuilder.Create(connection.ClientId)
                .WithClientSecret(connection.ClientSecret).WithAuthority(authority).Build(),
            D365AuthMode.Certificate => ConfidentialClientApplicationBuilder.Create(connection.ClientId)
                .WithCertificate(FindCertificate(connection.CertificateThumbprint!)).WithAuthority(authority).Build(),
            D365AuthMode.UsernamePassword => PublicClientApplicationBuilder.Create(connection.ClientId)
                .WithAuthority(authority).Build(),
            _ => throw new InvalidOperationException($"Unsupported authentication mode {connection.AuthMode}.")
        };
#pragma warning restore CS0618
    }

    private static X509Certificate2 FindCertificate(string thumbprint)
    {
        foreach (var location in new[] { StoreLocation.LocalMachine, StoreLocation.CurrentUser })
        {
            using var store = new X509Store(StoreName.My, location);
            store.Open(OpenFlags.ReadOnly);
            var found = store.Certificates.Find(X509FindType.FindByThumbprint, thumbprint.Replace(" ", string.Empty), validOnly: false);
            if (found.Count > 0)
            {
                return found[0];
            }
        }

        throw new InvalidOperationException($"Certificate with thumbprint {thumbprint} was not found in LocalMachine\\My or CurrentUser\\My.");
    }
}
