using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using RIS.Core.Contracts;
using RIS.Core.Options;

namespace RIS.Transfer;

/// <summary>
/// Status file found in a folder together with its data file.
/// </summary>
public sealed record StatusFileEntry(string StatusFilePath, DataMessage Message);

/// <summary>
/// Error report written next to a failed file (same name as status file, as in 4.x).
/// </summary>
public sealed record ErrorReport(
    string ErrorCode,
    string Message,
    string? ExecutionId = null,
    int? HttpStatusCode = null,
    string? ResponseBody = null,
    object? Details = null)
{
    public DateTime TimestampUtc { get; init; } = DateTime.UtcNow;
}

/// <summary>
/// Reads and writes status files ("file.Status") used to hand over submitted files to monitor jobs.
/// Format is compatible with RIS 4.x.
/// </summary>
public sealed class StatusFileStore(IFileStore files, ILogger<StatusFileStore> logger)
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    static StatusFileStore() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public string GetStatusFilePath(string dataFilePath, string extension)
    {
        var path = files.FileSystem.Path;
        return path.Combine(path.GetDirectoryName(dataFilePath) ?? string.Empty, path.GetFileNameWithoutExtension(dataFilePath) + extension);
    }

    public Task WriteAsync(DataMessage message, string extension, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(message.FullPath);
        return files.WriteTextAsync(GetStatusFilePath(message.FullPath, extension), JsonSerializer.Serialize(message, Json), cancellationToken);
    }

    /// <summary>Writes error report with name of the status file in the folder of the failed file.</summary>
    public Task WriteErrorReportAsync(string failedFilePath, string extension, ErrorReport report, CancellationToken cancellationToken) =>
        files.WriteTextAsync(GetStatusFilePath(failedFilePath, extension), JsonSerializer.Serialize(report, Json), cancellationToken);

    /// <summary>Reads all status files in a folder (recursively). Unreadable files are logged and skipped.</summary>
    public IReadOnlyList<StatusFileEntry> ReadAll(string folder, string extension)
    {
        var fileSystem = files.FileSystem;
        if (!fileSystem.Directory.Exists(folder))
        {
            return [];
        }

        var result = new List<StatusFileEntry>();
        foreach (var statusFile in fileSystem.Directory.EnumerateFiles(folder, "*" + extension, SearchOption.AllDirectories)
                     .OrderBy(f => fileSystem.File.GetCreationTimeUtc(f)))
        {
            try
            {
                var message = JsonSerializer.Deserialize<DataMessage>(ReadText(statusFile), Json);
                if (message?.FullPath is null)
                {
                    logger.LogWarning("Status file {StatusFile} has no data file path and is skipped", statusFile);
                    continue;
                }
                result.Add(new StatusFileEntry(statusFile, message));
            }
            catch (Exception ex) when (ex is JsonException or IOException)
            {
                logger.LogWarning(ex, "Status file {StatusFile} cannot be read and is skipped", statusFile);
            }
        }
        return result;
    }

    /// <summary>Time of submission: stored in status file, or (4.x status files) creation time of the status file.</summary>
    public DateTime? GetSubmittedUtc(StatusFileEntry entry)
    {
        if (entry.Message.SubmittedUtc is { } submitted)
        {
            return submitted.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(submitted, DateTimeKind.Utc) : submitted.ToUniversalTime();
        }

        return files.FileSystem.File.Exists(entry.StatusFilePath) ? files.FileSystem.File.GetCreationTimeUtc(entry.StatusFilePath) : null;
    }

    /// <summary>Moves data file to target and moves or deletes its status file.</summary>
    public async Task MoveWithStatusAsync(string source, string target, string extension, bool deleteStatusFile, RetryOptions retry, CancellationToken cancellationToken)
    {
        await files.MoveAsync(source, target, retry, cancellationToken);

        var sourceStatus = GetStatusFilePath(source, extension);
        if (!files.Exists(sourceStatus))
        {
            return;
        }

        if (deleteStatusFile)
        {
            files.Delete(sourceStatus);
        }
        else
        {
            await files.MoveAsync(sourceStatus, GetStatusFilePath(target, extension), retry, cancellationToken);
        }
    }

    /// <summary>4.x wrote status files in system ANSI code page (Encoding.Default on .NET Framework).</summary>
    private string ReadText(string path)
    {
        var bytes = files.FileSystem.File.ReadAllBytes(path);
        try
        {
            return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(bytes).TrimStart('﻿');
        }
        catch (DecoderFallbackException)
        {
            return Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.ANSICodePage).GetString(bytes);
        }
    }
}
