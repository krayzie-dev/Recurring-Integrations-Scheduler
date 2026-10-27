using RIS.Core.Configuration;
using RIS.Core.Options;

namespace RIS.Server.Services;

/// <summary>Jobs created together with another job in the wizard.</summary>
public static class JobTemplates
{
    public static bool CanHaveMonitor(JobType type) => type is JobType.Import or JobType.Upload;

    /// <summary>
    /// Monitor of an import (execution monitor) or upload (processing monitor) job: same connection, folders and status file
    /// extension, runs every 5 minutes. Both jobs reference each other; the parent writes status files.
    /// </summary>
    public static JobDefinition CreateMonitor(JobDefinition job, string monitorName, string processedDir, string processingErrorsDir)
    {
        JobOptionsBase options = job.Options switch
        {
            ImportJobOptions import => new ExecutionMonitorJobOptions
            {
                Connection = import.Connection, UploadSuccessDir = import.UploadSuccessDir, ProcessingSuccessDir = processedDir,
                ProcessingErrorsDir = processingErrorsDir, StatusFileExtension = import.StatusFileExtension, PackageTemplate = import.PackageTemplate
            },
            UploadJobOptions upload => new ProcessingMonitorJobOptions
            {
                Connection = upload.Connection, ActivityId = upload.ActivityId, UploadSuccessDir = upload.UploadSuccessDir, ProcessingSuccessDir = processedDir,
                ProcessingErrorsDir = processingErrorsDir, StatusFileExtension = upload.StatusFileExtension
            },
            _ => throw new InvalidOperationException("Only import and upload jobs have monitors.")
        };
        switch (job.Options)
        {
            case ImportJobOptions import: import.WriteStatusFiles = true; break;
            case UploadJobOptions upload: upload.WriteStatusFiles = true; break;
        }

        job.PairedJob = monitorName;
        var start = DateTime.Today;
        return new JobDefinition
        {
            Name = monitorName,
            Group = job.Group,
            Description = job.Description,
            PairedJob = job.Name,
            Schedule = new ScheduleOptions
            {
                Interval = TimeSpan.FromMinutes(5),
                StartAt = new DateTimeOffset(start, TimeZoneInfo.Local.GetUtcOffset(start)),
                TimeZone = job.Schedule.TimeZone
            },
            Options = options
        };
    }
}
