using System.Diagnostics;
using System.Net.Http.Headers;
using Microsoft.Extensions.Localization;
using RIS.Core.Configuration;
using RIS.Core.Errors;
using RIS.D365;
using RIS.Server.Resources;
using RIS.Transfer;

namespace RIS.Server.Services;

public sealed record TestResult(bool Succeeded, string Message, TimeSpan Duration);

/// <summary>
/// Checks run by the server, i.e. with the service account (that is what the jobs will use):
/// D365 sign-in and read access to Data management, access to a folder, SFTP login and remote folder.
/// </summary>
public sealed class ConnectionTestService(
    IRisConfigurationStore store, ID365TokenProvider tokens, IHttpClientFactory http, ISftpTransfer sftp, IStringLocalizer<UiText> text)
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    /// <summary>Signs in to Entra ID and reads one data project definition through OData.</summary>
    public Task<TestResult> D365Async(string connectionName, CancellationToken ct = default) => RunAsync(async token =>
    {
        var connection = store.Current.FindConnection(connectionName) ?? throw new RisException(ErrorCodes.ConnectionNotFound, $"Connection '{connectionName}' does not exist.");
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

    /// <summary>Logs in to the SFTP server and lists the remote folder.</summary>
    public Task<TestResult> SftpAsync(string serverName, string? remoteFolder, CancellationToken ct = default) => RunAsync(async token =>
    {
        var server = store.Current.FindSftpServer(serverName) ?? throw new RisException(ErrorCodes.SftpFailed, $"SFTP server '{serverName}' does not exist.");
        await sftp.TestConnectionAsync(server, string.IsNullOrWhiteSpace(remoteFolder) ? "/" : remoteFolder, token);
        return null;
    }, text["Test_SftpOk"], ct);

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
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException or InvalidOperationException)
        {
            return new TestResult(false, ex is OperationCanceledException ? $"Timeout after {Timeout.TotalSeconds:0} s." : ex.Message, stopwatch.Elapsed);
        }
    }
}
