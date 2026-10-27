# RIS 5 server – operations

## Install (test installation, before the installer of stage 5)

```powershell
# as Administrator, from the repository
.\scripts\install-service.ps1 -Account "CONTOSO\svc-ris$"          # gMSA, or -Account … -Password (Read-Host -AsSecureString)
ris db import jobs.json                                             # e.g. output of ris convert-schedule
ris db set-secret connection CONTOSO-PROD
curl http://localhost:5080/health
```

The service account needs: Modify on the data directory (the script grants it), access to the exchange folders (UNC shares
need a domain account or gMSA – `LocalService` cannot reach them), outbound HTTPS to D365 and Entra ID.

Migration from 4.x in parallel: import the converted schedule with the jobs paused (`"paused": true` in `schedule`), compare
results on a test environment, then pause 4.x jobs and resume 5.0 jobs (`POST /api/v1/jobs/{group}/{name}/resume`).

## Restart and missed runs

- Triggers live in `quartz.db`. On start the service compares each job definition with the hash stored in the scheduler
  and replaces the job only when the definition changed. An unchanged job keeps its trigger, so a run planned while the
  service was stopped is detected as missed:
  - `missedRuns: RunOnce` (default) – one catch-up run right after start, then the normal rhythm;
  - `missedRuns: Skip` – no catch-up, next run at the next planned time.
- A restart never shifts the rhythm: it is `startAt` + `interval` (or cron) in `timeZone`.
- Operator pause (API) survives restarts and definition changes. `schedule.paused` in the configuration applies when the
  job is created or its definition changes.
- Service stop interrupts running jobs within seconds. An interrupted export keeps running in D365; its execution id is in
  the log (Warning). It is not repeated automatically (a repeated export could duplicate data).

## Backup

Back up the whole data directory, at least `ris.db`, `quartz.db` and `keys\`. Without `keys\` stored secrets cannot be
decrypted (`RIS-CFG-001`); set them again with `ris db set-secret`.

Consistent copy while the service runs (SQLite online copy):

```powershell
sqlite3 "$env:ProgramData\RIS5\ris.db" ".backup 'D:\Backup\ris.db'"      # or stop the service and copy the files
```

The service copies `ris.db` to `backup\` before every schema migration and `quartz.db` before a Quartz version change.

Configuration without secrets (for version control or another environment): `ris db export -o jobs.json`.

## Restore

1. Stop the service.
2. Replace `ris.db`, `quartz.db` and `keys\` in the data directory.
3. Start the service. Missed runs during the outage are handled by `missedRuns`.

## Move to another server

Secret keys are protected by DPAPI of the machine by default and cannot be read on another server. Either:

- **Certificate** (recommended for servers that may be rebuilt): import the same certificate (with private key) to
  `LocalMachine\My` on both servers, set `Ris:Data:KeyCertificateThumbprint` *before* storing secrets (or set the secrets
  again after switching), then copy the data directory; or
- **Export and set secrets again:** `ris db export -o jobs.json` on the old server, `ris db import jobs.json` and
  `ris db set-secret …` on the new one. Execution history stays on the old server.

## Monitoring

- `GET /health` – `Healthy` / `Degraded` / `Unhealthy`: scheduler running, databases and data directory available, and every
  job in `Ris:Health:RequiredJobs` succeeded within its limit.
- Application Event Log, source "Recurring Integrations Scheduler 5": Warning and Error with error codes.
- `GET /api/v1/jobs`: `consecutiveFailures` and `lastResult` per job.

## Troubleshooting

| Symptom | Check |
|---|---|
| Job fails with `RIS-AUTH-001` "Secret … is not set" | `ris db set-secret connection <name>` |
| `RIS-CFG-001` on start or when reading configuration | `keys\` missing or from another machine/certificate |
| 401 in the browser / curl | Windows authentication: use `--negotiate -u :`; the user needs a role in `Ris:Security:Roles` (403 otherwise) |
| Changes from `ris db import` not visible | Wait `Ris:ConfigurationPollInterval` (30 s) or restart the service |
| Logs of one run | `GET /api/v1/runs/{runId}` or `logs\jobs\<Group.Name>\` filtered by `RunId` |
