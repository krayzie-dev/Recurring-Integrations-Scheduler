<#
.SYNOPSIS
    Builds the RIS 5 installer: self-contained publish of the server and the ris CLI, then Inno Setup.

.EXAMPLE
    .\scripts\build-installer.ps1 -Version 5.0.0
.EXAMPLE
    .\scripts\build-installer.ps1 -Version 5.0.0-ci.42 -Output artifacts   # CI
#>
[CmdletBinding()]
param(
    [string] $Version = "5.0.0-local",
    [string] $Configuration = "Release",
    [string] $Output = "artifacts",
    [string] $Iscc
)

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$output = if ([IO.Path]::IsPathRooted($Output)) { $Output } else { Join-Path $root $Output }
$publish = Join-Path $output "publish"

# Numeric file version for Windows: 5.0.0-ci.42 -> 5.0.0.42, 5.0.0 -> 5.0.0.0
$base = ($Version -split '[-+]')[0]
$build = if ($Version -match '\.(\d+)$' -and $Version -ne $base) { $Matches[1] } else { "0" }
$numeric = "$base.$build"

Write-Host "RIS 5 installer $Version ($numeric)"

# The installer runs its helper with Windows PowerShell 5.1: a syntax error there breaks every helper action silently.
foreach ($script in @("Setup\RIS5\setup-helpers.ps1")) {
    $errors = $null
    [System.Management.Automation.Language.Parser]::ParseFile((Join-Path $root $script), [ref]$null, [ref]$errors) | Out-Null
    if ($errors) { throw "$script has syntax errors: $($errors | ForEach-Object { "line $($_.Extent.StartLineNumber): $($_.Message)" })" }
    $legacy = & "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -Command `
        "`$e = `$null; [System.Management.Automation.Language.Parser]::ParseFile('$(Join-Path $root $script)', [ref]`$null, [ref]`$e) | Out-Null; `$e.Count"
    if ("$legacy".Trim() -ne "0") { throw "$script has syntax errors in Windows PowerShell 5.1." }
}
# Setup runs the helper through cmd.exe, so it inherits the module path of a PowerShell 7 session (see the helper). An unknown
# thumbprint must give "not found" (2), not a failure to read the certificate store (1).
$helper = Join-Path $root "Setup\RIS5\setup-helpers.ps1"
$check = & "$env:SystemRoot\System32\cmd.exe" /c "`"$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe`" -NoProfile -ExecutionPolicy Bypass -File `"$helper`" -Action TestCertificate -Thumbprint 00"
if ($LASTEXITCODE -ne 2) { throw "setup-helpers.ps1 TestCertificate: expected exit code 2, got $LASTEXITCODE ($check)." }
Remove-Item $publish -Recurse -Force -ErrorAction SilentlyContinue

foreach ($project in @(
        @{ Path = "src\RIS.Server\RIS.Server.csproj"; Target = "server" },
        @{ Path = "src\RIS.Cli\RIS.Cli.csproj"; Target = "cli" })) {
    dotnet publish (Join-Path $root $project.Path) -c $Configuration -r win-x64 --self-contained `
        -p:Version=$Version -p:FileVersion=$numeric -p:DebugType=none -o (Join-Path $publish $project.Target) --nologo
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish $($project.Path) failed." }
}

# Sanity check of the published server (UI assets must be there, otherwise the UI is unstyled).
foreach ($file in @("RIS.Server.exe", "appsettings.json", "wwwroot\css\ris.css")) {
    if (-not (Test-Path (Join-Path $publish "server\$file"))) { throw "Published server is missing $file." }
}
if (-not (Get-ChildItem (Join-Path $publish "server\wwwroot\_content\MudBlazor") -Filter "MudBlazor.min*.css" -ErrorAction SilentlyContinue)) {
    throw "Published server is missing MudBlazor static assets."
}

if (-not $Iscc) {
    $Iscc = @(
        (Get-Command iscc.exe -ErrorAction SilentlyContinue).Source,
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
        "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
    ) | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
}
if (-not $Iscc) { throw "Inno Setup 6 (ISCC.exe) not found. Install it (winget install JRSoftware.InnoSetup) or pass -Iscc." }

& $Iscc /Q "/DAppVersion=$Version" "/DAppVersionNumeric=$numeric" "/DPublishDir=$publish" "/DOutputDir=$output" (Join-Path $root "Setup\RIS5\RIS5.iss")
if ($LASTEXITCODE -ne 0) { throw "Inno Setup failed." }

$installer = Join-Path $output "RIS5-Setup-$Version.exe"
$hash = (Get-FileHash $installer -Algorithm SHA256).Hash.ToLowerInvariant()
"$hash  RIS5-Setup-$Version.exe" | Set-Content "$installer.sha256" -Encoding ascii
Compress-Archive -Path (Join-Path $publish "cli\*") -DestinationPath (Join-Path $output "ris-cli-$Version-win-x64.zip") -Force
Write-Host "Installer: $installer"
Write-Host "SHA256:    $hash"
