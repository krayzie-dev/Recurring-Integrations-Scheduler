# RIS 5 server – operations

## Install

Run `RIS5-Setup-<version>.exe` as Administrator (from the GitHub release or the CI artefact). The wizard (English/Polish):

1. **Service account:** LocalSystem, LocalService (default), NetworkService, or a domain account / gMSA (`DOMAIN\svc-ris$`).
   Network shares (UNC paths) need a domain account or gMSA. The password is checked; the account gets "Log on as a service".
2. **Access:** local HTTP port (default 5080, localhost only); optional HTTPS port for remote access (e.g. 5443) with the
   thumbprint of a certificate in `LocalMachine\My` (checked: exists, private key, not expired; the service account gets
   read access to the key; a firewall rule "RIS5 HTTPS" is added); administrators and operators (groups/users separated by
   `;`, SIDs allowed; default `S-1-5-32-544` = local Administrators on any Windows language).
3. **Import from 4.x** (only when `Program Files (x86)\Recurring Integrations Scheduler\Schedule.xml` exists): converts the
   4.x schedule and imports all jobs **paused**. Secrets are not imported – set them in the UI (Connections) or with
   `ris db set-secret`. Report: `%ProgramData%\RIS5\import-4x\import.log`.

The installer then writes the machine settings to `%ProgramData%\RIS5\appsettings.json`, grants the service account Modify
on `%ProgramData%\RIS5`, creates the Event Log source, installs the service `RIS5` (delayed automatic start, restarts after
failures), starts it and checks `/health`; the last page shows the result and the UI address.

| Location | Content |
|---|---|
| `%ProgramFiles%\RIS5` | Program (self-contained, no .NET install needed), `cli\ris.exe` (optionally in PATH) |
| `%ProgramData%\RIS5\appsettings.json` | Machine settings from the wizard (kept on upgrade; edit and restart the service to change) |
| `%ProgramData%\RIS5` | Databases, keys, logs, backups (see below) |

### Silent install

```
RIS5-Setup-5.0.0.exe /VERYSILENT /SUPPRESSMSGBOXES /LOG=C:\Temp\ris5-setup.log ^
  /ACCOUNT="CONTOSO\svc-ris$" /HTTPPORT=5080 /HTTPSPORT=5443 /CERT=<thumbprint> ^
  /ADMINS="CONTOSO\RIS-Admins" /OPERATORS="CONTOSO\RIS-Operators" /IMPORT4X=0 /TASKS="cliPath"
```

`/ACCOUNT`: `LocalSystem`, `LocalService`, `NetworkService`, `DOMAIN\user` (+ `/PASSWORD=`), `DOMAIN\gmsa$`.

### Upgrade

Run the new setup. It stops the service (running jobs are interrupted within seconds; their D365 execution ids are logged),
copies `ris.db` and `quartz.db` to `backup\before-upgrade-<time>`, replaces the program, keeps the settings and the service
account and starts the service again (database schema updates run on start, with their own backup). To change settings
during an upgrade use `/RECONFIGURE=1` (wizard pages are shown again; the previous `appsettings.json` is kept as `.bak`).

### Uninstall

Apps & features → Recurring Integrations Scheduler 5. The service and the firewall rule are removed. Data in
`%ProgramData%\RIS5` stays unless you confirm its removal (silent uninstall never removes data).

### Development / test installation without the installer

`scripts\install-service.ps1` publishes from the repository and installs the service (see the script help).

### Parallel run with 4.x

Import the 4.x jobs paused (installer option or `ris db import jobs.json --paused`), set the secrets, run single jobs with
"Run now" against a test environment and compare the results, then pause the 4.x jobs and resume the RIS 5 jobs (bulk
resume in the Jobs page).

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
