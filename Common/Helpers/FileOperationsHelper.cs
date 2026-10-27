/* Copyright (c) Microsoft Corporation. All rights reserved.
   Licensed under the MIT License. */

using System;
using log4net;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using RecurringIntegrationsScheduler.Common.Contracts;
using RecurringIntegrationsScheduler.Common.Properties;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text;

namespace RecurringIntegrationsScheduler.Common.Helpers
{
    public static class FileOperationsHelper
    {
        private static readonly ILog Log = LogManager.GetLogger(typeof(FileOperationsHelper));

        /// <summary>
        /// Deletes file specified by file path
        /// </summary>
        /// <param name="filePath">File path</param>
        /// <returns>Boolean with operation result</returns>
        public static void Delete(string filePath)
        {
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }
        }

        /// <summary>
        /// Opens file as a file stream
        /// </summary>
        /// <param name="filePath">File path</param>
        /// <param name="fileShare">File sharing mode.</param>
        /// <param name="fileAccess">Requested file access.</param>
        /// <returns>Stream</returns>
        public static Stream Read(string filePath, FileShare fileShare = FileShare.Read, FileAccess fileAccess = FileAccess.Read)
        {
            if (string.IsNullOrWhiteSpace(filePath))
            {
                throw new ArgumentException("File path must be provided.", nameof(filePath));
            }

            if (!File.Exists(filePath))
            {
                return null;
            }

            try
            {
                return new FileStream(filePath,
                    FileMode.Open,
                    fileAccess,
                    fileShare,
                    4096,
                    useAsync: true);
            }
            catch (IOException ex)
            {
                Log.Warn($"Unable to open file '{filePath}'. The file may be locked by another process. {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Creates a file
        /// </summary>
        /// <param name="sourceStream">Source stream</param>
        /// <param name="filePath">Target file path</param>
        /// <returns>Boolean with operation result</returns>
        public static void Create(Stream sourceStream, string filePath)
        {
            if (sourceStream == null)
            {
                throw new ArgumentNullException(nameof(sourceStream));
            }

            if (string.IsNullOrWhiteSpace(filePath))
            {
                throw new ArgumentException("Target file path must be provided.", nameof(filePath));
            }

            if (!sourceStream.CanRead)
            {
                throw new ArgumentException("Source stream must be readable.", nameof(sourceStream));
            }

            var targetDirectoryName = Path.GetDirectoryName(filePath);
            if (targetDirectoryName == null)
                throw new DirectoryNotFoundException();

            Directory.CreateDirectory(targetDirectoryName);
            using var fileStream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None);
            if (sourceStream.CanSeek)
            {
                sourceStream.Seek(0, SeekOrigin.Begin);
            }
            sourceStream.CopyTo(fileStream);
        }

        /// <summary>
        /// Searches for source files in target path and returns list of dataMessage objects
        /// </summary>
        /// <param name="messageStatus">Data message status for found files</param>
        /// <param name="path">Path to search</param>
        /// <param name="searchPatterns">File pattern</param>
        /// <param name="searchOption">Search option</param>
        /// <param name="sortBy">Sort by field</param>
        /// <param name="reverse">Order of files</param>
        /// <returns>List of dataMessage objects</returns>
        public static IEnumerable<DataMessage> GetFiles(MessageStatus messageStatus, string path, string searchPatterns = "*.*",
            SearchOption searchOption = SearchOption.AllDirectories, OrderByOptions sortBy = OrderByOptions.Created,
            bool reverse = false)
        {
            var dir = new DirectoryInfo(path);
            var files = dir.EnumerateFiles(searchPatterns, searchOption);
            foreach (FileInfo fileName in SortFiles(files, sortBy, reverse))
            {
                var dataMessage = new DataMessage
                {
                    Name = fileName.Name,
                    FullPath = fileName.FullName,
                    MessageStatus = messageStatus
                };
                yield return dataMessage;
            }
        }

        /// <summary>
        /// Searches for status files in target path and returns list of dataMessage objects
        /// </summary>
        /// <param name="messageStatus">Data message status for found files</param>
        /// <param name="path">Path to search</param>
        /// <param name="searchPatterns">File pattern</param>
        /// <param name="searchOption">Search option</param>
        /// <param name="sortBy">Sort by field</param>
        /// <param name="reverse">Order of files</param>
        /// <returns>List of dataMessage objects</returns>
        public static IEnumerable<DataMessage> GetStatusFiles(MessageStatus messageStatus, string path,
            string searchPatterns = "*.Status", SearchOption searchOption = SearchOption.AllDirectories,
            OrderByOptions sortBy = OrderByOptions.Created, bool reverse = false)
        {
            var dir = new DirectoryInfo(path);
            var files = dir.EnumerateFiles(searchPatterns, searchOption);
            foreach (FileInfo fileName in SortFiles(files, sortBy, reverse))
            {
                DataMessage dataMessage;
                using (var file = File.OpenText(fileName.FullName))
                {
                    var serializer = new JsonSerializer();
                    dataMessage = (DataMessage) serializer.Deserialize(file, typeof(DataMessage));
                }
                yield return dataMessage;
            }
        }

        /// <summary>
        /// Searches for all subfolders of given path
        /// </summary>
        /// <param name="directory">Root directory</param>
        /// <returns>List of subfolders' paths</returns>
        public static IEnumerable<string> FindAllSubfolders(string directory)
        {
            var allSubfolders = new List<string>();
            foreach (var d in Directory.GetDirectories(directory))
            {
                allSubfolders.Add(d);
                allSubfolders.AddRange(FindAllSubfolders(d));
            }
            return allSubfolders;
        }

        /// <summary>
        /// Extracts content of data package zip archive
        /// </summary>
        /// <param name="filePath">File path of data package</param>
        /// <param name="deletePackage">Flag whether to delete zip file</param>
        /// <param name="addTimestamp">Flag whether to add timestamp to extracted file name</param>
        /// <returns>Boolean with operation result</returns>
        public static void UnzipPackage(string filePath, bool deletePackage, bool addTimestamp = false)
        {
            if (!File.Exists(filePath))
            {
                return;
            }

            var baseDirectory = Path.GetDirectoryName(filePath) ?? throw new InvalidOperationException("Unable to determine package directory.");
            var normalizedBase = Path.GetFullPath(baseDirectory);

            using (var zip = ZipFile.OpenRead(filePath))
            {
                var candidateEntries = zip.Entries
                    .Where(entry => entry.Length > 0 && entry.FullName != "Manifest.xml" && entry.FullName != "PackageHeader.xml")
                    .ToList();

                EnsureSufficientDiskSpace(baseDirectory, candidateEntries);

                foreach (var entry in candidateEntries)
                {
                    var projectedPath = addTimestamp
                        ? Path.Combine(baseDirectory, (Path.GetFileNameWithoutExtension(filePath) ?? throw new InvalidOperationException()) + "-" + entry.FullName)
                        : Path.Combine(baseDirectory, entry.FullName);

                    var fullDestination = Path.GetFullPath(projectedPath);
                    if (!fullDestination.StartsWith(normalizedBase, StringComparison.OrdinalIgnoreCase))
                    {
                        Log.Warn($"Skipping zip entry '{entry.FullName}' because it resolves outside '{normalizedBase}'.");
                        continue;
                    }

                    var destinationDirectory = Path.GetDirectoryName(fullDestination);
                    if (!string.IsNullOrEmpty(destinationDirectory))
                    {
                        Directory.CreateDirectory(destinationDirectory);
                    }

                    entry.ExtractToFile(fullDestination, !addTimestamp);
                }
            }

            if (deletePackage)
            {
                File.Delete(filePath);
            }
        }

        private static IEnumerable<FileInfo> SortFiles(IEnumerable<FileInfo> files, OrderByOptions sortBy, bool reverse)
        {
            IOrderedEnumerable<FileInfo> ordered = sortBy switch
            {
                OrderByOptions.Created => files.OrderBy(f => f.CreationTimeUtc),
                OrderByOptions.Modified => files.OrderBy(f => f.LastWriteTimeUtc),
                OrderByOptions.Size => files.OrderBy(f => f.Length),
                OrderByOptions.FileName => files.OrderBy(f => f.FullName),
                _ => files.OrderBy(f => f.FullName)
            };

            return reverse ? ordered.Reverse() : ordered;
        }

        private static void EnsureSufficientDiskSpace(string baseDirectory, IReadOnlyCollection<ZipArchiveEntry> entries)
        {
            if (entries == null || entries.Count == 0)
            {
                return;
            }

            var totalPayloadSize = entries.Sum(entry => entry.Length);
            if (totalPayloadSize <= 0)
            {
                return;
            }

            var root = Path.GetPathRoot(baseDirectory);
            if (string.IsNullOrEmpty(root))
            {
                return;
            }

            try
            {
                var driveInfo = new DriveInfo(root);
                var requiredSpace = (long)Math.Min(long.MaxValue, totalPayloadSize * 1.2);
                if (driveInfo.AvailableFreeSpace < requiredSpace)
                {
                    var message = $"Insufficient disk space to extract archive. Required: {requiredSpace:N0} bytes, available: {driveInfo.AvailableFreeSpace:N0} bytes.";
                    Log.Error(message);
                    throw new IOException(message);
                }
            }
            catch (IOException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Log.Warn($"Unable to validate disk space before extraction: {ex.Message}");
            }
        }

        /// <summary>
        /// Gets time (UTC) when the message was submitted to D365.
        /// Uses value stored in status file or, for status files written by older versions, status file creation time.
        /// </summary>
        /// <param name="dataMessage">Data message read from status file</param>
        /// <param name="statusFileExtension">Status file extension</param>
        /// <returns>Submission time or null when it cannot be determined</returns>
        public static DateTime? GetSubmittedUtc(DataMessage dataMessage, string statusFileExtension = ".Status")
        {
            if (dataMessage?.SubmittedUtc != null)
            {
                return dataMessage.SubmittedUtc.Value.ToUniversalTime();
            }

            var directory = Path.GetDirectoryName(dataMessage?.FullPath ?? string.Empty);
            if (string.IsNullOrEmpty(directory))
            {
                return null;
            }

            var statusFile = Path.Combine(directory, Path.GetFileNameWithoutExtension(dataMessage.FullPath) + statusFileExtension);
            return File.Exists(statusFile) ? File.GetCreationTimeUtc(statusFile) : (DateTime?)null;
        }

        /// <summary>
        /// Moves source file between folders and deletes status files when needed
        /// </summary>
        /// <param name="sourceFilePath">Source file path</param>
        /// <param name="targetFilePath">Target file path</param>
        /// <param name="deleteStatusFile">Flag whether to delete status file</param>
        /// <param name="statusFileExtension">Status file extension</param>
        public static void MoveDataToTarget(string sourceFilePath, string targetFilePath, bool deleteStatusFile = false, string statusFileExtension = ".Status")
        {
            Move(sourceFilePath, targetFilePath);

            //Now status file
            if (!deleteStatusFile)
            {
                var sourceStatusFile = Path.Combine(Path.GetDirectoryName(sourceFilePath) ?? throw new InvalidOperationException(), Path.GetFileNameWithoutExtension(sourceFilePath) + statusFileExtension);
                var targetStatusFile = Path.Combine(Path.GetDirectoryName(targetFilePath) ?? throw new InvalidOperationException(), Path.GetFileNameWithoutExtension(targetFilePath) + statusFileExtension);
                Move(sourceStatusFile, targetStatusFile);
            }
            else
            {
                var sourceStatusFile = Path.Combine(Path.GetDirectoryName(sourceFilePath) ?? throw new InvalidOperationException(), Path.GetFileNameWithoutExtension(sourceFilePath) + statusFileExtension);
                Delete(sourceStatusFile);
            }
        }

        /// <summary>
        /// Moves source file between folders
        /// </summary>
        /// <param name="sourceFilePath">Source file path</param>
        /// <param name="targetFilePath">Target file path</param>
        /// <returns>Boolean with operation result</returns>
        public static void Move(string sourceFilePath, string targetFilePath)
        {
            new FileInfo(targetFilePath).Directory.Create();//Create subfolders if necessary
            if (File.Exists(targetFilePath))
            {
                File.Delete(targetFilePath);
            }
            File.Move(sourceFilePath, targetFilePath);
        }

        /// <summary>
        /// Creates status file for data message
        /// </summary>
        /// <param name="dataMessage">dataMessage object</param>
        /// <param name="statusFileExtension">Status file extension</param>
        public static void WriteStatusFile(DataMessage dataMessage, string statusFileExtension = ".Status")
        {
            if (dataMessage == null)
            {
                return;
            }
            var statusData = JsonConvert.SerializeObject(dataMessage, Formatting.Indented, new StringEnumConverter());

            using var statusFileMemoryStream = new MemoryStream(Encoding.Default.GetBytes(statusData));
            Create(statusFileMemoryStream, Path.Combine(Path.GetDirectoryName(dataMessage.FullPath) ?? throw new InvalidOperationException(), Path.GetFileNameWithoutExtension(dataMessage.FullPath) + statusFileExtension));
        }

        /// <summary>
        /// Creates status log file for data message
        /// </summary>
        /// <param name="targetDataMessage">target dataMessage object</param>
        /// <param name="httpResponse">httpResponse object</param>
        /// <param name="statusFileExtension">Status file extension</param>
        public static void WriteStatusLogFile(DataMessage targetDataMessage, HttpResponseMessage httpResponse, string statusFileExtension = ".Status")
        {
            if (targetDataMessage == null || httpResponse == null)
            {
                return;
            }
            var logFilePath = Path.Combine(Path.GetDirectoryName(targetDataMessage.FullPath) ?? throw new InvalidOperationException(), Path.GetFileNameWithoutExtension(targetDataMessage.FullPath) + statusFileExtension);
            var logData = JsonConvert.SerializeObject(httpResponse, Formatting.Indented, new StringEnumConverter());

            using var logMemoryStream = new MemoryStream(Encoding.Default.GetBytes(logData));
            Create(logMemoryStream, logFilePath);
        }

        /// <summary>
        /// Creates status log file for data message
        /// </summary>
        /// <param name="jobStatusDetail">DataJobStatusDetail object</param>
        /// <param name="targetDataMessage">target dataMessage object</param>
        /// <param name="httpResponse">httpResponse object</param>
        /// <param name="statusFileExtension">Status file extension</param>
        public static void WriteStatusLogFile(DataJobStatusDetail jobStatusDetail, DataMessage targetDataMessage, HttpResponseMessage httpResponse, string statusFileExtension = ".Status")
        {
            if (targetDataMessage == null)
            {
                return;
            }
            var logFilePath = Path.Combine(Path.GetDirectoryName(targetDataMessage.FullPath) ?? throw new InvalidOperationException(), Path.GetFileNameWithoutExtension(targetDataMessage.FullPath) + statusFileExtension);
            string logData;

            if (null != jobStatusDetail)
            {
                logData = JsonConvert.SerializeObject(jobStatusDetail, Formatting.Indented, new StringEnumConverter());
            }
            else if (null != httpResponse)
            {
                logData = JsonConvert.SerializeObject(httpResponse, Formatting.Indented, new StringEnumConverter());
            }
            else
            {
                logData = Resources.Unknown_error;
            }

            using var logMemoryStream = new MemoryStream(Encoding.Default.GetBytes(logData));
            Create(logMemoryStream, logFilePath);
        }
    }
}
