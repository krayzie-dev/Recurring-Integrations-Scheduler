using System.Globalization;
using System.IO.Abstractions;
using System.IO.Compression;
using Microsoft.Extensions.Logging;
using RIS.Core.Errors;
using RIS.Core.Options;

namespace RIS.Transfer;

/// <summary>
/// File operations used by jobs. Locked files are retried and reported with an error code instead of being silently skipped (4.x).
/// </summary>
public interface IFileStore
{
    IFileSystem FileSystem { get; }

    /// <summary>Lists files in a folder (recursively), ordered as configured.</summary>
    IReadOnlyList<string> GetFiles(string folder, FileSelectionOptions selection, bool recursive = true);

    /// <summary>Opens a file for reading. Retries while the file is locked by another process.</summary>
    Task<Stream> OpenReadAsync(string path, RetryOptions retry, CancellationToken cancellationToken);

    /// <summary>Moves a file, creating target folders and replacing existing target. Retries while locked.</summary>
    Task MoveAsync(string source, string target, RetryOptions retry, CancellationToken cancellationToken);

    /// <summary>Writes stream content to a new file, creating folders.</summary>
    Task WriteAsync(Stream content, string path, CancellationToken cancellationToken);

    Task WriteTextAsync(string path, string content, CancellationToken cancellationToken);

    void Delete(string path);

    bool Exists(string path);

    /// <summary>Returns <paramref name="fileName"/> in <paramref name="folder"/>, or "name_1.ext", "name_2.ext"... if it exists.</summary>
    string ResolveUniquePath(string folder, string fileName);

    /// <summary>Extracts data files from a package next to it (skips Manifest.xml, PackageHeader.xml and entries escaping the folder).</summary>
    IReadOnlyList<string> UnzipPackage(string packagePath, bool deletePackage, bool addTimestamp);
}

public sealed class FileStore(IFileSystem fileSystem, ILogger<FileStore> logger) : IFileStore
{
    private static readonly HashSet<string> PackageMetadataEntries = new(StringComparer.OrdinalIgnoreCase) { "Manifest.xml", "PackageHeader.xml" };

    public IFileSystem FileSystem { get; } = fileSystem;

    public IReadOnlyList<string> GetFiles(string folder, FileSelectionOptions selection, bool recursive = true)
    {
        var directory = FileSystem.DirectoryInfo.New(folder);
        if (!directory.Exists)
        {
            throw new RisException(ErrorCodes.FolderNotAccessible, $"Folder '{folder}' does not exist or is not accessible.",
                new Dictionary<string, object?> { ["Folder"] = folder });
        }

        var files = directory.EnumerateFiles(string.IsNullOrWhiteSpace(selection.SearchPattern) ? "*.*" : selection.SearchPattern,
            recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly);

        IOrderedEnumerable<IFileInfo> ordered = selection.OrderBy switch
        {
            FileOrder.Modified => files.OrderBy(f => f.LastWriteTimeUtc),
            FileOrder.Size => files.OrderBy(f => f.Length),
            FileOrder.FileName => files.OrderBy(f => f.FullName, StringComparer.OrdinalIgnoreCase),
            _ => files.OrderBy(f => f.CreationTimeUtc)
        };

        var result = ordered.ThenBy(f => f.FullName, StringComparer.OrdinalIgnoreCase).Select(f => f.FullName).ToList();
        if (selection.Reverse)
        {
            result.Reverse();
        }
        return result;
    }

    public Task<Stream> OpenReadAsync(string path, RetryOptions retry, CancellationToken cancellationToken) =>
        WithRetryAsync(path, retry, () => (Stream)FileSystem.File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read), cancellationToken);

    public Task MoveAsync(string source, string target, RetryOptions retry, CancellationToken cancellationToken) =>
        WithRetryAsync(source, retry, () =>
        {
            var directory = FileSystem.Path.GetDirectoryName(target);
            if (!string.IsNullOrEmpty(directory))
            {
                FileSystem.Directory.CreateDirectory(directory);
            }
            FileSystem.File.Move(source, target, overwrite: true);
            return true;
        }, cancellationToken);

    public async Task WriteAsync(Stream content, string path, CancellationToken cancellationToken)
    {
        CreateParent(path);
        await using var target = FileSystem.File.Create(path);
        await content.CopyToAsync(target, cancellationToken);
    }

    public Task WriteTextAsync(string path, string content, CancellationToken cancellationToken)
    {
        CreateParent(path);
        return FileSystem.File.WriteAllTextAsync(path, content, cancellationToken);
    }

    public void Delete(string path)
    {
        if (FileSystem.File.Exists(path))
        {
            FileSystem.File.Delete(path);
        }
    }

    public bool Exists(string path) => FileSystem.File.Exists(path);

    public string ResolveUniquePath(string folder, string fileName)
    {
        FileSystem.Directory.CreateDirectory(folder);
        var safeName = FileSystem.Path.GetFileName(fileName);
        var candidate = FileSystem.Path.Combine(folder, safeName);
        if (!FileSystem.File.Exists(candidate))
        {
            return candidate;
        }

        var name = FileSystem.Path.GetFileNameWithoutExtension(safeName);
        var extension = FileSystem.Path.GetExtension(safeName);
        for (var counter = 1; ; counter++)
        {
            candidate = FileSystem.Path.Combine(folder, string.Create(CultureInfo.InvariantCulture, $"{name}_{counter}{extension}"));
            if (!FileSystem.File.Exists(candidate))
            {
                logger.LogWarning("File {File} already exists in {Folder}, using {UniqueFile} instead", safeName, folder, candidate);
                return candidate;
            }
        }
    }

    public IReadOnlyList<string> UnzipPackage(string packagePath, bool deletePackage, bool addTimestamp)
    {
        var baseDirectory = FileSystem.Path.GetFullPath(FileSystem.Path.GetDirectoryName(packagePath)!);
        var extracted = new List<string>();

        using (var stream = FileSystem.File.OpenRead(packagePath))
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Read))
        {
            foreach (var entry in zip.Entries.Where(e => e.Length > 0 && !PackageMetadataEntries.Contains(e.FullName)))
            {
                var name = addTimestamp
                    ? FileSystem.Path.GetFileNameWithoutExtension(packagePath) + "-" + entry.FullName
                    : entry.FullName;
                var destination = FileSystem.Path.GetFullPath(FileSystem.Path.Combine(baseDirectory, name));

                if (!destination.StartsWith(baseDirectory + FileSystem.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                {
                    logger.LogWarning("Skipping zip entry {Entry} of {Package} because it resolves outside {Folder}", entry.FullName, packagePath, baseDirectory);
                    continue;
                }

                CreateParent(destination);
                using var source = entry.Open();
                using var target = FileSystem.File.Create(destination);
                source.CopyTo(target);
                extracted.Add(destination);
            }
        }

        if (deletePackage)
        {
            FileSystem.File.Delete(packagePath);
        }
        return extracted;
    }

    private void CreateParent(string path)
    {
        var directory = FileSystem.Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            FileSystem.Directory.CreateDirectory(directory);
        }
    }

    private async Task<T> WithRetryAsync<T>(string path, RetryOptions retry, Func<T> operation, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return operation();
            }
            catch (IOException ex) when (ex is not FileNotFoundException and not DirectoryNotFoundException)
            {
                if (attempt >= retry.Count)
                {
                    throw new RisException(ErrorCodes.FileLocked,
                        $"File '{path}' is locked or cannot be accessed after {attempt + 1} attempt(s): {ex.Message}",
                        new Dictionary<string, object?> { ["File"] = path }, ex);
                }

                logger.LogWarning("[{ErrorCode}] File {File} is not accessible ({Reason}), retry {Attempt}/{RetryCount} in {Delay}",
                    ErrorCodes.FileLocked, path, ex.Message, attempt + 1, retry.Count, retry.Delay);
                await Task.Delay(retry.Delay, cancellationToken);
            }
        }
    }
}
