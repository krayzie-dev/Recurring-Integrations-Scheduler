<#
.SYNOPSIS
    Builds RIS.Server and runs a copy of the build output on http://localhost:5099 with the demo data (Contoso) for the
    manual screenshots. The repo build stays unlocked, so you can keep building while it runs.

.DESCRIPTION
    - The scheduler is delayed by 30 days, so no demo job runs (the demo has no real D365 and runs would fail and spoil
      the seeded history).
    - The current Windows account gets the Admin and Operator roles (only in this process, via command-line settings).
    - Refuses the installed service data (%ProgramData%\RIS5).

.EXAMPLE
    .\scripts\manual\start-demo-server.ps1
    .\scripts\manual\start-demo-server.ps1 -Data D:\ris-demo\data
    .\scripts\manual\start-demo-server.ps1 -Stop
#>
param(
    [string]$Data = (Join-Path $env:LOCALAPPDATA "RIS5-demo\data"),
    [int]$Port = 5099,
    [switch]$Stop
)
$ErrorActionPreference = "Stop"
$repo = Resolve-Path (Join-Path $PSScriptRoot "..\..")
$run = Join-Path $env:TEMP "ris-demo-run"

Get-CimInstance Win32_Process -Filter "Name='dotnet.exe'" | Where-Object CommandLine -like "*ris-demo-run*RIS.Server.dll*" |
    ForEach-Object { Stop-Process -Id $_.ProcessId -Force }
if ($Stop) { "demo server stopped"; return }

if ($Data -like "*ProgramData*") { throw "Refusing to use $Data - never the installed service data." }
if (-not (Test-Path (Join-Path $Data "ris.db"))) { throw "Demo data $Data not found (ris.db). See .claude/skills/refresh-manual/SKILL.md." }

dotnet build (Join-Path $repo "src\RIS.Server") -nologo -v q | Select-String "error" | ForEach-Object { throw "Build failed: $_" }
Remove-Item $run -Recurse -Force -ErrorAction SilentlyContinue
Copy-Item (Join-Path $repo "src\RIS.Server\bin\Debug\net10.0") $run -Recurse

$account = "$env:USERDOMAIN\$env:USERNAME"
Start-Process dotnet -WindowStyle Hidden -ArgumentList @(
    "$run\RIS.Server.dll", "--contentRoot", (Join-Path $repo "src\RIS.Server"), "--Ris:Data:DataDirectory=$Data", "--Urls=http://localhost:$Port",
    "--Ris:Security:Roles:Operator:0=$account", "--Ris:Security:Roles:Admin:1=$account", "--Ris:Console=false", "--Ris:SchedulerStartDelay=30.00:00:00")

# /health is "Unhealthy" while the scheduler waits - any answer means the server is up.
for ($i = 0; $i -lt 90; $i++) {
    try { Invoke-WebRequest "http://localhost:$Port/health" -TimeoutSec 2 -UseBasicParsing | Out-Null; "demo server up on http://localhost:$Port"; return }
    catch { if ($_.Exception.Response) { "demo server up on http://localhost:$Port"; return }; Start-Sleep -Seconds 1 }
}
throw "Demo server did not answer on port $Port - see $Data\logs."
