<#
    Helper actions of the RIS 5 installer (Setup\RIS5\RIS5.iss). Exit code 0 = success.
      -Action TestCertificate     -Thumbprint X                 certificate in LocalMachine\My with private key, valid now
      -Action GrantCertificateKey -Thumbprint X -Account A      read access to the private key for the service account
      -Action GrantLogonRight     -Account A                    "Log on as a service" (sc.exe does not grant it)
      -Action EventSource                                       Application log source "Recurring Integrations Scheduler 5"
      -Action StopService                                       stop RIS5 and wait (60 s)
      -Action WaitHealth          -Port P                       wait until http://localhost:P/health answers (90 s); prints the status
#>
param(
    [Parameter(Mandatory)] [string] $Action,
    [string] $Thumbprint,
    [string] $Account,
    [int] $Port = 5080
)

$ErrorActionPreference = "Stop"

function Get-Certificate([string] $thumb) {
    $normalized = ($thumb -replace '[^0-9A-Fa-f]', '').ToUpperInvariant()
    Get-Item "Cert:\LocalMachine\My\$normalized" -ErrorAction SilentlyContinue
}

try {
    switch ($Action) {
        "TestCertificate" {
            $certificate = Get-Certificate $Thumbprint
            if (-not $certificate) { Write-Output "Certificate not found in LocalMachine\My."; exit 2 }
            if (-not $certificate.HasPrivateKey) { Write-Output "Certificate has no private key."; exit 3 }
            if ($certificate.NotAfter -lt (Get-Date)) { Write-Output "Certificate expired on $($certificate.NotAfter)."; exit 4 }
            Write-Output "OK: $($certificate.Subject), valid until $($certificate.NotAfter:d)"
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
            $source = "Recurring Integrations Scheduler 5"
            if (-not [System.Diagnostics.EventLog]::SourceExists($source)) { New-EventLog -LogName Application -Source $source }
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
        default { Write-Output "Unknown action $Action"; exit 9 }
    }
}
catch {
    Write-Output $_.Exception.Message
    exit 1
}
