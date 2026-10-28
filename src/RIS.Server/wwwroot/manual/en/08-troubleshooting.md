# Troubleshooting

## Where to look

1. **Run details** – summary with the error code, metrics (Execution Id) and run log.
2. **The job's error folder** – next to the file there is a `.Status` report with the D365 message and the full response.
3. **Job log** – `%ProgramData%\RIS5\logs\jobs\<Group.Job>\`.
4. **D365** – *Data management → Job history* (search by Execution Id); for recurring data jobs *Manage recurring data jobs*.

## Common situations

| Symptom | Cause and fix |
|---|---|
| `RIS-AUTH-001` … `AADSTS7000215: Invalid client secret` | The *Secret ID* was pasted instead of the secret **value**, or the secret expired. Create a new secret, paste the *Value* column in the D365 instance and use **Test connection**. |
| `RIS-AUTH-001` … `AADSTS700016: Application … was not found` | Wrong client id or tenant in the D365 instance. |
| `RIS-HTTP-001` 401/403 calling D365 | The application is not registered in D365 (*Microsoft Entra ID applications*) or its user lacks data management or company permissions. |
| `RIS-IMP-001` … *exists and overwrite has not been specified* | The data project exists in D365 – turn on **Overwrite data project** in the job. |
| After a RIS import a D365 recurring data job loads data into wrong fields | The overwrite import changed the project mapping. Use separate data projects for RIS and for recurring jobs; importing a file with the full column set restores the mapping. |
| `RIS-IMP-004` | Package files do not match the project entities – the message lists missing entities and unknown files. Name the files after the entities. |
| `RIS-IMP-002` … *does not exist in D365* | Multi-company import: the company code from the file or folder name does not exist in D365. Fix the file/folder name and move the file from the error folder back to the input folder. |
| `RIS-IMP-003` | The data project does not exist (check the exact name) or has no enabled entities. |
| `RIS-EXP-001` | The export did not finish within the time limit (12 h by default). Check the execution in D365 by Execution Id and the batch server load. |
| `RIS-HTTP-002` (503, 504, DNS) | D365 temporarily unavailable (maintenance window) or a network/DNS problem on the server. Usually passes; report repeated DNS errors to the network administrators. |
| `RIS-SFTP-001` | SFTP server unreachable, wrong credentials or remote folder. Use **Test connection** in *SFTP servers*. |
| `RIS-CFG-003` / *Check access* fails for `\\server\…` | The service account has no access to the share – built-in accounts (LocalService) have no network access. Use a domain account or gMSA. |
| `RIS-CFG-001` at startup, "key not found" | `keys\` missing or different (e.g. database moved to another server) – set the secrets again. |
| "No access" page | Your account has no role. An Admin adds it (or its group) on the *Access* page; when nobody has the Admin role – `ris access add Admin "DOMAIN\account"` on the server. After being added to a new AD group, sign out of Windows and in again. On the server itself the Administrators group is not enough (UAC). |
| "Lost connection to the RIS server" dialog | The service was restarted (e.g. upgrade). The page reloads by itself once the service is up. |
| Installer: "Certificate not found in LocalMachine\My" | Thumbprint typed with spaces or from another store – the certificate must be in *Local Computer → Personal* and have a private key. |
| Event log entries appear in *Application* | After an upgrade Windows moves the source to the RIS5 log only after a computer restart. |
| Installer from PowerShell: `ParserError` at `/VERYSILENT` | Two commands pasted on one line; run them separately. |

## Error codes

Every error starts with a code, e.g. `[RIS-EXP-001]`. The same code is in the run summary, in the log and (as the event id) in the event log.

| Code | Meaning | What to do |
|---|---|---|
| RIS-CFG-001 | Invalid configuration | The message lists the invalid settings (`ris validate`). |
| RIS-CFG-002 | The job refers to a missing D365 instance | Add the instance or fix the job. |
| RIS-CFG-003 | Folder missing or not accessible | Check the path and the service account permissions. |
| RIS-CFG-004 | Invalid package template | Export a new template from D365 or use the generated envelope. |
| RIS-AUTH-001 | Entra ID sign-in failed | Tenant, client id, secret/certificate (expiry); AADSTS code in the message. |
| RIS-HTTP-001 | D365 rejected the request (4xx) | The D365 reason is in the message (project, permissions, company). |
| RIS-HTTP-002 | Transient problem (5xx, timeout, network) | Usually passes; if repeated check network/DNS. |
| RIS-HTTP-003 | Unexpected D365 response | The response is in the log; report it if it repeats. |
| RIS-EXP-001…004 | Export: timeout / failed in D365 / no package / download | Check the execution in D365 by Execution Id; for 004 – access to Azure Blob. |
| RIS-IMP-001…004 | Import: request rejected / missing company / missing project / files don't match entities | See the table above. |
| RIS-UPL-001 | Upload to the queue failed | The error report is next to the file in the error folder. |
| RIS-DWN-001/002 | Download / acknowledge failed | Check the recurring data job (activity ID) in D365; the message returns to the queue. |
| RIS-MON-001/002 | Import/processing did not finish in time / finished with errors | File in *ProcessingErrors* with error keys and a `.url` link to D365. |
| RIS-FS-001/002 | File locked / file operation failed | A locked file waits for the next run. |
| RIS-SFTP-001 | SFTP error | Server, credentials, remote folder. |
| RIS-GEN-001/002 | Unexpected error / unknown D365 status | Details in the log – report it. |
