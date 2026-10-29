using Microsoft.Extensions.Logging;
using RIS.Core.Contracts;
using RIS.Core.Errors;
using RIS.Core.Options;
using RIS.Core.Polling;
using RIS.D365;
using RIS.Transfer;

namespace RIS.Jobs.Handlers;

/// <summary>
/// Imports files or packages using Package API (blob upload + ImportFromPackage). 4.x: Job.Import.
/// 5.0: failure of one file does not stop the remaining files; temporary packages are always deleted.
/// </summary>
public sealed class ImportHandler(ID365ClientFactory clients, JobFiles jobFiles, IPollingClock clock, LegalEntityCache legalEntities) : IJobHandler<ImportJobOptions>
{
    private const int MaxLegalEntityLength = 4;

    public async Task<JobOutcome> RunAsync(JobRunContext run, ImportJobOptions options, CancellationToken cancellationToken)
    {
        var logger = run.Logger;
        await jobFiles.DownloadInboundAsync(run, options.SftpInbound, options.InputDir, cancellationToken);

        var files = jobFiles.Files.GetFiles(options.InputDir, options.Files);
        if (files.Count == 0)
        {
            return JobOutcome.Skipped("No input files").With("Files", 0);
        }

        logger.LogInformation("Found {Count} file(s) in input folder {Folder}", files.Count, options.InputDir);

        var client = clients.Create(run.Connection, options.ActionPaths);
        string? templateInputFileName = null;
        IReadOnlyList<DataProjectEntity>? projectEntities = null;
        if (options.Envelope == PackageEnvelope.Generated)
        {
            // Once per run: the envelope of every package is generated from the data project as it is now in D365.
            projectEntities = await client.GetDataProjectEntitiesAsync(options.DataProject, cancellationToken);
            if (!projectEntities.Any(e => !e.Disabled))
            {
                throw new RisException(ErrorCodes.DataProjectNotFound,
                    $"Data project '{options.DataProject}' does not exist in D365 or has no enabled entities; input files were left in {options.InputDir}.",
                    new Dictionary<string, object?> { ["DataProject"] = options.DataProject });
            }
            logger.LogInformation("Data project {DataProject}: {Entities}", options.DataProject,
                string.Join(", ", projectEntities.Where(e => !e.Disabled).Select(e => $"{e.EntityName} ({e.SourceFormat})")));
        }
        else if (!options.InputFilesArePackages)
        {
            if (!jobFiles.Files.Exists(options.PackageTemplate!))
            {
                throw new RisException(ErrorCodes.PackageTemplateInvalid,
                    $"Package template '{options.PackageTemplate}' does not exist; input files were left in {options.InputDir}.",
                    new Dictionary<string, object?> { ["PackageTemplate"] = options.PackageTemplate });
            }

            await using var template = await jobFiles.Files.OpenReadAsync(options.PackageTemplate!, options.Retry, cancellationToken);
            templateInputFileName = PackageManifest.GetInputFileName(template, options.PackageTemplate!);
        }

        var counters = new ItemCounters();
        for (var i = 0; i < files.Count; i++)
        {
            if (i > 0 && options.DelayBetweenFiles > TimeSpan.Zero)
            {
                await clock.Delay(options.DelayBetweenFiles, cancellationToken);
            }

            var file = files[i];
            using var scope = logger.BeginScope(new Dictionary<string, object> { ["File"] = file });
            run.ReportProgress(i * 100 / files.Count, $"Importing {jobFiles.Files.FileSystem.Path.GetFileName(file)}");
            await ItemProcessing.RunAsync(logger, counters, $"Import of file {file}",
                () => ImportFileAsync(run, client, options, file, templateInputFileName, projectEntities, counters, cancellationToken));
        }

        return counters.ToOutcome("Imported files").With("DataProject", options.DataProject);
    }

    private async Task ImportFileAsync(JobRunContext run, ID365Client client, ImportJobOptions options, string file,
        string? templateInputFileName, IReadOnlyList<DataProjectEntity>? projectEntities, ItemCounters counters, CancellationToken cancellationToken)
    {
        var logger = run.Logger;
        var fs = jobFiles.Files.FileSystem;
        var fileName = fs.Path.GetFileName(file);

        var legalEntity = ResolveLegalEntity(options, file);
        if (string.IsNullOrWhiteSpace(legalEntity))
        {
            await MoveToErrorsAsync(options, file, new ErrorReport(ErrorCodes.LegalEntityNotResolved,
                $"Unable to determine legal entity from file '{fileName}'."), cancellationToken);
            throw new RisException(ErrorCodes.LegalEntityNotResolved, $"Unable to determine legal entity from file '{fileName}'. File moved to errors.");
        }

        // Multi-company: the company comes from the file name or folder, so it is checked before the package is built and sent
        // (D365 would reject it only after overwriting the data project, and reports it truncated to 4 characters).
        if (options.MultiCompany is not null && await legalEntities.ExistsAsync(client, run.Connection, legalEntity, cancellationToken) == false)
        {
            var message = $"Legal entity '{legalEntity}' (from file '{fileName}') does not exist in D365.";
            await MoveToErrorsAsync(options, file, new ErrorReport(ErrorCodes.LegalEntityNotResolved, message), cancellationToken);
            throw new RisException(ErrorCodes.LegalEntityNotResolved, message + " File moved to errors.",
                new Dictionary<string, object?> { ["LegalEntity"] = legalEntity, ["File"] = fileName });
        }

        if (legalEntity.Length > MaxLegalEntityLength)
        {
            logger.LogWarning("Legal entity {LegalEntity} resolved from {File} is longer than {Max} characters", legalEntity, fileName, MaxLegalEntityLength);
        }

        var executionId = JobFiles.NewExecutionId(options.DataProject, DateTime.Now);
        using var scope = logger.BeginScope(new Dictionary<string, object> { ["ExecutionId"] = executionId, ["LegalEntity"] = legalEntity });
        string? tempPackage = null;
        try
        {
            Stream package;
            if (projectEntities is not null)
            {
                tempPackage = fs.Path.Combine(fs.Path.GetTempPath(), $"ris-{Guid.NewGuid():N}.zip");
                if (!await BuildGeneratedPackageAsync(run, options, file, projectEntities, tempPackage, cancellationToken))
                {
                    fs.File.Delete(tempPackage);
                    tempPackage = null;
                    package = await jobFiles.Files.OpenReadAsync(file, options.Retry, cancellationToken);
                }
                else
                {
                    package = fs.File.OpenRead(tempPackage);
                }
            }
            else if (options.InputFilesArePackages)
            {
                package = await jobFiles.Files.OpenReadAsync(file, options.Retry, cancellationToken);
            }
            else
            {
                tempPackage = fs.Path.Combine(fs.Path.GetTempPath(), $"ris-{Guid.NewGuid():N}.zip");
                fs.File.Copy(options.PackageTemplate!, tempPackage);
                await using (var data = await jobFiles.Files.OpenReadAsync(file, options.Retry, cancellationToken))
                await using (var packageCopy = fs.File.Open(tempPackage, FileMode.Open, FileAccess.ReadWrite))
                {
                    foreach (var removed in PackageManifest.WrapFile(packageCopy, data, fileName, templateInputFileName!))
                    {
                        logger.LogWarning("Package template contained entry {Entry}; it was replaced by the input file. Please remove it from the template", removed);
                    }
                }
                package = fs.File.OpenRead(tempPackage);
            }

            await using (package)
            {
                logger.LogDebug("Uploading package of {File} ({Size} bytes)", fileName, package.Length);
                var blob = await client.GetAzureWriteUrlAsync(cancellationToken);
                await client.UploadToBlobAsync(blob.BlobUrl, package, cancellationToken);

                try
                {
                    var returnedId = await client.ImportFromPackageAsync(blob.BlobUrl.ToString(), options.DataProject, executionId,
                        options.ExecuteImport, options.OverwriteDataProject, legalEntity, cancellationToken);
                    executionId = string.IsNullOrEmpty(returnedId) ? executionId : returnedId;
                }
                catch (D365ApiException ex)
                {
                    // Most common setup error: the data project exists in D365 and the job does not allow overwriting it.
                    var hint = !options.OverwriteDataProject && ex.ResponseBody?.Contains("overwrite has not been specified", StringComparison.OrdinalIgnoreCase) == true
                        ? $" Data project '{options.DataProject}' already exists in D365: turn on OverwriteDataProject (\"Overwrite data project\") in the job."
                        : "";
                    await MoveToErrorsAsync(options, file, new ErrorReport(ErrorCodes.ImportRequestFailed, ex.Message + hint, executionId,
                        (int?)ex.StatusCode, ex.ResponseBody, new { options.DataProject, LegalEntity = legalEntity }), cancellationToken);
                    throw new RisException(ErrorCodes.ImportRequestFailed, $"ImportFromPackage failed for '{fileName}': {ex.Message}.{hint} File moved to errors.",
                        new Dictionary<string, object?> { ["ExecutionId"] = executionId }, ex);
                }
            }
        }
        finally
        {
            if (tempPackage is not null)
            {
                jobFiles.Files.Delete(tempPackage);
            }
        }

        var target = jobFiles.Rebase(file, options.InputDir, options.UploadSuccessDir);
        await jobFiles.Files.MoveAsync(file, target, options.Retry, cancellationToken);
        if (options.WriteStatusFiles)
        {
            await jobFiles.StatusFiles.WriteAsync(new DataMessage
            {
                Name = fileName,
                FullPath = target,
                MessageId = executionId,
                MessageStatus = MessageStatus.Enqueued,
                SubmittedUtc = clock.UtcNow
            }, options.StatusFileExtension, cancellationToken);
        }

        counters.Succeeded++;
        logger.LogInformation("File {File} submitted to D365 import, execution {ExecutionId}, legal entity {LegalEntity}", fileName, executionId, legalEntity);
    }

    private string? ResolveLegalEntity(ImportJobOptions options, string file)
    {
        var p = jobFiles.Files.FileSystem.Path;
        switch (options.MultiCompany?.Source)
        {
            case null:
                return options.Company;
            case LegalEntitySource.Subfolder:
                return p.GetFileName(p.GetDirectoryName(file));
            default:
                // 4.x: split file name (with extension) by separator, max 10 tokens, 1-based position.
                var tokens = p.GetFileName(file).Split([options.MultiCompany.FileNameSeparator!], 10, StringSplitOptions.RemoveEmptyEntries);
                var index = options.MultiCompany.TokenPosition - 1;
                return index >= 0 && index < tokens.Length ? tokens[index] : null;
        }
    }

    /// <summary>
    /// Generated envelope: a data file, or a zip with data files of several entities, becomes a package. Returns false for a zip
    /// that already is a package (has Manifest.xml): it is imported as it is.
    /// </summary>
    private async Task<bool> BuildGeneratedPackageAsync(JobRunContext run, ImportJobOptions options, string file,
        IReadOnlyList<DataProjectEntity> entities, string tempPackage, CancellationToken cancellationToken)
    {
        var fs = jobFiles.Files.FileSystem;
        var fileName = fs.Path.GetFileName(file);
        try
        {
            await using var input = await jobFiles.Files.OpenReadAsync(file, options.Retry, cancellationToken);
            await using var output = fs.File.Create(tempPackage);
            if (fileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                using var zip = new System.IO.Compression.ZipArchive(input, System.IO.Compression.ZipArchiveMode.Read, leaveOpen: true);
                var dataFiles = zip.Entries.Where(e => e.Name.Length > 0).ToList();
                if (dataFiles.Any(e => e.FullName.Equals(DataPackageBuilder.ManifestName, StringComparison.OrdinalIgnoreCase)))
                {
                    run.Logger.LogDebug("{File} already is a data package (Manifest.xml); imported as it is", fileName);
                    return false;
                }

                var parts = DataPackageBuilder.Match(options.DataProject, entities, dataFiles.Select(e => e.FullName).ToList(), options.AllowPartialPackages);
                DataPackageBuilder.Write(output, options.DataProject, parts, name => zip.GetEntry(name)!.Open());
                run.Logger.LogInformation("Package {File}: {Parts}", fileName, string.Join(", ", parts.Select(p => $"{p.FileName} -> {p.Entity.EntityName}")));
            }
            else
            {
                var parts = DataPackageBuilder.Match(options.DataProject, entities, [fileName], options.AllowPartialPackages);
                DataPackageBuilder.Write(output, options.DataProject, parts, _ => input);
            }
            return true;
        }
        catch (RisException ex) when (ex.ErrorCode == ErrorCodes.PackageFilesMismatch)
        {
            await MoveToErrorsAsync(options, file, new ErrorReport(ex.ErrorCode, ex.Message, null), cancellationToken);
            throw new RisException(ex.ErrorCode, $"{ex.Message} File '{fileName}' moved to errors.", ex.Context, ex);
        }
        catch (System.IO.InvalidDataException ex)
        {
            await MoveToErrorsAsync(options, file, new ErrorReport(ErrorCodes.PackageFilesMismatch, $"'{fileName}' is not a valid zip file: {ex.Message}", null), cancellationToken);
            throw new RisException(ErrorCodes.PackageFilesMismatch, $"'{fileName}' is not a valid zip file. File moved to errors.", null, ex);
        }
    }

    private async Task MoveToErrorsAsync(ImportJobOptions options, string file, ErrorReport report, CancellationToken cancellationToken)
    {
        var target = jobFiles.Rebase(file, options.InputDir, options.UploadErrorsDir);
        await jobFiles.Files.MoveAsync(file, target, options.Retry, cancellationToken);
        await jobFiles.StatusFiles.WriteErrorReportAsync(target, options.StatusFileExtension, report, cancellationToken);
    }
}
