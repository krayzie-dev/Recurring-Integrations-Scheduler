# Job types

| Type | Direction | What it does | D365 API |
|---|---|---|---|
| **Export** | D365 → folder | Starts a data project export, waits for completion, downloads the package (optionally extracts it, adds a timestamp, sends it to SFTP). | Package API (`ExportToPackage`) |
| **Import** | folder → D365 | Packages the files (or takes ready packages) and imports them into a data project. Writes a status file for the monitor. | Package API (`ImportFromPackage`) |
| **Execution monitor** | – | Checks the result of imports in D365 and moves files to the processed or errors folder (with error keys and a link to D365). | Package API |
| **Upload** | folder → D365 | Sends files to a D365 recurring data job (queue). Writes a status file for the monitor. | Recurring Integrations (`enqueue`) |
| **Processing monitor** | – | Checks whether D365 processed the uploaded messages; moves files like the execution monitor. | Recurring Integrations |
| **Download** | D365 → folder | Downloads packages published by a D365 export recurring data job (`dequeue`), saves them and acknowledges them (`ack`). | Recurring Integrations |

## Which type to use?

- **Export data from D365 at a fixed time** (reports, interfaces to other systems) → **Export**. RIS starts the export and downloads the result.
- **Import files into D365 with result checking** → **Import + Execution monitor** (the wizard creates the pair with one switch).
- **D365 already has a recurring data job** → **Upload + Processing monitor** (import) or **Download** (export). Processing is scheduled in D365; RIS only delivers or picks up files.

## Folders and status files

File jobs work on folders – example of an Import + monitor pair:

```
D:\Integrations\VendorInvoices\
  In\                 ← the source system writes files here (InputDir)
  Uploaded\           ← file sent to D365 + .Status file for the monitor (UploadSuccessDir)
  UploadErrors\       ← D365 rejected the file on upload (+ .Status error report)
  Processed\          ← monitor: import succeeded (ProcessingSuccessDir)
  ProcessingErrors\   ← monitor: import failed (+ error keys, execution log, .url link to D365)
```

- The **status file** (`.Status`, JSON) links the import or upload job with its monitor: it holds the Execution Id or Message Id and the submission time. It is compatible with RIS 4.x.
- Subfolders of the input folder are preserved in the target folders.
- A failure of one file does not stop the others – each file is processed and reported separately.
- Paths can be local (`D:\…`) or network (`\\server\share\…` – requires a domain account or gMSA).

## Job pairs (job + monitor)

An import or upload job and its monitor point to each other (*Paired job*). The monitor runs more often (e.g. every 5 minutes) and works on the *Uploaded* folder of its parent job. The wizard sets this up automatically – see [Configuring jobs](05-configuring-jobs.md).
