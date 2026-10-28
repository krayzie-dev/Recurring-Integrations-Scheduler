# Configuring jobs

## Job wizard

*Jobs → New job* (Admin role). A new job is created step by step; when editing an existing job the same steps are tabs you can switch between freely.

![Step 1 – choosing the job type](img/wizard-type.png)

| Step | Contents |
|---|---|
| **1. Job** | Type (card with a description and a flow diagram), name, group, description. For Import and Upload the *Also create the monitor job* switch. *Based on an existing job* copies all settings. |
| **2. Dynamics 365** | D365 instance, app registration (empty = the default of the instance; another one when the job must run as another application – as in 4.x), data project or activity ID, company, import options. Buttons **Test connection** and **Show project entities**. |
| **3. Files and folders** | Input, success and error folders, file selection and order, extraction, SFTP. Next to folders the **Check access** button (test as the service account: exists, read, write). |
| **4. Schedule** | Schedule builder, time zone, missed runs, paused – see [Schedule](#schedule). |
| **5. Advanced** | Status polling, request retries, pause on error, OData action paths. |
| **6. Summary** | Plain-language description of the whole job, next runs and validation of the whole configuration before saving. |

The **help panel on the right** describes the current step and the field marked with the ⓘ icon and lists the step's problems (e.g. a missing required folder). **Next** does not move on while the step has errors.

You can also **duplicate** a job (*Duplicate* on the job page) – the copy opens in the wizard with "(copy)" added to the name.

## Example 1: Export

Goal: every day at 02:00 export sales invoices (data project `CustInvoiceJour`) from company USMF to `D:\Integrations\Invoices\Out` and extract the package.

1. **Type**: Export, name `CustInvoiceJour`, group `Export jobs`.
2. **Dynamics 365**: D365 instance `CONTOSO-PROD`, data project `CustInvoiceJour` (exactly as in D365), company `USMF`.
3. **Files**: download folder `D:\Integrations\Invoices\Out`, tick *Unzip package*. Optionally *Add timestamp*, delete the package after extraction, send to SFTP.
4. **Schedule**: *Daily* at `02:00`.
5. **Summary → Save**.

After each run the history shows the Execution Id, D365 status, number of status checks and file name. Export waits for the result for up to 12 hours (growing intervals between checks) – set in the *Advanced* step.

## Example 2: Import with execution monitor

Goal: import vendor invoice files arriving in `D:\Integrations\VendorInvoices\In` every 15 minutes on weekdays 7–18 and check the result.

1. **Type**: Import, name `Vendor invoices import`, group `Finance`, tick **Also create the monitor job**.
2. **Dynamics 365**: D365 instance, data project `Vendor invoices`, company `USMF`, **Package envelope: Generated from the data project**, **Overwrite data project** on.

   ![Dynamics 365 step of an import job](img/editor-d365.png)

3. **Files**: `In`, `Uploaded`, `UploadErrors`; for the monitor `Processed` and `ProcessingErrors`.

   ![Files and folders step](img/editor-files.png)

4. **Schedule**: *Every 15 minutes*, switch *Only between* 07:00–18:00, days Mon–Fri. The monitor gets an every-5-minutes schedule.

### Package envelope

D365 imports **data packages** (a ZIP with data files, `Manifest.xml` and `PackageHeader.xml`). RIS builds the package in one of two ways:

- **Generated from the data project (recommended)** – RIS reads the project's entities from D365 (order, format, validation settings) and creates `Manifest.xml` and `PackageHeader.xml` itself. No template is needed. Columns are mapped **by name** (any order, a subset of columns is fine).
  - **A single file** goes to the project's only entity – any file name.
  - **A ZIP with files of several entities** (no manifest) becomes one package. Name the files after the entities, e.g. `Vendor invoice header.csv` and `Vendor invoice lines.csv` (case, spaces, `_` and `-` do not matter). The execution order comes from the D365 project.
  - **A ZIP with `Manifest.xml`** is sent unchanged (ready package).
  - If the files do not match the entities (a missing file for an entity, a file without an entity, two files for one entity), the file goes to errors with code `RIS-IMP-004` and nothing is sent to D365. **D365 itself does not report missing files** – it would import the rest and report success. *Allow incomplete packages* disables this check.
  - **Show project entities** lists the entities, levels and expected file names.
- **From a package template (4.x)** – a copy of a ZIP exported from D365 into which RIS puts the data file. Needed when the project has its own mapping, transformations or default values in D365.

> **Overwrite data project** is required when the project already exists in D365 (D365 rejects the import otherwise: *"The data project … exists and overwrite has not been specified"*). Overwriting replaces the **project's field mapping** with the mapping from the package (with a generated envelope – the columns of the imported file). Therefore **RIS imports should have their own data project**, not used by recurring data jobs or manual imports.

### Import into several companies

One job can import files into different companies. In the *Dynamics 365* step leave the company empty and set the **Multi-company** section:

- **From subfolder** – the company is the name of the file's folder: `In\2001\groups.csv` → 2001, `In\2101\groups.csv` → 2101. Subfolders are kept in the target folders.
- **From file name** – the company is a part of the name: with separator `_` and position 1 the file `2001_groups.csv` → 2001. This also works with files downloaded from SFTP.

Before sending, RIS checks that the company exists in D365. A file with an unknown company (e.g. `groups.csv` without a code) goes straight to errors with code `RIS-IMP-002` and is not sent to D365; the other files are imported. RIS reads the list of companies once a day (per environment) and, when it meets an unknown code, reloads it at most every 5 minutes, so a company created during the day is recognised.

## Example 3: Upload with processing monitor {#upload}

Goal: web shop orders picked up from SFTP and sent to a D365 recurring data job every 10 minutes.

1. **In D365**: in the import data project choose *Create recurring data job*: name, **Application ID** = the client id of the RIS application (tick *Enabled*), source type *File*, entity, processing schedule (e.g. every minute), *Enabled*. Copy the job **ID** (GUID in braces) – this is the **activity ID**.
2. **In RIS**: type Upload, tick *Also create the monitor job*; activity ID, entity name, company; folders; download from SFTP (server, remote folder, file mask).

   ![Dynamics 365 step of an upload job](img/editor-upload.png)

3. Schedule every 10 minutes, monitor every 5 minutes.

The processing monitor moves the file to *Processed* when D365 has processed the message, or to *ProcessingErrors* (with execution errors) when processing fails.

## Example 4: Download

1. **In D365**: in the export data project choose *Create recurring data job* with the RIS **Application ID** and an export schedule. Copy its ID (activity ID).
2. **In RIS**: type Download, activity ID, download folder, optionally extraction and timestamp, schedule (e.g. `6, 12, 18` on weekdays).

RIS downloads all messages from the queue, saves the packages and **acknowledges each message separately** only after the file is saved – a failed download loses no data (D365 offers the message again).

## Schedule {#schedule}

![Schedule builder with a time window](img/editor-schedule.png)

| Mode | Example | Result |
|---|---|---|
| **Daily / selected days** | 02:00, days Mon–Fri | Monday to Friday at 02:00 |
| **Every N minutes or hours** | every 2 hours from 12:00 | 00:00, 02:00 … 12:00, 14:00 … (rhythm counted from 12:00, a restart does not shift it) |
| **… only between** | every 10 minutes, 08:00–16:00, Mon–Fri | 08:00, 08:10 … 15:50, 16:00 on weekdays (window end inclusive) |
| **Monthly** | day 1 at 06:00 / last day | |
| **Advanced (cron)** | `0 0 8-18 ? * MON-FRI` | Quartz expression (seconds minutes hours day-of-month month day-of-week) |

- **Time zone** – the server's by default; clock times stay fixed across daylight saving changes.
- **Missed runs** – when the service was not running at a scheduled time: *Run once after start* (recommended for exports) or *Skip*.
- **Paused in configuration** – the job is saved but does not run.
- Below the builder: a plain-language description and a table of the next runs.

## Copying and editing many jobs

On the job list select jobs and choose **Bulk edit** (Admin role):

- **Common fields** – D365 instance, app registration (set, or clear = the default of the instance), group, description, time zone, missed runs, polling, retries, pause on error,
- **Schedule** – shift by ±N minutes, same cron or interval, *stagger starts* (next job every N minutes),
- **Replace in paths** – e.g. `\\old-server\share` → `\\new-server\share` in all folders.

Every change shows a **preview of differences** and is validated as a whole; it is saved in one transaction (all or nothing) and recorded in the audit.

![Selected jobs and bulk actions](img/bulk.png)
