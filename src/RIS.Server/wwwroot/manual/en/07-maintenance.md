# Maintenance

## Where the data is

| Location | Contents |
|---|---|
| `%ProgramFiles%\RIS5` | Program, `cli\ris.exe` |
| `%ProgramData%\RIS5\appsettings.json` | Machine settings (ports, HTTPS, roles, retention). Restart the service after a change. |
| `%ProgramData%\RIS5\ris.db` | Configuration: D365 instances, SFTP servers, jobs, encrypted secrets, audit |
| `%ProgramData%\RIS5\quartz.db` | Schedule (triggers, pauses) and run history with logs |
| `%ProgramData%\RIS5\keys\` | Secret encryption keys – **without them the secrets cannot be read** |
| `%ProgramData%\RIS5\logs\` | `ris-YYYYMMDD.clef` (service log), `jobs\<Group.Job>\` (per job log) |
| `%ProgramData%\RIS5\backup\` | Database copies taken before schema changes and upgrades |

## Retention and data cleanup

*Data maintenance* (Admin role) shows what is stored, how much, since when and for how long, plus the last and next cleanup. **Clean up now** runs the cleanup immediately.

![Data maintenance](img/maintenance.png)

| Setting (`Ris:Retention`) | Default | Meaning |
|---|---|---|
| `RunHistoryDays` / `FailedRunHistoryDays` | 30 / 90 | Run history (successful / failed) |
| `MaxRunsPerJob` / `MaxRuns` | 1000 / 200000 | Newest runs per job (failures not counted) / in total |
| `RunLogKilobytes` | 64 | Log of one run (oldest lines are dropped) |
| `AuditDays` | 365 | Configuration audit (the newest entry is always kept) |
| `UpgradeBackupsToKeep` / `SettingsBackupsToKeep` | 5 / 5 | Pre-upgrade copies / `appsettings.json` copies |
| `CleanupAt` | 03:30 | Daily cleanup (also 10 min after the service starts) |
| `Ris:LogRetentionDays` | 30 | Log files; empty log folders of deleted jobs are removed |

Example change in `%ProgramData%\RIS5\appsettings.json`:

```json
{
  "Ris": {
    "Retention": { "RunHistoryDays": 60, "AuditDays": 730 },
    "LogRetentionDays": 60
  }
}
```

History is trimmed continuously by the scheduler; the rest by the daily cleanup. Files in job folders (In, Processed…) are **not** cleaned up by RIS.

## Logs

- **Service log** – `logs\ris-YYYYMMDD.clef` (one JSON object per line, CLEF format; readable e.g. in Seq or with `Get-Content … | ConvertFrom-Json`).
- **Job log** – `logs\jobs\<Group.Job>\job-YYYYMMDD.clef` – everything about one job.
- **Run log** – in the run details in the UI (up to 64 KB per run).
- Each entry carries `JobKey`, `RunId` and, where relevant, `ExecutionId`, `MessageId`, `File` and the `RIS-…` error code.

## Windows Event Log

Log **RIS5** (*Event Viewer → Applications and Services Logs → RIS5*, 20 MB, oldest entries overwritten). Warnings and errors are written (`Ris:EventLogMinimumLevel`), plus service start and stop. Fixed event ids for monitoring:

| Id | Meaning |
|---|---|
| 1000 / 1001 | Service started (version, account, addresses) / stopping |
| 2000 | Job run failed or partially succeeded |
| 10000 + 100 × category + number | Error with a RIS code: CFG=1, AUTH=2, HTTP=3, EXP=4, IMP=5, UPL=6, DWN=7, MON=8, FS=9, SFTP=10, GEN=11 – e.g. RIS-HTTP-001 = **10301**, RIS-IMP-004 = **10504** |
| 3000 / 4000 | Other warnings / errors |

> After upgrading from a version that wrote to the *Application* log, Windows moves the entries to RIS5 only **after the computer restarts**.

## Monitoring

- `GET /health` – `Healthy` / `Degraded` / `Unhealthy`: the scheduler runs, databases and the data folder are available, and jobs listed in `Ris:Health:RequiredJobs` succeeded within the given time, e.g.:

  ```json
  { "Ris": { "Health": { "RequiredJobs": { "Export jobs.CustInvoiceJour": "1.02:00:00" } } } }
  ```

  (*Unhealthy* when the invoice export has not succeeded for 26 hours). By default `/health` does not require sign-in.
- The RIS5 event log (ids above).
- `GET /api/v1/jobs` – `lastResult` and `consecutiveFailures` of each job.

## Backup and restore

**Backup**: the whole `%ProgramData%\RIS5` folder, at least `ris.db`, `quartz.db` and `keys\`. A consistent copy while the service runs:

```powershell
sqlite3 "$env:ProgramData\RIS5\ris.db" ".backup 'D:\Backup\ris.db'"
```

or stop the service and copy the files. Configuration without secrets (e.g. for source control): `ris db export -o jobs.json`.

**Restore**: stop the service → replace `ris.db`, `quartz.db`, `keys\` → start the service. Runs missed during the outage are handled according to the *Missed runs* setting.

## Moving to another server

Secret keys are protected by the machine's DPAPI by default and do not work on another server. Options:

- **Certificate** (for servers that may need to be rebuilt): the same certificate with a private key on both servers, `Ris:Data:KeyCertificateThumbprint` set *before* secrets are saved, then copy the data folder;
- **Export and re-enter secrets**: `ris db export -o jobs.json` on the old server, `ris db import jobs.json` and enter the secrets again (in *D365 instances* or with `ris db set-secret`) on the new one. The run history stays on the old server.

## Maintenance windows

- **Pause many jobs** during D365 maintenance: select them on the list → *Pause* with a reason (e.g. "D365 maintenance until 18:00"). Everyone sees the reason.
- **Start the service without running jobs**: `Ris:SchedulerStartDelay` (e.g. `"01:00:00"`) – the UI works immediately, jobs start after the delay.
- **Stopping the service** interrupts running jobs within seconds; the Execution Id of an interrupted export is logged, and the export may still finish in D365 (it is not repeated automatically).

## Command line (`ris.exe`)

| Command | Purpose |
|---|---|
| `ris db export -o jobs.json` | Configuration to a file (secrets as `<SET-SECRET>`) |
| `ris db import jobs.json [--paused]` | Add/update D365 instances, SFTP servers and jobs |
| `ris db set-secret connection <name>` | Set the secret of a D365 instance (typed without echo) |
| `ris convert-schedule Schedule.xml -o jobs.json` | Convert a RIS 4.x configuration |
| `ris validate jobs.json` | Check a configuration file and its folders |
| `ris run jobs.json --job <Group.Job>` | Run one job once from the console (testing) |
