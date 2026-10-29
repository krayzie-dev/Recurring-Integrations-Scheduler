# FAQ

### Can RIS 5 run side by side with RIS 4.x?
Yes – it is a separate service (`RIS5`) with separate data. Import the 4.x jobs as paused and switch them one by one: pause in 4.x, resume in RIS 5. Do not run the same job in both versions at the same time.

### Where are secrets stored and who can see them?
Encrypted in `ris.db` with the keys from `%ProgramData%\RIS5\keys`. They are never shown in the UI, returned by the API or written to the audit and logs. They can only be overwritten.

### How do I change an expiring secret?
*App registrations → edit the app registration → paste the new value in Client secret → Test → Save*. The secret is set **once** – from their next run every D365 instance and job that uses this app registration (column *Used by*) uses it. The audit shows `*** (new value, hidden)`.

### Can a job sign in with another application than its D365 instance?
Yes. In the job wizard, step *Dynamics 365*, choose the **App registration** field – empty means the default app registration of the instance. The same in *Bulk edit* (set or clear for many jobs). The import from 4.x does it for jobs that used another application than the other jobs of their instance.

### What happens when the server is down at a scheduled time?
After start the service detects the missed run: with *Run once after start* it runs the job once, with *Skip* it waits for the next scheduled time. The schedule rhythm does not shift.

### A job runs every 2 hours from 12:00 – does it start counting again after a restart?
No. The rhythm comes from the schedule (12:00, 14:00…), not from the moment the service started.

### How do I run a job only during working hours?
Schedule *Every N minutes or hours* → switch *Only between* (e.g. 08:00–16:00) and select weekdays. The window end is inclusive.

### How do I import several entities (e.g. headers and lines) in one package?
A D365 data project with those entities, and in RIS the *Generated* envelope. The source system writes a **ZIP** to the input folder with files named after the entities (`Sales order headers V2.csv`, `Sales order lines V2.csv`). RIS orders them as in the project. *Show project entities* suggests the file names.

### Do I need a package template like in 4.x?
No, unless the data project has its own mapping, transformations or default values in D365. Then choose *From a package template (4.x)*.

### Why did a D365 recurring data job start loading data incorrectly after a RIS import?
An import with *Overwrite data project* replaces the project mapping. Use separate data projects for RIS jobs and for recurring data jobs.

### How do I check that the service account can access a network folder?
In the job editor next to the folder field use **Check access** – the test creates and deletes a probe file as the service account and shows the account name.

### How do I pause all jobs during D365 maintenance?
Job list → select all (or filter by D365 instance) → *Pause* with a reason → after the maintenance *Resume*. A pause survives a service restart.

### What is the difference between "Paused" in the job form and Pause in the job list?
None – it is one state stored in the job configuration. The list button, the form switch, bulk edit, *Pause on error* and the REST API all change it. Every pause and resume is audited (who, when, reason), and a later change of the job does not undo it. The 4.x option "IndefinitePause" (never run) is imported as a normal pause.

### How long is run history kept?
30 days by default (failed runs 90 days), at most 1000 runs per job. Change it in `Ris:Retention` – see [Maintenance](07-maintenance.md).

### How do I access RIS from another computer?
Enable HTTPS in the installer (`/RECONFIGURE=1 /HTTPSPORT=5443 /CERT=<thumbprint>`) and open `https://<server>:5443`. HTTP works only locally on the server.

### How do I add a new operator?
*Access* → role **Operator** → enter the user or (better) an AD group → **Add**. It applies at the next page load, without a restart. Managing access through AD group membership is easiest – see [Roles and permissions](02-installation.md#roles).

### Can I automate operations (scripts, CI)?
Yes – the REST API `/api/v1` (Windows sign-in, same roles): job list, run, pause, history, configuration export. Description: `/openapi/v1.json` on the server.

### The page shows "Lost connection to the RIS server".
The service was restarted or upgraded. Once it is up the page reloads by itself; if not, refresh it (F5).

### Event log entries are in the Application log, not RIS5.
After an upgrade from an older version Windows applies the move of the source to the RIS5 log only after a computer restart.

### How do I report a problem?
Include the error code, Execution Id and an excerpt of the job log (`logs\jobs\<job>\`) – secrets never go into logs, so they are safe to share.
