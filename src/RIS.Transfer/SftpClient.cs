using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Renci.SshNet;
using RIS.Core.Errors;
using RIS.Core.Options;

namespace RIS.Transfer;

/// <summary>
/// SFTP transfers used by jobs (inbound download, outbound upload).
/// </summary>
public interface ISftpTransfer
{
    /// <summary>Downloads files matching the mask to a local folder and deletes them on the server.</summary>
    Task<IReadOnlyList<string>> DownloadFilesAsync(SftpOptions options, string localFolder, CancellationToken cancellationToken);

    /// <summary>Uploads a file (overwrites remote file), creating remote folders.</summary>
    Task UploadFileAsync(SftpOptions options, string localFilePath, CancellationToken cancellationToken);

    /// <summary>Connects and lists remote folder (connection test).</summary>
    Task TestConnectionAsync(SftpOptions options, CancellationToken cancellationToken);
}

public sealed class SshNetSftpTransfer(IFileStore files, ILogger<SshNetSftpTransfer> logger) : ISftpTransfer
{
    public async Task<IReadOnlyList<string>> DownloadFilesAsync(SftpOptions options, string localFolder, CancellationToken cancellationToken)
    {
        var downloaded = new List<string>();
        await RunAsync(options, "download", async client =>
        {
            await foreach (var entry in client.ListDirectoryAsync(options.RemoteFolder, cancellationToken))
            {
                if (entry.IsDirectory || entry.IsSymbolicLink || !MatchesMask(entry.Name, options.FileMask))
                {
                    continue;
                }

                var localPath = files.ResolveUniquePath(localFolder, entry.Name);
                await using (var target = files.FileSystem.File.Create(localPath))
                {
                    await client.DownloadFileAsync(entry.FullName, target, cancellationToken);
                }

                // Deleting only after the local copy is complete: a crash in between leads to a duplicate, never to a lost file.
                await client.DeleteFileAsync(entry.FullName, cancellationToken);
                downloaded.Add(localPath);
                logger.LogInformation("SFTP downloaded {RemoteFile} from {SftpServer} to {File}", entry.FullName, options.Host, localPath);
            }
        }, cancellationToken);
        return downloaded;
    }

    public Task UploadFileAsync(SftpOptions options, string localFilePath, CancellationToken cancellationToken) =>
        RunAsync(options, "upload", async client =>
        {
            await EnsureRemoteFolderAsync(client, options.RemoteFolder, cancellationToken);
            var remotePath = CombineRemotePath(options.RemoteFolder, Path.GetFileName(localFilePath));
            await using var source = files.FileSystem.File.OpenRead(localFilePath);
            await client.UploadFileAsync(source, remotePath, cancellationToken);
            logger.LogInformation("SFTP uploaded {File} to {SftpServer}:{RemoteFile}", localFilePath, options.Host, remotePath);
        }, cancellationToken);

    public Task TestConnectionAsync(SftpOptions options, CancellationToken cancellationToken) =>
        RunAsync(options, "connection test", async client =>
        {
            await foreach (var _ in client.ListDirectoryAsync(options.RemoteFolder, cancellationToken))
            {
                break;
            }
        }, cancellationToken);

    private async Task RunAsync(SftpOptions options, string operation, Func<Renci.SshNet.SftpClient, Task> action, CancellationToken cancellationToken)
    {
        try
        {
            using var client = CreateClient(options);
            await client.ConnectAsync(cancellationToken);
            await action(client);
            client.Disconnect();
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not RisException)
        {
            throw new RisException(ErrorCodes.SftpFailed, $"SFTP {operation} failed for {options}: {ex.Message}",
                new Dictionary<string, object?> { ["SftpServer"] = options.Host, ["RemoteFolder"] = options.RemoteFolder }, ex);
        }
    }

    private static Renci.SshNet.SftpClient CreateClient(SftpOptions options)
    {
        AuthenticationMethod auth = options.UseKey
            ? new PrivateKeyAuthenticationMethod(options.UserName, string.IsNullOrEmpty(options.KeyPassphrase)
                ? new PrivateKeyFile(options.KeyPath!)
                : new PrivateKeyFile(options.KeyPath!, options.KeyPassphrase))
            : new PasswordAuthenticationMethod(options.UserName, options.Password ?? string.Empty);

        var connection = new ConnectionInfo(options.Host, options.Port, options.UserName, auth) { Timeout = options.ConnectTimeout };
        return new Renci.SshNet.SftpClient(connection) { OperationTimeout = options.OperationTimeout };
    }

    private static async Task EnsureRemoteFolderAsync(Renci.SshNet.SftpClient client, string remoteFolder, CancellationToken cancellationToken)
    {
        if (await client.ExistsAsync(remoteFolder, cancellationToken))
        {
            return;
        }

        var path = remoteFolder.StartsWith('/') ? "/" : string.Empty;
        foreach (var segment in remoteFolder.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            path = CombineRemotePath(path, segment);
            if (!await client.ExistsAsync(path, cancellationToken))
            {
                await client.CreateDirectoryAsync(path, cancellationToken);
            }
        }
    }

    internal static string CombineRemotePath(string left, string right) =>
        string.IsNullOrEmpty(left) ? "/" + right.TrimStart('/')
        : left.EndsWith('/') ? left + right.TrimStart('/')
        : left + "/" + right.TrimStart('/');

    internal static bool MatchesMask(string name, string? mask)
    {
        if (string.IsNullOrWhiteSpace(mask) || mask is "*" or "*.*")
        {
            return true;
        }

        var pattern = "^" + Regex.Escape(mask).Replace(@"\*", ".*").Replace(@"\?", ".") + "$";
        return Regex.IsMatch(name, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }
}
