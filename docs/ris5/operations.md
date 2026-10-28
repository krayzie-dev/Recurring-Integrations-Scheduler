# RIS 5 server – operations

## Install

Run `RIS5-Setup-<version>.exe` as Administrator (from the GitHub release or the CI artefact). The wizard (English/Polish):

1. **Service account:** LocalSystem, LocalService (default), NetworkService, or a domain account / gMSA (`DOMAIN\svc-ris$`).
   Network shares (UNC paths) need a domain account or gMSA. The password is checked; the account gets "Log on as a service".
2. **Access:** local HTTP port (default 5080, localhost only); optional HTTPS port for remote access (e.g. 5443) with the
   thumbprint of a certificate in `LocalMachine\My` (checked: exists, private key, not expired; the service account gets
   read access to the key; a firewall rule "RIS5 HTTPS" is added); administrators and operators (groups/users separated by
   `;`, SIDs allowed; default `S-1-5-32-544` = local Administrators on any Windows language, plus the installing user).
   On the server itself the Administrators group does not count: UAC gives the browser a filtered token, so local admins
   need their account listed by name (the installer adds the installing user).
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
Validation errors (account, port, certificate) end a silent install with an error; the message is in the `/LOG` file.
From PowerShell run the setup with `& .\RIS5-Setup-….exe …` on its own line and quote arguments containing `;`
(`"/ADMINS=S-1-5-32-544;CONTOSO\user"`).

### HTTPS (remote access)

Use a certificate from the company CA with the server name (and its FQDN) in the subject alternative names, imported with
its private key into `LocalMachine\My`. For a test a self-signed certificate is enough (browsers will warn):

```powershell
$c = New-SelfSignedCertificate -DnsName $env:COMPUTERNAME, localhost -CertStoreLocation Cert:\LocalMachine\My -NotAfter (Get-Date).AddYears(1)
& .\RIS5-Setup-5.0.0.exe /VERYSILENT /SUPPRESSMSGBOXES /RECONFIGURE=1 /LOG=C:\Temp\ris5-https.log /HTTPPORT=5080 /HTTPSPORT=5443 "/CERT=$($c.Thumbprint)"
```

`/RECONFIGURE=1` on an existing installation rewrites the machine settings, so pass the other settings again (`/ADMINS`, …).
Check: `https://<server>:5443/health` answers `Healthy`, `netsh advfirewall firewall show rule name="RIS5 HTTPS"` shows the
rule, and the UI at `https://<server>:5443/` signs you in with Windows authentication. HTTP stays on localhost only. When the
certificate is renewed run the setup again with `/RECONFIGURE=1` and the new thumbprint (the service needs read access to the
new key).

### Domain account / gMSA

Needed when jobs read or write network shares (UNC): the built-in accounts access the network as the computer account or
anonymously. The installer grants "Log on as a service" and Modify on `%ProgramData%\RIS5`; access to the shares themselves
(share and NTFS permissions) must be granted by the file server admins.

- **gMSA** (recommended, no password to manage): the AD admins create it and allow this server to retrieve the password;
  on the server `Install-ADServiceAccount svc-ris$` and `Test-ADServiceAccount svc-ris$` (must return `True`), then
  `/ACCOUNT="CONTOSO\svc-ris$"` (no password; in the wizard leave the password empty).
- **Domain user:** enter it in the wizard (the password is checked with a network logon). In a silent install
  `/ACCOUNT="CONTOSO\svc-ris" /PASSWORD=…` – the password ends up in the command history, so prefer the wizard.
- **After the install:** open a job in the UI and use "Check access" next to its folders – the check runs as the service account
  and reports the account name.

### Installation tests

| Scenario | Version | Result (27.10.2026) |
|---|---|---|
| First install (LocalService, localhost HTTP) | 5.0.0-local.2 | Passed |
| Upgrade with `/RECONFIGURE=1` (roles, Event Log source) | 5.0.0-local.3 | Passed |
| Upgrade (service stopped cleanly, databases backed up, settings kept) | 5.0.0-local.4 | Passed |
| Uninstall and reinstall (jobs, history, settings and roles kept) | 5.0.0-local.5 | Passed |
| HTTPS with a certificate from `LocalMachine\My` (TLS 1.3, firewall rule, key access for LocalService, Windows sign-in over HTTPS) | 5.0.0-local.6 | Passed |
| Domain account / gMSA | – | Not tested yet: do it on the target server |

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
- Windows Event Log: own log **RIS5** (Event Viewer → Applications and Services Logs → RIS5; 20 MB, oldest entries
  overwritten), source "Recurring Integrations Scheduler 5". Warnings and errors (`Ris:EventLogMinimumLevel`, default
  Warning) plus service start/stop. Stable event ids for monitoring:

  | Id | Meaning |
  |---|---|
  | 1000 / 1001 | Service started (version, account, addresses) / stopping |
  | 2000 | Job run failed or partly failed (summary with metrics) |
  | 10000 + 100 × category + number | Error with a RIS code: categories CFG=1, AUTH=2, HTTP=3, EXP=4, IMP=5, UPL=6, DWN=7, MON=8, FS=9, SFTP=10, GEN=11 – e.g. RIS-HTTP-001 = 10301, RIS-IMP-004 = 10504 |
  | 3000 / 4000 | Other warnings / errors |

  Versions up to 5.0.0-local.13 wrote to the Application log; the setup moves the source to RIS5.
- `GET /api/v1/jobs`: `consecutiveFailures` and `lastResult` per job.

## Troubleshooting

| Symptom | Check |
|---|---|
| Job fails with `RIS-AUTH-001` "Secret … is not set" | `ris db set-secret connection <name>` |
| `RIS-CFG-001` on start or when reading configuration | `keys\` missing or from another machine/certificate |
| 401 in the browser / curl | Windows authentication: use `--negotiate -u :`; the user needs a role in `Ris:Security:Roles` (403 otherwise) |
| Changes from `ris db import` not visible | Wait `Ris:ConfigurationPollInterval` (30 s) or restart the service |
| Logs of one run | `GET /api/v1/runs/{runId}` or `logs\jobs\<Group.Name>\` filtered by `RunId` |
| Setup: "Certificate not found in LocalMachine\My" for an existing certificate | Setup 5.0.0-local.5 and older, started from PowerShell 7 – use a newer setup (or start it from Explorer / Windows PowerShell) |
| Setup: PowerShell `ParserError` at `/VERYSILENT` | Two commands pasted on one line; run `$c = …` and `& .\RIS5-Setup…` separately |
| Service does not start after enabling HTTPS, `RIS-CFG-001` in the Event Log | Certificate missing, without private key, or the service account cannot read the key – run the setup with `/RECONFIGURE=1` and a valid thumbprint |
