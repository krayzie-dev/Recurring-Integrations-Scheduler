# Recurring Integrations Scheduler 5 (preview: stages 1–2)

RIS 5 is the .NET 10 rewrite of RIS 4.x. It lives next to the 4.x code (`src/`, `test/`, `RIS5.slnx`); 4.x still builds from
`Recurring Integrations Scheduler.sln`.

Current state: job engine and command line. Not yet available: Windows service, SQLite storage, web UI (MudBlazor +
Quartz dashboard), installer.

## Projects

| Project | Content |
|---|---|
| RIS.Core | Configuration model (connections, SFTP servers, job options), D365 statuses, error codes, 4.x status file contract |
| RIS.D365 | Package API and Recurring Integrations clients, MSAL with token cache, retry of transient errors, masked request logging |
| RIS.Transfer | File operations (locked-file retry, safe unzip), status files, SFTP |
| RIS.Jobs | Job pipeline (logging scope, single error log, summary), 6 job handlers, Quartz 4 job adapter |
| RIS.Hosting | DI setup, Serilog (console, daily file, file per job, Event Log), Quartz with execution history and log capture |
| RIS.Migration | Converter of 4.x `Schedule.xml` |
| RIS.Cli | `ris` command line |

## Command line

```
ris convert-schedule "C:\Program Files (x86)\Recurring Integrations Scheduler\Schedule.xml" -o jobs.json [--decrypt-secrets]
ris validate jobs.json
ris list jobs.json
ris run jobs.json --job "CustInvoicejour interface D365_AX2012" [--dry-run] [--logs <folder>]
```

`--decrypt-secrets` works only on the 4.x server where secrets were encrypted (DPAPI LocalMachine).

## Logs

- `logs/ris-YYYYMMDD.clef` – everything (compact JSON, one event per line).
- `logs/jobs/<Group.Name>/job-YYYYMMDD.clef` – one file per job.
- Every entry of a run carries `JobKey`, `RunId` (= Quartz fire instance id) and, when known, `ExecutionId` / `MessageId` / `File`.
- Errors start with an error code, see [error-codes.md](error-codes.md).
- Quartz execution history keeps the run summary, metrics and the job's log lines.

## Documents

- [parity-4x.md](parity-4x.md) – mapping of 4.x settings and changed behaviour
- [error-codes.md](error-codes.md)
- [quartz4-notes.md](quartz4-notes.md) – Quartz.NET 4.6.2 API and pitfalls
