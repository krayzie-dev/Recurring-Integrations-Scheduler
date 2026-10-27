using System.Collections.Concurrent;
using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using System.Net.Http.Headers;
using Microsoft.Extensions.Localization;
using RIS.Core.Configuration;
using RIS.Core.Errors;
using RIS.Core.Options;
using RIS.D365;
using RIS.Server.Resources;
using RIS.Transfer;

namespace RIS.Server.Services;

public sealed record TestResult(bool Succeeded, string Message, TimeSpan Duration);

/// <summary>Last test of a saved connection or SFTP server (process memory; shown in the lists).</summary>
public sealed record LastTest(TestResult Result, DateTimeOffset At);

public enum TestTarget { D365, Sftp }

/// <summary>Results of the last tests per saved connection / SFTP server, kept until the service restarts.</summary>
public sealed class ConnectionTestResults
{
    private readonly ConcurrentDictionary<(TestTarget, string), LastTest> _results = new();

    public LastTest? Get(TestTarget target, string name) => _results.GetValueOrDefault((target, name.ToUpperInvariant()));

    public void Set(TestTarget target, string name, TestResult result)
    {
        _results[(target, name.ToUpperInvariant())] = new LastTest(result, DateTimeOffset.Now);
    }
}

/// <summary>
/// Checks run by the server, i.e. with the service account (that is what the jobs will use):
/// D365 sign-in and read access to Data management, access to a folder, SFTP login and remote folder.
/// Connections and SFTP servers can be tested before they are saved; an empty secret then means the stored one.
/// </summary>
public sealed class ConnectionTestService(
    IRisConfigurationStore store, ID365TokenProvider tokens, IHttpClientFactory http, ISftpTransfer sftp, IStringLocalizer<UiText> text,
    ConnectionTestResults? results = null)
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    /// <summary>Tests a saved connection and remembers the result.</summary>
    public async Task<TestResult> D365Async(string connectionName, CancellationToken ct = default)
    {
        var connection = store.Current.FindConnection(connectionName);
        if (connection is null)
        {
            return new TestResult(false, $"[{ErrorCodes.ConnectionNotFound}] Connection '{connectionName}' does not exist.", TimeSpan.Zero);
        }
        var result = await D365Async(connection, null, ct);
        results?.Set(TestTarget.D365, connection.Name, result);
        return result;
    }

    /// <summary>
    /// Signs in to Entra ID and reads one data project definition through OData, with the given (possibly unsaved) settings.
    /// Empty secrets are taken from the saved connection <paramref name="storedName"/>.
    /// </summary>
    public Task<TestResult> D365Async(D365ConnectionOptions form, string? storedName, CancellationToken ct = default) => RunAsync(async token =>
    {
        var connection = WithStoredSecrets(form, storedName is null ? null : store.Current.FindConnection(storedName));
        if (Invalid(connection) is { } invalid) return invalid;
        var authorization = await tokens.GetAuthorizationHeaderAsync(connection, token);
        using var client = http.CreateClient();
        client.Timeout = Timeout;
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(connection.AosUri!, "data/DataManagementDefinitionGroups?$top=1"));
        request.Headers.Authorization = AuthenticationHeaderValue.Parse(authorization);
        using var response = await client.SendAsync(request, token);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(token);
            return text["Test_D365Http", (int)response.StatusCode, body.Length > 300 ? body[..300] + "…" : body].Value;
        }
        return null;
    }, text["Test_D365Ok"], ct);

    /// <summary>Folder exists and the service account can list it and create/delete a file.</summary>
    public Task<TestResult> FolderAsync(string? path, CancellationToken ct = default) => RunAsync(token =>
    {
        var account = $"{Environment.UserDomainName}\\{Environment.UserName}";
        if (string.IsNullOrWhiteSpace(path)) return Task.FromResult<string?>(text["Test_FolderEmpty"].Value);
        if (!Path.IsPathRooted(path)) return Task.FromResult<string?>(text["Test_FolderNotAbsolute"].Value);
        if (!Directory.Exists(path))
        {
            var unc = path.StartsWith(@"\\", StringComparison.Ordinal) ? " " + text["Test_UncHint"] : "";
            return Task.FromResult<string?>(text["Test_FolderMissing", account].Value + unc);
        }
        try
        {
            _ = Directory.EnumerateFileSystemEntries(path).FirstOrDefault();
            var probe = Path.Combine(path, $".ris-access-test-{Guid.NewGuid():N}.tmp");
            File.WriteAllText(probe, "RIS 5 access test");
            File.Delete(probe);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return Task.FromResult<string?>(text["Test_FolderNoWrite", account, ex.Message].Value);
        }
        return Task.FromResult<string?>(null);
    }, text["Test_FolderOk", $"{Environment.UserDomainName}\\{Environment.UserName}"], ct);

    /// <summary>Tests a saved SFTP server and remembers the result.</summary>
    public async Task<TestResult> SftpAsync(string serverName, string? remoteFolder, CancellationToken ct = default)
    {
        var server = store.Current.FindSftpServer(serverName);
        if (server is null)
        {
            return new TestResult(false, $"[{ErrorCodes.SftpFailed}] SFTP server '{serverName}' does not exist.", TimeSpan.Zero);
        }
        var result = await SftpAsync(server, null, remoteFolder, ct);
        results?.Set(TestTarget.Sftp, server.Name, result);
        return result;
    }

    /// <summary>Logs in to the SFTP server (possibly unsaved settings) and lists the remote folder.</summary>
    public Task<TestResult> SftpAsync(SftpServerOptions form, string? storedName, string? remoteFolder, CancellationToken ct = default) => RunAsync(async token =>
    {
        var server = WithStoredSecrets(form, storedName is null ? null : store.Current.FindSftpServer(storedName));
        if (Invalid(server) is { } invalid) return invalid;
        await sftp.TestConnectionAsync(server, string.IsNullOrWhiteSpace(remoteFolder) ? "/" : remoteFolder, token);
        return null;
    }, text["Test_SftpOk"], ct);

    /// <summary>Copy of the form with the secrets of the saved item where the form leaves them empty.</summary>
    public static D365ConnectionOptions WithStoredSecrets(D365ConnectionOptions form, D365ConnectionOptions? stored) => new()
    {
        Name = form.Name, AosUri = form.AosUri, Tenant = form.Tenant, Authority = form.Authority, ClientId = form.ClientId, AuthMode = form.AuthMode,
        CertificateThumbprint = form.CertificateThumbprint, UserName = form.UserName,
        ClientSecret = SecretPlaceholder.IsPlaceholderOrEmpty(form.ClientSecret) ? stored?.ClientSecret : form.ClientSecret,
        UserPassword = SecretPlaceholder.IsPlaceholderOrEmpty(form.UserPassword) ? stored?.UserPassword : form.UserPassword
    };

    /// <inheritdoc cref="WithStoredSecrets(D365ConnectionOptions, D365ConnectionOptions?)"/>
    public static SftpServerOptions WithStoredSecrets(SftpServerOptions form, SftpServerOptions? stored) => new()
    {
        Name = form.Name, Host = form.Host, Port = form.Port, UserName = form.UserName, UseKey = form.UseKey, KeyPath = form.KeyPath,
        ConnectTimeout = form.ConnectTimeout, OperationTimeout = form.OperationTimeout,
        Password = SecretPlaceholder.IsPlaceholderOrEmpty(form.Password) ? stored?.Password : form.Password,
        KeyPassphrase = SecretPlaceholder.IsPlaceholderOrEmpty(form.KeyPassphrase) ? stored?.KeyPassphrase : form.KeyPassphrase
    };

    /// <summary>Validation errors of the settings (missing fields, missing secret) as one message, or null.</summary>
    private static string? Invalid(object options)
    {
        var errors = new List<ValidationResult>();
        return Validator.TryValidateObject(options, new ValidationContext(options), errors, validateAllProperties: true)
            ? null
            : $"[{ErrorCodes.ConfigurationInvalid}] " + string.Join(" ", errors.Select(e => e.ErrorMessage));
    }

    /// <summary>Runs a check; the check returns an error message or null for success.</summary>
    private static async Task<TestResult> RunAsync(Func<CancellationToken, Task<string?>> check, string success, CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Timeout);
        try
        {
            var error = await check(timeout.Token);
            return new TestResult(error is null, error ?? success, stopwatch.Elapsed);
        }
        catch (RisException ex)
        {
            return new TestResult(false, $"[{ex.ErrorCode}] {ex.Message}", stopwatch.Elapsed);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException or InvalidOperationException
                                       or System.Net.Sockets.SocketException or UnauthorizedAccessException)
        {
            return new TestResult(false, ex is OperationCanceledException ? $"Timeout after {Timeout.TotalSeconds:0} s." : ex.Message, stopwatch.Elapsed);
        }
    }
}
