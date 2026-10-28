# Recurring Integrations Scheduler 5 (preview: stages 1–5)

RIS 5 is the .NET 10 rewrite of RIS 4.x. It lives next to the 4.x code (`src/`, `test/`, `RIS5.slnx`); 4.x still builds from
`Recurring Integrations Scheduler.sln`.

Current state: job engine, command line, Windows service with REST API and web UI (Polish/English, light/dark, bulk
operations, Gantt charts of schedule and history, exports – see [ui.md](ui.md)), SQLite storage, installer and CI
(see [operations.md](operations.md#install) and [ci.md](ci.md)). Next: parallel run with 4.x and switch-over (stage 6).

## Projects

| Project | Content |
|---|---|
| RIS.Core | Configuration model (connections, SFTP servers, job options), D365 statuses, error codes, 4.x status file contract |
| RIS.D365 | Package API and Recurring Integrations clients, MSAL with token cache, retry of transient errors, masked request logging |
| RIS.Transfer | File operations (locked-file retry, safe unzip), status files, SFTP |
| RIS.Jobs | Job pipeline (logging scope, single error log, summary), 6 job handlers, Quartz 4 job adapter |
| RIS.Persistence | ris.db (EF Core SQLite): connections, SFTP servers, jobs, audit; encrypted secrets; backups |
| RIS.Hosting | DI setup, Serilog (console, daily file, file per job, Event Log), Quartz (in-memory for the CLI, quartz.db for the server), schedule synchronization |
| RIS.Server | Windows service: web UI (Blazor + MudBlazor), REST API `/api/v1`, Windows authentication and roles, `/health` |
| RIS.Migration | Converter of 4.x `Schedule.xml` |
| RIS.Cli | `ris` command line |

## Server

Data directory `%ProgramData%\RIS5` (`Ris:Data:DataDirectory`):

| Path | Content |
|---|---|
| `ris.db` | Configuration and audit (schema migrated at start, backup first) |
| `quartz.db` | Triggers, pauses, execution history with captured job logs (schema kept up to date by Quartz.Weasel) |
| `keys\` | Data Protection keys for secrets – back them up together with `ris.db` (see [operations.md](operations.md)) |
| `logs\` | `ris-YYYYMMDD.clef`, `jobs\<Group.Name>\job-YYYYMMDD.clef` |
| `backup\` | Database copies before schema changes (10 kept) |

Install: `RIS5-Setup-<version>.exe` (GitHub release or CI artefact), see [operations.md](operations.md#install).
Run from a console: `dotnet run --project src/RIS.Server` (or `RIS.Server.exe`). Machine settings written by the installer
are in `%ProgramData%\RIS5\appsettings.json` (loaded after the program's `appsettings.json`).

`appsettings.json` (next to the executable):

| Setting | Default | |
|---|---|---|
| `Urls` | `http://localhost:5080` | Local HTTP (localhost only) |
| `Ris:Https:Port`, `Ris:Https:CertificateThumbprint` | – | Remote access over HTTPS on all interfaces with a certificate from `LocalMachine\My` (set by the installer) |
| `Ris:Security:Roles` | `Admin` = `S-1-5-32-544` (local Administrators) | Windows groups or users per role `Viewer`, `Operator`, `Admin`; SIDs or names |
| `Ris:Health:AllowAnonymous` | `true` | `/health` without login (monitoring agents) |
| `Ris:Health:RequiredJobs` | – | `{ "Export jobs.CustInvoicejour interface D365_AX2012": "1.02:00:00" }` – `/health` is Unhealthy when the job has not succeeded within 26 h |
| `Ris:ConfigurationPollInterval` | `00:00:30` | How quickly changes made by `ris db …` reach a running service |
| `Ris:Data:KeyCertificateThumbprint` | – | Protect secret keys with a certificate (LocalMachine\My) instead of DPAPI – needed to move data to another server |
| `Ris:EventLog` | `true` as a service | Warning+ to the Application log, source "Recurring Integrations Scheduler 5" |

### REST API (`/api/v1`, OpenAPI at `/openapi/v1.json`)

| Method | Path | Role |
|---|---|---|
| GET | `/jobs` – state, next/previous run, last result, consecutive failures | Viewer |
| GET | `/jobs/{group}/{name}` – also definition and next 5 runs | Viewer |
| GET | `/jobs/{group}/{name}/history?take=50` | Viewer |
| GET | `/runs/{runId}` – result, summary, metrics, error, captured log | Viewer |
| POST | `/jobs/{group}/{name}/run`, `/pause` (`{"reason": "…"}`), `/resume` | Operator |
| PUT / DELETE | `/jobs/{group}/{name}`, `/connections/{name}`, `/sftp-servers/{name}` | Admin |
| GET | `/connections`, `/sftp-servers` (secrets as `<SET-SECRET>`) | Admin |
| PUT | `/connections/{name}/secret`, `/sftp-servers/{name}/secret` (`{"value": "…"}`) | Admin |
| POST | `/config/validate`, `/config/import`; GET `/config/export`, `/audit` | Admin |
| GET | `/me` – current user and roles | any user |
| GET | `/history?from&to&result&job` – runs of all jobs | Viewer |
| POST | `/jobs/bulk/run`, `/jobs/bulk/pause`, `/jobs/bulk/resume` (`{"jobs": ["Group.Name", …], "reason": "…"}`) – result per job | Operator |
| POST | `/jobs/bulk/delete`; `/jobs/bulk/edit[?preview=true]` (`{"jobs": […], "edit": {"shiftMinutes": 30, "pathFind": "…", …}}`) | Admin |
| GET | `/export/{jobs,history,plan}.{xlsx,csv}`; `/export/audit.{xlsx,csv}` (Admin) | Viewer |

Invalid changes return 400 with the validator's messages (`errors.configuration`) and `errorCode`. Example:

```
curl --negotiate -u : http://localhost:5080/api/v1/jobs
curl --negotiate -u : -X POST http://localhost:5080/api/v1/jobs/Export%20jobs/CustInvoicejour%20interface%20D365_AX2012/run
```

## Command line

```
ris convert-schedule "C:\Program Files (x86)\Recurring Integrations Scheduler\Schedule.xml" -o jobs.json [--decrypt-secrets]
ris validate jobs.json
ris list jobs.json
ris run jobs.json --job "CustInvoicejour interface D365_AX2012" [--dry-run] [--logs <folder>]

ris db migrate                         [--data <dir>]
ris db import jobs.json                [--data <dir>]   add/update; <SET-SECRET> keeps stored secrets
ris db import-4x Schedule.xml [--paused] [--report <dir>] [--no-secrets] [--data <dir>]
                                       4.x upgrade in one step; secrets decrypted on the 4.x server, never written to a file
ris db export -o jobs.json             [--data <dir>]   secrets replaced by <SET-SECRET>
ris db set-secret connection CONTOSO-PROD              value read without echo (or from stdin)
ris db set-secret sftp "Contoso SFTP"
ris run --db --job <name> [--dry-run]                  job from ris.db, in-memory scheduler (quartz.db untouched)
```

`--decrypt-secrets` works only on the 4.x server where secrets were encrypted (DPAPI LocalMachine). `ris db` runs on the
server as an account with access to the data directory.

## Logs

- `logs/ris-YYYYMMDD.clef` – everything (compact JSON, one event per line).
- `logs/jobs/<Group.Name>/job-YYYYMMDD.clef` – one file per job.
- Every entry of a run carries `JobKey`, `RunId` (= Quartz fire instance id) and, when known, `ExecutionId` / `MessageId` / `File`.
- Errors start with an error code, see [error-codes.md](error-codes.md).
- Execution history in quartz.db keeps the run summary, metrics and the job's log lines (`GET /api/v1/runs/{runId}`).

## Documents

- **User manual** (installation, operation, maintenance, troubleshooting, FAQ; Polish and English, with screenshots) – in the application under *Help* (`/help`); source: [src/RIS.Server/wwwroot/manual](../../src/RIS.Server/wwwroot/manual) (`pl/`, `en/`, `img/`). Both languages must have the same chapter files; `ManualTests` checks chapters, links, anchors and images.
- [ui.md](ui.md) – web UI: roles, language/theme, dashboard, Gantt charts, bulk edit, exports
- [ci.md](ci.md) – installer build, GitHub Actions, releases
- [operations.md](operations.md) – installation (installer, silent install, upgrade), backup and restore, moving to another server, restarts and missed runs
- [parity-4x.md](parity-4x.md) – mapping of 4.x settings and changed behaviour
- [error-codes.md](error-codes.md)
- [quartz4-notes.md](quartz4-notes.md) – Quartz.NET 4.6.2 API and pitfalls
