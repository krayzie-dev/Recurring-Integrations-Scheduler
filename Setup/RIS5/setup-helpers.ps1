<#
    Helper actions of the RIS 5 installer (Setup\RIS5\RIS5.iss). Exit code 0 = success.
      -Action TestCertificate     -Thumbprint X                 certificate in LocalMachine\My with private key, valid now
      -Action GrantCertificateKey -Thumbprint X -Account A      read access to the private key for the service account
      -Action GrantLogonRight     -Account A                    "Log on as a service" (sc.exe does not grant it)
      -Action EventSource                                       Application log source "Recurring Integrations Scheduler 5"
      -Action StopService                                       stop RIS5 and wait (60 s)
      -Action WaitHealth          -Port P                       wait until http://localhost:P/health answers (90 s); prints the status
      -Action Find4x                                            RIS 4.x on this server: prints SCHEDULE=<Schedule.xml>, ACCOUNT=<service account>
                                                                (exit 2 when no schedule is found)
#>
param(
    [Parameter(Mandatory)] [string] $Action,
    [string] $Thumbprint,
    [string] $Account,
    [int] $Port = 5080
)

$ErrorActionPreference = "Stop"

# Setup started from PowerShell 7 passes its PSModulePath on (pwsh only fixes it when it starts powershell.exe itself), so
# Windows PowerShell would autoload the PS7 modules, fail, and e.g. have no Cert: drive. Use the Windows PowerShell paths only.
$env:PSModulePath = "$env:ProgramFiles\WindowsPowerShell\Modules;$env:windir\system32\WindowsPowerShell\v1.0\Modules"

# .NET store instead of the Cert: drive: does not depend on any PowerShell module.
function Get-Certificate([string] $thumb) {
    $normalized = ($thumb -replace '[^0-9A-Fa-f]', '').ToUpperInvariant()
    $store = [System.Security.Cryptography.X509Certificates.X509Store]::new("My", "LocalMachine")
    try {
        $store.Open("ReadOnly, OpenExistingOnly")
        $store.Certificates.Find("FindByThumbprint", $normalized, $false) | Select-Object -First 1
    }
    finally { $store.Close() }
}

try {
    switch ($Action) {
        "TestCertificate" {
            $certificate = Get-Certificate $Thumbprint
            if (-not $certificate) { Write-Output "Certificate not found in LocalMachine\My."; exit 2 }
            if (-not $certificate.HasPrivateKey) { Write-Output "Certificate has no private key."; exit 3 }
            if ($certificate.NotAfter -lt (Get-Date)) { Write-Output "Certificate expired on $($certificate.NotAfter)."; exit 4 }
            Write-Output "OK: $($certificate.Subject), valid until $($certificate.NotAfter.ToString("d"))"
            exit 0
        }
        "GrantCertificateKey" {
            $certificate = Get-Certificate $Thumbprint
            $key = [System.Security.Cryptography.X509Certificates.RSACertificateExtensions]::GetRSAPrivateKey($certificate)
            if (-not $key) { $key = [System.Security.Cryptography.X509Certificates.ECDsaCertificateExtensions]::GetECDsaPrivateKey($certificate) }
            $name = if ($key -is [System.Security.Cryptography.RSACng] -or $key -is [System.Security.Cryptography.ECDsaCng]) { $key.Key.UniqueName } else { $key.CspKeyContainerInfo.UniqueKeyContainerName }
            $paths = @("$env:ProgramData\Microsoft\Crypto\Keys\$name", "$env:ProgramData\Microsoft\Crypto\RSA\MachineKeys\$name")
            $file = $paths | Where-Object { Test-Path $_ } | Select-Object -First 1
            if (-not $file) { Write-Output "Private key file not found."; exit 2 }
            & icacls.exe $file /grant "$($Account):R" | Out-Null
            exit $LASTEXITCODE
        }
        "GrantLogonRight" {
            Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
using System.Security.Principal;
public static class RisLsa {
    [StructLayout(LayoutKind.Sequential)] struct LSA_UNICODE_STRING { public ushort Length; public ushort MaximumLength; public IntPtr Buffer; }
    [StructLayout(LayoutKind.Sequential)] struct LSA_OBJECT_ATTRIBUTES { public int Length; public IntPtr RootDirectory; public IntPtr ObjectName; public uint Attributes; public IntPtr SecurityDescriptor; public IntPtr SecurityQualityOfService; }
    [DllImport("advapi32.dll")] static extern uint LsaOpenPolicy(IntPtr system, ref LSA_OBJECT_ATTRIBUTES attributes, uint access, out IntPtr handle);
    [DllImport("advapi32.dll")] static extern uint LsaAddAccountRights(IntPtr policy, byte[] sid, LSA_UNICODE_STRING[] rights, uint count);
    [DllImport("advapi32.dll")] static extern uint LsaClose(IntPtr handle);
    [DllImport("advapi32.dll")] static extern int LsaNtStatusToWinError(uint status);
    public static int Grant(string account, string right) {
        var sid = (SecurityIdentifier)new NTAccount(account).Translate(typeof(SecurityIdentifier));
        var bytes = new byte[sid.BinaryLength]; sid.GetBinaryForm(bytes, 0);
        var attributes = new LSA_OBJECT_ATTRIBUTES();
        IntPtr policy;
        var status = LsaOpenPolicy(IntPtr.Zero, ref attributes, 0x00000800 | 0x00000010, out policy);
        if (status != 0) return LsaNtStatusToWinError(status);
        var name = new LSA_UNICODE_STRING { Buffer = Marshal.StringToHGlobalUni(right), Length = (ushort)(right.Length * 2), MaximumLength = (ushort)((right.Length + 1) * 2) };
        try { return LsaNtStatusToWinError(LsaAddAccountRights(policy, bytes, new[] { name }, 1)); }
        finally { Marshal.FreeHGlobal(name.Buffer); LsaClose(policy); }
    }
}
"@
            $result = [RisLsa]::Grant($Account, "SeServiceLogonRight")
            if ($result -ne 0) { Write-Output "LsaAddAccountRights failed: $result"; exit 2 }
            exit 0
        }
        "EventSource" {
            # Own log "RIS5" under Applications and Services Logs (5.0.0-local.13 and older registered the source in Application).
            $source = "Recurring Integrations Scheduler 5"
            $logName = "RIS5"
            if ([System.Diagnostics.EventLog]::SourceExists($source) -and [System.Diagnostics.EventLog]::LogNameFromSourceName($source, ".") -ne $logName) {
                [System.Diagnostics.EventLog]::DeleteEventSource($source)
            }
            if (-not [System.Diagnostics.EventLog]::SourceExists($source)) {
                [System.Diagnostics.EventLog]::CreateEventSource([System.Diagnostics.EventSourceCreationData]::new($source, $logName))
            }
            # 20 MB, oldest entries overwritten: the log never fills up.
            $log = [System.Diagnostics.EventLog]::new($logName)
            $log.MaximumKilobytes = 20480
            $log.ModifyOverflowPolicy([System.Diagnostics.OverflowAction]::OverwriteAsNeeded, 0)
            exit 0
        }
        "StopService" {
            $service = Get-Service RIS5 -ErrorAction SilentlyContinue
            if ($service -and $service.Status -ne "Stopped") {
                Stop-Service RIS5 -Force -ErrorAction SilentlyContinue
                $service.WaitForStatus("Stopped", [TimeSpan]::FromSeconds(60))
            }
            exit 0
        }
        "WaitHealth" {
            $deadline = (Get-Date).AddSeconds(90)
            while ((Get-Date) -lt $deadline) {
                try {
                    $response = Invoke-WebRequest "http://localhost:$Port/health" -UseBasicParsing -TimeoutSec 5
                    Write-Output $response.Content
                    exit 0
                }
                catch {
                    if ($_.Exception.Response) { Write-Output "HTTP $([int]$_.Exception.Response.StatusCode)"; exit 0 }
                    Start-Sleep -Seconds 2
                }
            }
            Write-Output "No answer"
            exit 1
        }
        "Find4x" {
            # Where the 4.x service really reads its schedule: service -> folder -> quartz.plugin.xml.fileNames of its .exe.config
            # ("~/" = folder of the service). Default install folders when there is no service.
            $candidates = @()
            $service = Get-CimInstance Win32_Service -Filter "Name='RecurringIntegrationsScheduler'" -ErrorAction SilentlyContinue
            if ($service) {
                $exe = if ($service.PathName -match '^\s*"([^"]+)"') { $Matches[1] } else { ($service.PathName -split '\s+')[0] }
                $folder = Split-Path $exe -Parent
                Write-Output "ACCOUNT=$($service.StartName)"
                Write-Output "STATE=$($service.State)"
                $config = "$exe.config"
                if (Test-Path $config) {
                    $entry = ([xml](Get-Content $config -Raw)).SelectNodes("//add[@key='quartz.plugin.xml.fileNames']") | Select-Object -First 1
                    if ($entry) {
                        foreach ($name in ($entry.value -split ',')) {
                            $name = $name.Trim()
                            if ($name.StartsWith("~")) { $name = Join-Path $folder $name.TrimStart('~', '/', '\') }
                            elseif (-not [IO.Path]::IsPathRooted($name)) { $name = Join-Path $folder $name }
                            $candidates += $name
                        }
                    }
                }
                $candidates += Join-Path $folder "Schedule.xml"
            }
            $candidates += "${env:ProgramFiles(x86)}\Recurring Integrations Scheduler\Schedule.xml", "$env:ProgramFiles\Recurring Integrations Scheduler\Schedule.xml"
            $schedule = $candidates | Where-Object { $_ -and (Test-Path $_ -PathType Leaf) } | Select-Object -First 1
            if (-not $schedule) { Write-Output "No RIS 4.x schedule found."; exit 2 }
            Write-Output "SCHEDULE=$schedule"
            exit 0
        }
        default { Write-Output "Unknown action $Action"; exit 9 }
    }
}
catch {
    Write-Output $_.Exception.Message
    exit 1
}
