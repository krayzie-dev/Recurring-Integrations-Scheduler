# RIS 5 web UI

The server hosts the UI at its address (default `http://localhost:5080`). Login is Windows authentication; what a user
sees depends on the role (`Ris:Security:Roles`):

| Role | Can |
|---|---|
| Viewer | dashboard, jobs, schedule, history, run details, exports of lists and charts |
| Operator | + run now, pause with a reason, resume (one job or many) |
| Admin | + edit/create/delete jobs, bulk edit, connections, SFTP servers, audit, JSON exports of definitions |

Users without a role see a "no access" page.

## Language, theme, time

App bar menus (remembered in cookies, per browser):

- **Language:** Polski / English. Default: browser language, otherwise English. Technical messages (error texts from D365,
  validation messages, logs) stay English; error codes have a description in both languages.
- **Theme:** system (follows Windows/browser light/dark, also live), light, dark.
- **Time:** server time zone (shown as `UTC+01:00`) or UTC. Applies to all times, charts and exports.

Links: `/culture/set?culture=pl-PL`, `/prefs/set?theme=Dark&zone=Utc` (both accept `redirect=/path`).

## Pages

| Page | Content |
|---|---|
| Dashboard `/` | Tiles (jobs, OK, failing, paused, running), **needs attention** (failing jobs with the error, no success for longer than `Ris:Health:RequiredJobs` or 2× the job period + 1 h, paused with reason, not scheduled), running now, runs per hour in the last 24 h, failures of the last 24 h. Refreshes when a job starts/finishes or configuration changes. |
| Jobs `/jobs` | Search and filters (group, type, connection, failing/paused/running), multi-select, bulk actions, exports. `?filter=failing`, `?group=…`, `?connection=…` |
| Job `/jobs/{group}/{name}` | Overview (schedule, next 5 runs, last result), history (export), definition JSON; actions. |
| Schedule `/plan` | Gantt chart of planned runs, week or day. |
| Data maintenance `/maintenance` | What is stored (run history, audit, logs, backups), how much, the oldest item, retention settings, last/next cleanup, "Clean up now" (Admin). |
| History `/history` | Gantt chart of executed runs (colour by result, running, missed runs) + table; click opens run details. |
| Run `/runs/{runId}` | Result, duration, D365 execution/message id, error code with description, metrics, captured job log; run again. |
| App registrations | Entra ID applications (tenant, client id, client secret or certificate) defined once; "Used by" counts instances and jobs. A renewed secret is set once for all of them. "Test" gets a token, optionally also reading data management of a chosen instance. |
| D365 instances, SFTP servers | D365 instance: AOS address and default app registration (jobs may override it in step Dynamics 365 or by bulk edit). List with sign-in method, secret / certificate status, last test ("Test" per row, "Test all") and jobs using it (link filters the job list). The form shows only the fields of the chosen method – D365: client secret **or** certificate (picked from the server certificate store or pasted); SFTP: password **or** private key – and removes the other method's credentials on save. Secrets are write-only (empty = keep the saved one). "Test connection" in the form checks the unsaved values with the service account. Delete refused while in use. |
| Audit | Every configuration change: who, when and what – a table of the changed fields (before struck through, after highlighted; "All fields" and "JSON" switches). Secrets are never stored: `***` when one is stored, `*** (new)` in the change that set it. Pages and search run in the database (50 per page); export takes all matching entries. |

## Gantt charts

- Rows are jobs, grouped by group, connection or type; search filters rows.
- **Schedule:** bar = planned run, length = median duration of the last runs (unknown → thin bar). Jobs running more often
  than every 30 min (week view) / 5 min (day view) are a continuous band "every 5 min". Paused jobs: grey dashed.
- **History:** green succeeded, red failed, grey cancelled/skipped, blue running, orange triangle = missed run
  (service down or previous run still running). "Failures only" switch.
- Days and hours follow the selected time zone, including 23/25-hour days on DST changes; red line = now.
- Export: **PNG** (as on screen, light or dark), **Print / PDF** (landscape, chart and legend only; choose "Save as PDF"),
  **xlsx/CSV** (one line per planned run or per executed run).

## Bulk actions and bulk edit

Select jobs (checkboxes; "select all" takes the filtered rows) and use the bar above the list:

- **Run now / Pause (with reason) / Resume** – Operator. The result is reported per job; one failure does not stop the others.
- **Delete** – Admin, with the list of jobs to confirm.
- **Edit together** – Admin. Set only what should change:
  - common: connection, group, time zone (or back to server time), missed runs, paused (same state as Pause/Resume in the list), pause on error,
    retries, status polling (initial/maximum delay, timeout – only job types that poll);
  - schedule: same cron or same interval + start for all; **shift by N minutes** (cron minute/hour or interval start;
    crons with lists/ranges or specific days across midnight are left unchanged with a warning); **stagger** starts
    (first job at a time, each next one N minutes later, in list order);
  - **replace in folder paths** (all folder fields including SFTP remote folders), e.g. `\\old-server\share` → `\\new-server\share`.

  **Preview** shows every changed field (before → after), warnings and the validation of the whole resulting configuration.
  **Apply** saves all jobs in one transaction, or nothing when anything is invalid. Each job gets an audit entry plus one
  "BulkUpdated" entry.

## Job editor

`/jobs/new` (wizard, step by step) and `/jobs/{group}/{name}/edit` (same steps as tabs), Admin. Steps: job type (with its
flow), Dynamics 365, files and folders, schedule, advanced, summary; a help panel explains the step and the selected field and
lists the problems of the step. Server-side tests (D365, folder, SFTP) run with the service account.

Schedule builder: daily / on selected days at a time, **every N minutes or hours** (from a time, optionally **only between two
times** and/or on selected days, e.g. 08:00–16:00 every 10 min Monday to Friday = 08:00, 08:10 … 16:00), monthly, or cron.
The description in words and the next runs (table: day, date, time, time left) update while editing. Saving validates the
complete configuration (e.g. unknown connection) and lists the problems. Changing group or name moves the job.

## Exports

| What | Formats | Where |
|---|---|---|
| Job list (selection or filtered list) | xlsx, CSV | Jobs |
| Job definitions (selection) with their connections and SFTP servers | JSON (secrets as `<SET-SECRET>`; importable with `ris db import` or `POST /api/v1/config/import`) | Jobs (Admin) |
| Whole configuration | JSON | Jobs (Admin) |
| Plan | PNG, PDF, xlsx, CSV | Schedule |
| History | PNG, PDF, xlsx, CSV | History, Job → History |
| Audit | xlsx, CSV | Audit |

xlsx: header row, autofilter, frozen header, dates and durations as Excel values, headers in the UI language.
CSV: UTF-8 with BOM, separator of the language (`;` Polish, `,` English) so Excel opens it directly.

REST for automation: `GET /api/v1/export/{jobs|history|plan}.{xlsx|csv}` (`?from=&to=`, `?zone=utc`, headers by
`Accept-Language`), `/api/v1/export/audit.{xlsx|csv}` (Admin).
