# Introduction

**Recurring Integrations Scheduler 5 (RIS 5)** is a Windows service that moves data on a schedule between folders (local, network, SFTP) and **Dynamics 365 Finance & Operations (D365)**. It replaces RIS 4.x: it keeps all its features and adds a browser UI, persistent run history, contextual logs, error codes and a job wizard.

This manual is available in the application (**Help** in the menu or the ❔ icon in the top bar) in English and Polish – it follows the UI language. The **Print / PDF** button next to the chapter list saves a chapter as PDF.

![RIS 5 dashboard](img/dashboard.png)

## How it works

```
 Folders / SFTP  ──►  RIS 5 (Windows service)  ──►  D365 F&O
                       │  scheduler (Quartz)
                       │  run history, logs
                       └─ web UI + REST API
```

- RIS runs as the **RIS5** service on a Windows server. Open the UI in a browser, e.g. `http://localhost:5080` on the server or `https://server:5443` from another computer (when HTTPS is enabled).
- You sign in with your Windows (domain) account. Permissions come from roles assigned to groups or users.
- Configuration (D365 instances, jobs, secrets) is stored in `ris.db`, schedules and run history in `quartz.db` – both in `%ProgramData%\RIS5`.

## Key terms

| Term | Meaning |
|---|---|
| **Job** | One integration: what, from where, to where and when. It has a name and a group (key `Group.Name`). |
| **Job type** | Export, Import, Upload, Download, Execution monitor, Processing monitor – see [Job types](04-job-types.md). |
| **D365 instance** | A D365 environment and the Microsoft Entra ID application RIS signs in with (client secret or certificate). Many jobs share one instance. |
| **Data project** | A project in D365 *Data management* that defines entities and file format. RIS export and import run data projects. |
| **Recurring data job** | A recurring integration in D365 identified by an *activity ID*. Used by Upload and Download. |
| **Monitor** | A job that checks the result of an import or upload in D365 and moves the file to a "processed" or "errors" folder. Paired with the import or upload job. |
| **Run** | One execution of a job – with result, duration, summary, metrics and log. |
| **Execution Id** | The execution identifier in D365. Shown with the run – use it to find the execution in D365. |

## Roles

| Role | Can |
|---|---|
| **Viewer** | View the dashboard, jobs, plan, history and run details; export lists. |
| **Operator** | Everything a Viewer can, plus run, pause and resume jobs (also in bulk). |
| **Admin** | Everything: jobs, D365 instances, SFTP servers, bulk edit, audit, data maintenance. |

Roles are assigned in the settings (the installer asks for administrators and operators) – see [Installation](02-installation.md#roles).

## What's new compared to RIS 4.x

- Browser UI (no WinForms client, no .NET Remoting), light and dark theme, English and Polish.
- Configuration saved immediately to the database with an audit trail (who, when, what) – no manual `Schedule.xml` saving.
- Run history with the log of every run, Gantt charts of the plan and history, dashboard with alerts.
- Job wizard with field descriptions, tests of D365, folders and SFTP and a schedule builder (including "every 10 minutes from 8 to 16 on weekdays").
- Import package envelope generated automatically from the data project (no template), also for multi-entity packages.
- Clear error codes (`RIS-…`), Windows Event Log with fixed event ids, `/health` endpoint for monitoring.
- The schedule rhythm does not shift after a restart; missed runs are handled per job setting.
- Installer with service account choice, HTTPS and import of 4.x jobs.

Detailed comparison: `docs/ris5/parity-4x.md` in the repository.
