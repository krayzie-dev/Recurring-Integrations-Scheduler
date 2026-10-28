# Installation

## Requirements

- Windows Server 2019 or later (or Windows 10/11 for testing), 64-bit. **No .NET installation needed** – the program carries its own runtime.
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
3. **Import from 4.x** – shown when a RIS 4.x `Schedule.xml` exists on the server. Jobs are imported **paused** (secrets are not imported – set them in Connections).

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

Roles are in `%ProgramData%\RIS5\appsettings.json` (written by the installer):

```json
{
  "Ris": {
    "Security": {
      "Roles": {
        "Admin": [ "S-1-5-32-544", "CONTOSO\\RIS-Admins" ],
        "Operator": [ "CONTOSO\\RIS-Operators" ],
        "Viewer": [ "CONTOSO\\Domain Users" ]
      }
    }
  }
}
```

You can list groups, users or SIDs (`S-1-5-32-544` = local Administrators in every Windows language). Restart the service after a change.

> **Note (UAC):** in a browser running on the server itself the Administrators group is "deny only" in your token. That is why the installer adds the installing user to the administrators. If you see the "No access" page, add your account by name.

## Upgrade

Run the installer of the new version. It stops the service (running jobs are interrupted within seconds and their Execution Id is logged), copies `ris.db` and `quartz.db` to `backup\before-upgrade-<date>`, replaces the program, keeps the settings and service account and starts the service. Database schema changes are applied at startup (with a backup).

## Uninstall

*Settings → Apps → Recurring Integrations Scheduler 5*. The service and firewall rule are removed. The data in `%ProgramData%\RIS5` stays unless you confirm its removal (a silent uninstall never removes data).

## Moving from RIS 4.x

1. Install RIS 5 next to 4.x with **Import from 4.x** (or later: `ris convert-schedule Schedule.xml -o jobs.json` and `ris db import jobs.json --paused`).
2. Enter the secrets in **Connections** (4.x secrets are not migrated).
3. Run single jobs with **Run now** on a test environment and compare results.
4. Pause the jobs in 4.x and resume them in RIS 5 (in bulk on the job list).
