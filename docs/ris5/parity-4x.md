# RIS 4.x → 5.0 parity

Every 4.x JobDataMap key (`Common/Contracts/SettingsConstants.cs`) is either converted by `ris convert-schedule`
or deliberately ignored. This is enforced by `Every4xSettingKey_IsConvertedOrDeliberatelyIgnored` in `test/RIS.Migration.Tests`.

## Connections and secrets

| 4.x key | 5.0 | Notes |
|---|---|---|
| AosUri | `connections[].aosUri` | One D365 instance per AOS address (named after the host). |
| AadTenant, AzureAuthEndpoint, AadClientId | `appRegistrations[].tenant/authority/clientId` | One app registration per application (a tenant given as domain in some jobs and as id in others is one tenant). Named as in the 4.x client (`portable.config`, *Azure App registrations*), otherwise `APP-` + start of the client id. The instance's `appRegistration` is the one used by most of its jobs; a job using another one gets `options.appRegistration`. |
| UseServiceAuthentication + AadClientSecret | `authMode: ClientSecret`, `clientSecret` of the app registration | 5.0 also supports `Certificate` (thumbprint). 4.x copied the secret into every job; `import-4x` tries the secrets of the 4.x client and of all jobs against Entra ID once per app registration and keeps the accepted one. A renewed secret is set once, in the app registration. |
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
| Missed runs | Not configurable | `schedule.missedRuns`: `RunOnce` (default, one catch-up run after downtime) or `Skip`. The server keeps triggers in quartz.db and replaces a job only when its definition changed, so runs missed while the service was stopped are detected (`ris run` uses an in-memory scheduler and never catches up) |
| Interval with a daily window | Not available (only cron, e.g. `0 0/10 8-15 ? * MON-FRI`, which ends at 15:50) | `schedule.activeFrom` / `activeTo` (e.g. `08:00` / `16:00`, both included) and `schedule.days` (e.g. `["Monday", …, "Friday"]`) for interval schedules; any interval in whole minutes, rhythm starts at `activeFrom` every day |
| Import package envelope | Every import job needed a package template (zip exported from D365) | `options.envelope: Generated` builds Manifest.xml and PackageHeader.xml from the entities of the data project read from D365 (no template). Single files go to the only entity; a zip with data files of several entities (named after the entities) becomes one package in the execution order of the project; a zip with Manifest.xml is imported unchanged. Files must cover all entities (`allowPartialPackages` to relax) - D365 silently skips missing files. `Template` keeps the 4.x behaviour (own mapping, transformations, default values). Verified against D365 on 28.10.2026: columns are mapped by name, PackageHeader.xml is required, a subset manifest does not change the entity list of the project. **But** an import with `overwriteDataProject` (needed for existing projects, also in 4.x) replaces the field mapping of the project with the package (generated envelope: the columns of the file) - a recurring data job sharing the project then parses its files with that mapping (seen on 28.10.2026: an 11-column file imported with a 2-column mapping). Give RIS imports their own data project. |
| Configuration storage | Schedule.xml written only on "Save"; changes made in the client were lost after a service restart | ris.db (SQLite): every change is validated, audited (user, before/after, no secrets) and applied to the scheduler at once |
| Secrets | DPAPI LocalMachine, copied into every job | Stored once per connection / SFTP server, encrypted with ASP.NET Core Data Protection (keys in the data directory, protected by DPAPI machine key or a certificate); write-only in the API. A required secret that is not set yet makes the job fail with `RIS-AUTH-001` / `RIS-SFTP-001` |
| Remote access | Unauthenticated .NET Remoting on TCP 555 | REST API with Windows authentication and roles; localhost only by default |
| Operator pause | Lost on restart (RAM store) | Kept in quartz.db, also when the job definition changes |

## Client (4.x WinForms) → web UI (5.0)

| 4.x | 5.0 |
|---|---|
| WinForms client connecting over unauthenticated Remoting (TCP 555) | Browser UI hosted by the service, Windows login, roles Viewer/Operator/Admin |
| One grid of jobs, actions on one job at a time | Filters, multi-select, bulk run/pause (with reason)/resume/delete, bulk edit with preview |
| Changes active only until "Save schedule" | Every change saved to ris.db at once, validated and audited |
| No history, no logs | Run history (table and Gantt), run details with captured log and error code description |
| No overview of the week | Gantt chart of planned runs (week/day), dashboard with alerts |
| English only | Polish and English, light/dark theme |
| Instance, application, user, data job forms per job | App registrations, D365 instances (with a default app registration) and SFTP servers defined once, referenced by jobs (a job may choose another app registration); write-only secrets |
| No export | xlsx/CSV/PNG/PDF/JSON exports, REST exports |
