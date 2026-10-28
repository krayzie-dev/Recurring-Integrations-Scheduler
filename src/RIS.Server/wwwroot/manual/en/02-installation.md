# Installation

## Requirements

- Windows Server 2016 or later (or Windows 10/11 for testing), 64-bit. **No .NET installation needed** – the program carries its own runtime.
- Local administrator rights for the installation.
- Network access from the server to D365 (`https://<environment>.operations.dynamics.com`), to `login.microsoftonline.com` and to Azure Blob storage (data packages).
- For network folders (UNC) – a service account with access to the shares (domain account or gMSA).
- For access from other computers – a server certificate (preferably from your company CA) with a private key in the `LocalMachine\My` store.

## Installing with the wizard

Run `RIS5-Setup-<version>.exe` as administrator. The wizard (English or Polish) has these pages:

1. **Service account**
   - *LocalService* (default) – enough for local folders.
   - *NetworkService* / *LocalSystem* – the computer account on the network.
   - *Domain account or gMSA* – required for network (UNC) folders. The password is checked immediately; a gMSA (`DOMAIN\account$`) has no password. The installer grants the account "Log on as a service".
2. **Access**
   - *HTTP port* (default 5080) – local only (`http://localhost:5080`).
   - *HTTPS port* (e.g. 5443) and *certificate thumbprint* – access from other computers. The installer checks the certificate (exists, has a private key, is valid), grants the service account access to the key and adds the "RIS5 HTTPS" firewall rule.
   - *Administrators* and *Operators* – groups or users separated by `;` (e.g. `DOMAIN\RIS-Admins;DOMAIN\john.smith`). Default: local Administrators and the installing user.
3. **Import from 4.x** – shown when RIS 4.x runs on the server. Jobs are imported **paused**, with their secrets – see [Moving from RIS 4.x](#upgrade4x).

Finally the installer saves the settings, creates the **RIS5** service (delayed automatic start, restart on failure), starts it and checks `/health`. The last page shows the result and the UI address.

> The program goes to `%ProgramFiles%\RIS5` (command line `ris.exe` in `cli\`), data to `%ProgramData%\RIS5`: databases, secret keys, logs, backups and the `appsettings.json` settings file.

## Silent installation (script, GPO)

```powershell
& .\RIS5-Setup-5.0.0.exe /VERYSILENT /SUPPRESSMSGBOXES /LOG=C:\Temp\ris5-setup.log `
  "/ACCOUNT=CONTOSO\svc-ris$" /HTTPPORT=5080 /HTTPSPORT=5443 "/CERT=<thumbprint>" `
  "/ADMINS=CONTOSO\RIS-Admins" "/OPERATORS=CONTOSO\RIS-Operators" /IMPORT4X=0 /TASKS="cliPath"
```

| Parameter | Meaning |
|---|---|
| `/ACCOUNT` | `LocalSystem`, `LocalService`, `NetworkService`, `DOMAIN\user` (with `/PASSWORD=`), `DOMAIN\gmsa$` |
| `/HTTPPORT`, `/HTTPSPORT`, `/CERT` | Ports and HTTPS certificate thumbprint (no HTTPS: omit `/HTTPSPORT` and `/CERT`) |
| `/ADMINS`, `/OPERATORS` | Roles, values separated by `;` – in PowerShell quote the whole argument |
| `/IMPORT4X=0/1` | Import 4.x jobs as paused |
| `/RECONFIGURE=1` | On upgrade: write the settings again (otherwise the existing ones are kept) |
| `/TASKS="cliPath"` | Add `ris.exe` to PATH |

Validation errors (account, port, certificate) end a silent installation with an error – details are in the `/LOG` file.

## HTTPS and certificate

For production use a certificate from your company CA with the server name (and FQDN) in the SAN, imported with the private key into `LocalMachine\My`. For testing a self-signed certificate is enough (the browser shows a warning):

```powershell
$c = New-SelfSignedCertificate -DnsName $env:COMPUTERNAME, localhost -CertStoreLocation Cert:\LocalMachine\My -NotAfter (Get-Date).AddYears(1)
& .\RIS5-Setup-5.0.0.exe /VERYSILENT /SUPPRESSMSGBOXES /RECONFIGURE=1 /HTTPPORT=5080 /HTTPSPORT=5443 "/CERT=$($c.Thumbprint)"
```

Check: `https://<server>:5443/health` returns `Healthy`. After renewing the certificate run the installer again with `/RECONFIGURE=1` and the new thumbprint.

## Domain account or gMSA

Needed when jobs read or write files on network shares.

- **gMSA** (recommended – no password): AD administrators create the account and allow the server to retrieve its password; on the server run `Install-ADServiceAccount svc-ris$` and `Test-ADServiceAccount svc-ris$` (must return `True`), then `/ACCOUNT="CONTOSO\svc-ris$"`.
- **Regular domain account**: enter it in the wizard (the password is checked).
- Share permissions (sharing and NTFS) are granted by the file server administrators. Verify access with **Check access** next to folder fields in the job editor – the test runs as the service account.

## Roles and permissions {#roles}

| Role | Can |
|---|---|
| **Admin** | Everything: jobs, D365 instances, SFTP servers, access, audit, data maintenance. |
| **Operator** | Run, pause and resume jobs (also in bulk), plus everything a Viewer can. |
| **Viewer** | Read only: dashboard, jobs, plan, history, run details, exports. |

**After the installation roles are given on the *Access* page** (Admin role): choose the role, enter a group or user (`DOMAIN\RIS-Operators`, `DOMAIN\john.smith` or a SID) and click **Add**. RIS checks the account in Windows/AD; the change applies at the next page load – no service restart – and is recorded in the audit. Giving roles to **AD groups** and managing access through group membership is easiest. Details: [Access](06-daily-operations.md#access).

> Adding a user to an AD group takes effect after they **sign in to Windows again** – the list of groups is read at sign-in.

**Local groups of the server** – when you cannot create AD groups or want to give access on this server only, create a local group and add domain accounts to it (PowerShell as administrator):

```powershell
New-LocalGroup -Name RIS-Operators -Description "RIS 5 operators"
Add-LocalGroupMember -Group RIS-Operators -Member CONTOSO\john.smith, CONTOSO\Accounting
```

On the *Access* page start typing in the *Group or user* field – it suggests the local groups of the server. You can enter `SERVER\RIS-Operators`, `.\RIS-Operators` or just the name. A change of membership in a local group takes effect after reopening the browser (no need to sign out of Windows). With several RIS servers AD groups are more convenient – one group for all servers.

**Settings file.** Roles given in the installer (`/ADMINS`, `/OPERATORS`) are in `%ProgramData%\RIS5\appsettings.json` and are read-only on the *Access* page (lock icon) – so administrators cannot lock themselves out by accident. A change in the file needs a service restart:

```json
{
  "Ris": {
    "Security": {
      "Roles": {
        "Admin": [ "S-1-5-32-544", "CONTOSO\\RIS-Admins" ],
        "Operator": [ "CONTOSO\\RIS-Operators" ]
      }
    }
  }
}
```

You can list groups, users or SIDs (`S-1-5-32-544` = local Administrators in every Windows language).

**Command line** (on the server, as administrator – for scripts or when nobody has the Admin role any more):

```powershell
ris access list
ris access add Operator "CONTOSO\RIS-Operators"
ris access remove Operator "CONTOSO\RIS-Operators"
```

> **Note (UAC):** in a browser running on the server itself the Administrators group is "deny only" in your token. That is why the installer adds the installing user to the administrators. If you see the "No access" page, add your account by name.

## Upgrade

Run the installer of the new version. It stops the service (running jobs are interrupted within seconds and their Execution Id is logged), copies `ris.db` and `quartz.db` to `backup\before-upgrade-<date>`, replaces the program, keeps the settings and service account and starts the service. Database schema changes are applied at startup (with a backup).

## Uninstall

*Settings → Apps → Recurring Integrations Scheduler 5*. The service and firewall rule are removed. The data in `%ProgramData%\RIS5` stays unless you confirm its removal (a silent uninstall never removes data).

## Moving from RIS 4.x {#upgrade4x}

RIS 5 is installed **next to** RIS 4.x on the same server (separate service, data and ports), and jobs are switched one by one.

**Before the installation**

1. In the RIS 4.x client **save the schedule** (*Save schedule*). RIS 4.x keeps changes made in the client only in the memory of the service – the file on disk may be out of date.
2. Check which account the RIS 4.x service runs as – the installer suggests the same account, because it has access to the integration folders.

**Installation**

3. Run the RIS 5 installer as administrator on the RIS 4.x server. The installer finds the 4.x schedule itself (through the 4.x service and its configuration file, so also in a non-default folder) and shows the **Import from RIS 4.x** page.
4. Jobs are imported **paused**. The 4.x **secrets** (client secrets, SFTP passwords) are decrypted on this server and stored in RIS 5 – they are not written to any file. Import report: `%ProgramData%\RIS5\import-4x\import.log` (warnings and secrets that could not be taken over) and `jobs.json` (configuration without secrets).
5. Silent installation: `/IMPORT4X=1` (default). For a domain account pass `/ACCOUNT` and `/PASSWORD` – without them the account of the 4.x service is used, but a regular domain account needs its password.

**Checks after the installation**

6. *D365 instances* – **Test all**. Enter the missing secrets (listed in the report) manually.
7. *Jobs* (filter *Paused*) – review the jobs. **Start time**: RIS 4.x did not save the start time of "every N minutes/hours" schedules, so RIS 5 uses 00:00 – fix the jobs that should start at another time (in bulk: *Bulk edit → Schedule*).
8. Use **Check access** next to folders and package templates – the test runs as the RIS 5 service account.

**Switching jobs**

9. For every job (the job and its **monitor together**): stop it in RIS 4.x → resume it in RIS 5 (*Resume*) → check the history after the first run. The RIS 5 monitor reads status files written by the 4.x import, so nothing in progress is lost.
10. **Note:** a pause in RIS 4.x does not survive a restart of the 4.x service (e.g. after Windows updates) – the job then runs again, in parallel with RIS 5. After switching, **delete the job in RIS 4.x and save the schedule**; when all jobs are switched, disable the RIS 4.x service (`Set-Service RecurringIntegrationsScheduler -StartupType Disabled`).

Manually (e.g. on another server): `ris db import-4x Schedule.xml --paused` (secrets are taken over only on the server where RIS 4.x encrypted them; elsewhere set them in *D365 instances*).
