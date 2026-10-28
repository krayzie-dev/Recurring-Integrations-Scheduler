# First steps: D365 instances

Before you create a job, RIS must be able to sign in to D365. It uses a **Microsoft Entra ID application** (formerly Azure AD) – RIS calls the D365 APIs on behalf of this application.

## 1. Register the application in Entra ID

In the Azure portal: *Microsoft Entra ID → App registrations → New registration*.

1. Name, e.g. `RIS5-PROD`, account type: *Accounts in this organizational directory only*. No redirect URI is needed.
2. Note the **Application (client) ID** and the **Directory (tenant) ID** or the tenant domain (e.g. `contoso.onmicrosoft.com`).
3. Sign-in method – choose one:
   - **Client secret**: *Certificates & secrets → New client secret*. Copy the **Value** column (shown only once, right after creation). Note: the *Secret ID* is not the secret – pasting it fails with `AADSTS7000215`. Note the expiry date.
   - **Certificate**: upload the public key (`.cer`) under *Certificates & secrets → Certificates*. Import the certificate with its private key on the RIS server into `LocalMachine\My`; the service account needs access to the key.
4. API permissions are not required (D365 authorizes the application on its side).

## 2. The application in D365

In D365: *System administration → Setup → Microsoft Entra ID applications* → **New**:

- **Client ID** – the Application (client) ID from step 1,
- **Name** – anything, e.g. `RIS5`,
- **User ID** – the D365 user the integration acts as. It needs access to *Data management* (e.g. the *Data management administrator* role) and to the companies the jobs work with.

For Upload and Download also enter this application in the **Application ID** field of the D365 recurring data job and tick *Enabled* – see [Examples](05-configuring-jobs.md#upload).

## 3. The D365 instance in RIS

*D365 instances → New D365 instance* (Admin role).

![D365 instance form](img/connection-dialog.png)

| Field | Example | Notes |
|---|---|---|
| Name | `CONTOSO-PROD` | Jobs refer to this name; it cannot be changed later. |
| AOS address | `https://contoso-prod.operations.dynamics.com` | The D365 environment URL (https). |
| Tenant | `contoso.onmicrosoft.com` | Tenant domain or id. |
| Client id | `aaaaaaaa-bbbb-…` | Application (client) ID. |
| Sign-in | **Client secret** or **Certificate** | Only the fields of the selected method are shown; the other method's data is removed on save. |
| Client secret | (value) | Write only: never displayed or returned by the API. Empty when editing = keep the stored one. |
| Certificate | pick from list | Certificates with a private key from the server store, with expiry date; you can also paste a thumbprint. |

**Test connection** in the dialog checks the values in the form **before saving** – as the service account it signs in to Entra ID and reads the list of data projects. The result appears below the button.

![D365 instance list with last test](img/connections.png)

In the instance list the **Last test** column shows the result and time of the last test, the ⟲ button in a row tests one instance and **Test all** tests all of them. The number in *Used by jobs* opens the job list filtered by the instance. An instance used by jobs cannot be deleted – RIS shows which jobs use it.

## 4. SFTP servers (optional)

*SFTP servers → New SFTP server*. Sign in with a **password** or a **private key** (path to an OpenSSH key file on the RIS server and an optional key passphrase). **Test connection** signs in and lists the given remote folder.

![SFTP server form](img/sftp-dialog.png)

## 5. Personal settings

In the top bar: 🌐 language (English/Polish), ☀/🌙 theme (light, dark, system), 🕒 time zone for displayed times (server or UTC), ❔ this manual. Your choice is remembered in the browser.
