#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Installs RIS 5 server as a Windows service (for testing; the installer comes in stage 5).

.DESCRIPTION
    - publishes src/RIS.Server to -InstallDirectory (unless -SkipPublish),
    - creates the Event Log source "Recurring Integrations Scheduler 5" in its own log RIS5,
    - creates the data directory (default %ProgramData%\RIS5) and grants the service account Modify rights,
    - creates (or updates) the service "RIS5" running as -Account and starts it.

    The service listens on http://localhost:5080 (appsettings.json "Urls"). Import configuration with
    "ris db import jobs.json" and set secrets with "ris db set-secret".

.EXAMPLE
    .\scripts\install-service.ps1 -Account "CONTOSO\svc-ris$"                      # gMSA
.EXAMPLE
    .\scripts\install-service.ps1 -Account "CONTOSO\svc-ris" -Password (Read-Host -AsSecureString)
.EXAMPLE
    .\scripts\install-service.ps1 -Uninstall
#>
[CmdletBinding()]
param(
    [string] $Account = "NT AUTHORITY\LocalService",
    [securestring] $Password,
    [string] $InstallDirectory = "$env:ProgramFiles\RIS5",
    [string] $DataDirectory = "$env:ProgramData\RIS5",
    [string] $ServiceName = "RIS5",
    [switch] $SkipPublish,
    [switch] $Uninstall
)

$ErrorActionPreference = "Stop"
$eventSource = "Recurring Integrations Scheduler 5"

if ($Uninstall) {
    if (Get-Service $ServiceName -ErrorAction SilentlyContinue) {
        Stop-Service $ServiceName -ErrorAction SilentlyContinue
        sc.exe delete $ServiceName | Out-Null
        Write-Host "Service $ServiceName removed. Data in $DataDirectory was kept."
    }
    return
}

if (-not $SkipPublish) {
    $project = Join-Path $PSScriptRoot "..\src\RIS.Server\RIS.Server.csproj"
    dotnet publish $project -c Release -r win-x64 --self-contained -o $InstallDirectory
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed." }
}
$exe = Join-Path $InstallDirectory "RIS.Server.exe"
if (-not (Test-Path $exe)) { throw "$exe not found." }

if ([System.Diagnostics.EventLog]::SourceExists($eventSource) -and [System.Diagnostics.EventLog]::LogNameFromSourceName($eventSource, ".") -ne "RIS5") {
    [System.Diagnostics.EventLog]::DeleteEventSource($eventSource)
}
if (-not [System.Diagnostics.EventLog]::SourceExists($eventSource)) {
    New-EventLog -LogName RIS5 -Source $eventSource
    Write-Host "Event Log source '$eventSource' created."
}

New-Item -ItemType Directory -Force $DataDirectory | Out-Null
# Service account: modify rights on data (databases, keys, logs, backups). Keys are protected with DPAPI (machine),
# so the directory ACL is what keeps other users away from them.
icacls $DataDirectory /grant "$($Account):(OI)(CI)M" | Out-Null
if ($DataDirectory -ne "$env:ProgramData\RIS5") {
    Write-Warning "Set Ris:Data:DataDirectory = '$DataDirectory' in $InstallDirectory\appsettings.json."
}

$existing = Get-Service $ServiceName -ErrorAction SilentlyContinue
if ($existing) {
    Stop-Service $ServiceName
    sc.exe config $ServiceName binPath= "`"$exe`"" obj= $Account | Out-Null
} else {
    sc.exe create $ServiceName binPath= "`"$exe`"" start= delayed-auto obj= $Account DisplayName= "Recurring Integrations Scheduler 5" | Out-Null
    sc.exe description $ServiceName "Runs D365 F&O data management jobs (RIS 5). API and health: http://localhost:5080" | Out-Null
    # Restart after a crash: 1 min, 5 min, then every 15 min.
    sc.exe failure $ServiceName reset= 86400 actions= restart/60000/restart/300000/restart/900000 | Out-Null
}
if ($Password) {
    $plain = [System.Net.NetworkCredential]::new("", $Password).Password
    sc.exe config $ServiceName password= $plain | Out-Null
}
if ($LASTEXITCODE -ne 0) { throw "sc.exe failed with exit code $LASTEXITCODE." }

Start-Service $ServiceName
Write-Host "Service $ServiceName runs as $Account. Health: http://localhost:5080/health"
