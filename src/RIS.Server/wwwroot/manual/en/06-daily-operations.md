# Daily operations

## Dashboard

![Dashboard](img/dashboard.png)

- **Tiles** – number of jobs: OK, failing, paused, running.
- **Needs attention** – failing jobs (with error code and consecutive failures), jobs without success for a long time, paused jobs (with reason and user).
- **Running now** – running jobs with their duration.
- **Runs in the last 24 h** – successes, failures and others per hour.
- **Failures in the last 24 h** – click to open the run details.

Pages refresh by themselves when a job starts or finishes.

## Job list

![Job list](img/jobs.png)

- **Search** looks in name, description, D365 instance, summary and schedule. Filters: group, type, D365 instance, quick filters *Failing*, *Paused*, *Running*.
- The *Last run* column shows the result and time and, after several failures in a row, a red marker (e.g. ×3). The run and pause buttons are in the *Actions* column, always visible at the right edge.
- The coloured bar at the type marks the job type (the same colours as the bar borders in the charts).
- **Bulk actions** after selecting: *Run now*, *Pause* (with a reason visible to everyone), *Resume*, *Bulk edit*, *Delete*.
- The download icon exports the visible or selected jobs to Excel, CSV or JSON (definitions without secrets).

## Job page

![Job overview](img/job-overview.png)

Tabs: **Overview** (state, last and next runs, schedule in words), **History** (run table, *Show older runs*), **Definition** (JSON). Buttons: *Run now*, *Pause / Resume*, *Edit*, *Duplicate*, *Delete*.

![Job history](img/job-history.png)

## Run details

Clicking a run (in history, on a chart, on the dashboard) opens a panel with details: result, time, summary, **metrics** (Execution Id, file, number of files, wait time…) and the **run log**. The ↗ icon opens the run page with a shareable address, ⟲ runs the job again.

![Run details](img/run-details.png)

## Schedule (plan)

![Week plan](img/plan.png)

A Gantt chart of scheduled runs for a day or a week:

- **Bar fill** – state (scheduled, paused – dashed), **border** – job type (legend below the toolbar).
- Jobs running more often than every 30 minutes are shown as a **band** ("every 5 min").
- Bar length is the median duration of recent runs.
- **Scale**: 🔍− / 🔍+ shrinks or widens the time axis (25% to 1200%), clicking the percentage returns to 100%, ⛶ fits the whole day or week to the page width (the chart opens that way). Hour labels get denser at a larger scale.
- Grouping: group, D365 instance, type. Red line = now. Clicking a bar opens the job.
- Export: PNG, PDF (print), Excel, CSV.

## Run history

![Week history](img/history.png)

The same chart for actual runs: colour = result (success, failure, cancelled, running), ▲ = missed run, border = type. Runs that overlap at the current scale are merged into one bar with a count ("12 runs"). Below the chart a paged run table; *Failures only* switch. Above the table there are **filters**: text (job, summary, error text, run id), group, result, error code and *Started manually*. A counter shows how many runs match ("Showing 28 of 291 runs"), and the Excel and CSV export contains only the filtered runs. The same filters are on the *History* tab of the job page.

![Run list filters: text "Sales" and result "Failed"](img/history-filter.png)

## Access {#access}

*Access* (Admin role) shows who has which role and lets you give or take a role without restarting the service.

![Access: roles, members and your own access](img/access.png)

- **Role cards** – what Admin, Operator and Viewer can do and how many members they have.
- **Adding** – role, group or user (`DOMAIN\group`, `DOMAIN\user` or a SID), **Add**. The account is checked in Windows/AD; an unknown account, a duplicate or an entry that is already in the settings file are rejected with a message.
- **Table** – role, group or user (icon: group/person, SID in the tooltip), source (*Settings file* – read-only, *Application*), who added it and when, *Actions* (remove, with confirmation).
- **Your access** – your roles and what gives them (group, account, settings file or application).

Every change is recorded in the audit (object *Access*). The same from the command line: `ris access list | add | remove`.

## Audit

*Audit* (Admin role) records every configuration change: who, when, what. Expanding an entry shows the **changed fields** (before – struck through, after – highlighted); switches *All fields* and *JSON*. Secrets are never stored – a secret change shows as `*** (new value, hidden)`.

![Audit with differences](img/audit.png)

## Exports

| What | Formats | Where |
|---|---|---|
| Job list | Excel, CSV, JSON definitions | Jobs |
| Plan | PNG, PDF, Excel, CSV | Schedule |
| History | PNG, PDF, Excel, CSV | History, job page |
| Audit | Excel, CSV | Audit |

CSV is UTF-8 with a separator matching the language (`,` in English, `;` in Polish), so it opens correctly in Excel.
