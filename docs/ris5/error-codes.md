# RIS 5 error codes

Every error logged by RIS 5 starts with a code, e.g. `[RIS-EXP-001]`. The same code is in the run summary in Quartz history.
Logs contain `JobKey`, `RunId` (Quartz fire instance id) and, where relevant, `ExecutionId`, `MessageId`, `File`.

| Code | Meaning | What to do |
|---|---|---|
| RIS-CFG-001 | Job configuration is invalid | Run `ris validate jobs.json`; the message lists the invalid settings. |
| RIS-CFG-002 | Connection referenced by the job is not defined | Add the connection or fix `options.connection`. |
| RIS-CFG-003 | Folder does not exist or is not accessible | Check the path and permissions of the service account (UNC shares need a domain account). |
| RIS-CFG-004 | Package template is invalid (no Manifest.xml / InputFilePath) | Export a fresh template from D365 Data management. |
| RIS-AUTH-001 | Authentication to Entra ID failed | Check tenant, client id, secret/certificate expiry and app permissions. The message contains the AADSTS code. |
| RIS-HTTP-001 | D365 rejected the request (4xx) | Response body is in the log; typical causes: wrong data project, missing permissions, wrong legal entity. |
| RIS-HTTP-002 | Temporary problem (timeout, throttling, 5xx, network/DNS) | Usually resolves itself (servicing window). Repeated DNS errors: check DNS/network of the server. |
| RIS-HTTP-003 | Unexpected response from D365 | Response is in the log; report if repeated. |
| RIS-EXP-001 | Export did not finish within the timeout (default 12 h) | Check the execution id in D365 Data management → Job history; check batch server load. |
| RIS-EXP-002 | Export ended with Failed/Canceled/Unknown | Open the execution in D365 (execution id in log) and check its log. |
| RIS-EXP-003 | Package URL not available within the timeout | Execution succeeded but package was not produced; check the execution in D365. |
| RIS-EXP-004 | Download of exported package failed | Check network access to Azure blob storage from the server. |
| RIS-IMP-001 | ImportFromPackage request failed | File moved to errors; error report next to it contains D365 response. |
| RIS-IMP-002 | Legal entity cannot be determined from file name/folder, or the company does not exist in D365 (multi-company import; the list of companies is read once a day per environment, reloaded for an unknown company at most every 5 minutes) | Check file naming against `multiCompany` settings; file moved to errors, nothing sent to D365. |
| RIS-IMP-003 | Data project not found in D365 or has no entities (generated envelope) | Check `dataProject` (exact name) and the connection's company access; use "Show project entities" in the job editor. |
| RIS-IMP-004 | Files of a package do not match the entities of the data project (generated envelope) | Name each file after its entity (e.g. `Customer groups.csv`); include a file for every entity or turn on `allowPartialPackages`. The message lists unmatched files and missing entities. |
| RIS-UPL-001 | Enqueue failed | File moved to errors; error report contains D365 response. |
| RIS-DWN-001 | Dequeue failed | Check the recurring data job (activity id) in D365. |
| RIS-DWN-002 | Acknowledgement failed | File is in download errors folder; D365 may offer the message again. |
| RIS-MON-001 | Import/processing did not finish within the timeout | File moved to processing errors; check the execution/message in D365. |
| RIS-MON-002 | Import/processing ended with errors in D365 | File moved to processing errors with error keys / execution errors files and a link (.url) to D365. |
| RIS-FS-001 | File is locked by another process | Left for the next run; if it persists, find the process holding the file. |
| RIS-FS-002 | File operation failed | See message (permissions, disk space). |
| RIS-SFTP-001 | SFTP transfer failed | Check server, credentials, remote folder; message contains the SFTP error. |
| RIS-GEN-001 | Unexpected error | Stack trace is in the log; please report. |
| RIS-GEN-002 | D365 returned a status RIS does not recognise | Check the execution in D365; report the status text. |
