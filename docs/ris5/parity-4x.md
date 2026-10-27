# RIS 4.x → 5.0 parity

Every 4.x JobDataMap key (`Common/Contracts/SettingsConstants.cs`) is either converted by `ris convert-schedule`
or deliberately ignored. This is enforced by `Every4xSettingKey_IsConvertedOrDeliberatelyIgnored` in `test/RIS.Migration.Tests`.

## Connections and secrets

| 4.x key | 5.0 | Notes |
|---|---|---|
| AosUri, AadTenant, AzureAuthEndpoint, AadClientId | `connections[].aosUri/tenant/authority/clientId` | Jobs with the same environment and app share one named connection. |
| UseServiceAuthentication + AadClientSecret | `authMode: ClientSecret`, `clientSecret` | 5.0 also supports `Certificate` (thumbprint). |
| UserName, UserPassword | `authMode: UsernamePassword` | Kept for compatibility; no MFA – converter warns. |
| UseADAL | – | ADAL was removed already in 4.x. |
| Sftp{Inbound,Outbound}Host/Port/Username/Password/UseKey/KeyPath/KeyPassphrase | `sftpServers[]` | Named entry; jobs reference it by name. Secrets never become job data. |
| Sftp*ServerName, Sftp*CredentialName | `sftpServers[].name` | Credential is part of the server entry. |
| Sftp*RemoteFolder, Sftp*FileMask, UseSftp* | `options.sftpInbound/sftpOutbound` | |

4.x secrets are DPAPI (LocalMachine) encrypted. `--decrypt-secrets` decrypts them on the original 4.x server; otherwise
placeholders `<SET-SECRET>` are written and reported.

## Common job settings

| 4.x key | 5.0 option | Notes |
|---|---|---|
| DelayBetweenFiles | `delayBetweenFiles` | Monitors: from `DelayBetweenStatusCheck` (4.x used it between checked files). |
| RetryCount, RetryDelay | `retry.count/delay` | File operations. HTTP retry is global (`Ris:Http`), transient errors only, never for ExportToPackage/ImportFromPackage/Enqueue. |
| PauseJobOnException | `pauseOnError` | Pauses the Quartz job after a failed run. |
| IndefinitePause | `indefinitePause` | Job is scheduled paused with reason. |
| *ActionPath, *Path (OData actions) | `actionPaths` | |
| LogVerbose | – | Replaced by log levels; every job has its own log file. |
| JobKey | – | `group.name`. |

## Per job type

| Job | 4.x keys | 5.0 |
|---|---|---|
| Export | DataProject, Company, DownloadSuccessDir, DownloadErrorsDir, UnzipPackage, AddTimestamp, DeletePackage, DelayBetweenStatusCheck, MaxDelayBetweenStatusCheck, StatusCheckTimeoutMinutes | `ExportJobOptions`, `polling.initialDelay/maxDelay/timeout` |
| Import | InputDir, UploadSuccessDir, UploadErrorsDir, DataProject, Company, PackageTemplate, InputFilesArePackages, OverwriteDataProject, ExecuteImport, MultiCompanyImport, GetLegalEntityFromSubfolder, GetLegalEntityFromFilename, FilenameSeparator, LegalEntityTokenPosition, ExecutionJobPresent, StatusFileExtension, SearchPattern, OrderBy, ReverseOrder | `ImportJobOptions` (`multiCompany`, `writeStatusFiles`, `files`) |
| Upload | ActivityId, EntityName, IsDataPackage, Company, InputDir, UploadSuccessDir, UploadErrorsDir, ProcessingJobPresent, StatusFileExtension, SearchPattern, OrderBy, ReverseOrder | `UploadJobOptions`. `UploadInOrder` was never used by 4.x. |
| Download | ActivityId, DownloadSuccessDir, DownloadErrorsDir, UnzipPackage, AddTimestamp, DeletePackage | `DownloadJobOptions` |
| ExecutionMonitor | UploadSuccessDir, ProcessingSuccessDir, ProcessingErrorsDir, StatusFileExtension, GetImportTargetErrorKeysFile, PackageTemplate, GetExecutionErrors, ErrorKeysFileTimeoutMinutes, StatusCheckTimeoutMinutes | `ExecutionMonitorJobOptions` |
| ProcessingMonitor | ActivityId, UploadSuccessDir, ProcessingSuccessDir, ProcessingErrorsDir, StatusFileExtension, GetExecutionErrors, StatusCheckTimeoutMinutes | `ProcessingMonitorJobOptions` |

Job/monitor pairs (4.x naming convention `"<name>-Execution monitor"`, `"<name>-Processing monitor"`) become `pairedJob`.

## Behaviour kept from 4.x (deliberately)

- File and folder layout: input → upload success/errors → processing success/errors; status files `<name>.Status` in 4.x format
  (4.x and 5.0 can read each other's status files during migration).
- Download/Export package file names: default `fileNamePattern` gives the 4.x names `yyyy-MM-dd_HH-mm-ss-ffff[-NNNNNN].zip`
  (downstream systems may depend on them). Configurable per job, tokens: Export `{timestamp} {date} {executionId} {dataProject} {company}`,
  Download `{timestamp} {date} {number} {correlationId} {activityId}`. An existing file is never overwritten (`_1`, `_2` suffix).
- Export treats `PartiallySucceeded` as success and logs a warning; ExecutionMonitor treats it as failure (decision 27.10.2026).
- SFTP outbound: upload, then unzip, then move package to `Uploaded`.

## Behaviour changed in 5.0 (needs review)

| Area | 4.x | 5.0 |
|---|---|---|
| Export polling | 100 attempts (≈ 8 min), limit checked before final status | Time limit (default 12 h), growing delay 5 s → 5 min, final status always wins, transient D365/network errors tolerated |
| Unknown D365 status | Export logged error and ended as success | Run fails with `RIS-GEN-002` |
| One bad file | Stopped processing of remaining files | Remaining files continue; run reported as failed |
| Locked input file | Silently skipped | Retried, then left for next run with warning `RIS-FS-001` |
| Blob upload / GetAzureWriteUrl failure (Import) | File moved to errors | File stays in input and is retried next run (transient problem) |
| Download | Dequeued all messages first, lost un-acked ones on error | Dequeue → download → ack per message |
| Monitors | Polled forever; one HTTP error stopped the run | Timeout per item (default 12 h) → errors folder; temporary errors leave item for next run |
| Error report next to failed file | Serialized `HttpResponseMessage` (no body) | JSON with error code, message, HTTP status, response body, execution id |
| Errors in logs | Only with LogVerbose; often duplicated; no context | Logged once with code, JobKey, RunId, ExecutionId, File |
| Service stop | Waited for running jobs | Running jobs are interrupted; interrupted exports are logged with execution id |
| Subfolders (Upload, ProcessingMonitor) | Files moved to success/errors without subfolders (same names overwrote each other) | Subfolders of the input folder are kept, as in Import/ExecutionMonitor |
| Interval schedule after restart | `start-time` not saved in Schedule.xml: job ran immediately after restart and the rhythm restarted from the restart time | Rhythm = `startAt` + `interval` in `timeZone`, independent of restarts. Intervals dividing an hour/day run as cron (stable wall-clock times also on DST change days). Converter: missing 4.x start-time → 00:00 + warning |
| Missed runs | Not configurable | `schedule.missedRuns`: `RunOnce` (default, one catch-up run after downtime) or `Skip`. Detection of runs missed during downtime needs the persistent store (stage 3) |
