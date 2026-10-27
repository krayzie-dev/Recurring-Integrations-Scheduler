using System.Globalization;
using Microsoft.Extensions.Logging;
using RIS.Core.Options;
using RIS.Transfer;

namespace RIS.Jobs;

/// <summary>
/// File and SFTP steps shared by job handlers.
/// </summary>
public sealed class JobFiles(IFileStore files, StatusFileStore statusFiles, ISftpTransfer sftp)
{
    public const string UploadedFolderName = "Uploaded";

    public IFileStore Files { get; } = files;

    public StatusFileStore StatusFiles { get; } = statusFiles;

    /// <summary>D365 execution id for Package API calls: "{project}-{yyyy-MM-dd_HH-mm-ss}-{guid}" (4.x format).</summary>
    public static string NewExecutionId(string dataProject, DateTime now) =>
        $"{dataProject}-{now.ToString("yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture)}-{Guid.NewGuid()}";

    /// <summary>Same file relative to another root folder (keeps subfolders, as 4.x did with path replace).</summary>
    public string Rebase(string path, string fromRoot, string toRoot)
    {
        var p = Files.FileSystem.Path;
        var relative = p.GetRelativePath(fromRoot, path);
        return relative.StartsWith("..", StringComparison.Ordinal) || p.IsPathRooted(relative)
            ? p.Combine(toRoot, p.GetFileName(path))
            : p.Combine(toRoot, relative);
    }

    public async Task<int> DownloadInboundAsync(JobRunContext run, SftpTransferOptions? transfer, string inputDir, CancellationToken cancellationToken)
    {
        if (transfer is null)
        {
            return 0;
        }

        var downloaded = await sftp.DownloadFilesAsync(run.GetSftpServer(transfer.Server), transfer, inputDir, cancellationToken);
        if (downloaded.Count > 0)
        {
            run.Logger.LogInformation("Downloaded {Count} file(s) from SFTP server {SftpServer} to {Folder}", downloaded.Count, transfer.Server, inputDir);
        }
        return downloaded.Count;
    }

    /// <summary>
    /// Outbound handling of a downloaded/exported package (4.x order): upload to SFTP, unzip, move package to "Uploaded".
    /// </summary>
    public async Task<IReadOnlyList<string>> DeliverPackageAsync(JobRunContext run, string packagePath, SftpTransferOptions? outbound,
        bool unzip, bool deletePackage, bool addTimestamp, RetryOptions retry, CancellationToken cancellationToken)
    {
        var uploaded = false;
        if (outbound is not null)
        {
            await sftp.UploadFileAsync(run.GetSftpServer(outbound.Server), outbound, packagePath, cancellationToken);
            uploaded = true;
        }

        IReadOnlyList<string> extracted = [];
        if (unzip)
        {
            extracted = Files.UnzipPackage(packagePath, deletePackage, addTimestamp);
            run.Logger.LogInformation("Extracted {Count} file(s) from {Package}", extracted.Count, packagePath);
        }

        if (uploaded && Files.Exists(packagePath))
        {
            var p = Files.FileSystem.Path;
            var target = p.Combine(p.GetDirectoryName(packagePath)!, UploadedFolderName, p.GetFileName(packagePath));
            await Files.MoveAsync(packagePath, target, retry, cancellationToken);
        }

        return extracted;
    }

    /// <summary>Internet shortcut next to a monitored file pointing to D365 execution summary (4.x ".url" file).</summary>
    public Task WriteShortcutAsync(string dataFilePath, string? url, CancellationToken cancellationToken)
    {
        var p = Files.FileSystem.Path;
        var path = p.Combine(p.GetDirectoryName(dataFilePath)!, p.GetFileNameWithoutExtension(dataFilePath) + ".url");
        return Files.WriteTextAsync(path, $"[InternetShortcut]\r\nURL={url}\r\n", cancellationToken);
    }

    public static string Timestamp(DateTime now) => now.ToString("yyyy-MM-dd_HH-mm-ss-ffff", CultureInfo.InvariantCulture);
}
