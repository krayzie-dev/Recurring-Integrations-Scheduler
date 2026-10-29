<#
.SYNOPSIS
    Screenshots for the user manual (src/RIS.Server/wwwroot/manual/img/<lang>), taken with headless Edge from the demo
    server (scripts/manual/start-demo-server.ps1).

.DESCRIPTION
    One headless Edge per language; pages visited one after another, 1440 px wide, light theme, chart style A (Neon).
    The real account, the computer name and the demo data path are replaced in the rendered text before each picture
    (CONTOSO\jan.kowalski / CONTOSO\john.smith, RIS-APP01, C:\ProgramData\RIS5). Pictures go to -OutDir; with -Sheet
    a contact sheet (all thumbnails) is written next to them for a quick review. Copy them to the manual yourself
    after the review.

.EXAMPLE
    .\scripts\manual\capture-screenshots.ps1 -Lang pl -OutDir $env:TEMP\ris-shots\pl -Sheet
    .\scripts\manual\capture-screenshots.ps1 -Lang en -OutDir $env:TEMP\ris-shots\en -Only maintenance,history
#>
param(
    [Parameter(Mandatory)][ValidateSet("pl", "en")][string]$Lang,
    [Parameter(Mandatory)][string]$OutDir,
    [string[]]$Only = @(),
    [int]$Port = 5099,
    # Demo data folder the server runs on: shown as C:\ProgramData\RIS5 in the pictures.
    [string]$Data = (Join-Path $env:LOCALAPPDATA "RIS5-demo\data"),
    [switch]$Sheet
)
$ErrorActionPreference = "Stop"
$edge = "C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe"
$edgeProfile = Join-Path $env:TEMP "ris-edge-manual-$Lang"
# PowerShell variable names ignore case: keep the viewport height apart from the picture height ($h).
$W = 1440; $ViewHeight = 900
$base = "http://localhost:$Port"
New-Item -ItemType Directory -Force $OutDir | Out-Null
$culture = if ($Lang -eq "pl") { "pl-PL" } else { "en-US" }
$fakeUser = if ($Lang -eq "pl") { "jan.kowalski" } else { "john.smith" }

# Texts that must never appear in a manual picture, with their replacements (plain text, case-insensitive).
$pairs = @(
    @($Data.TrimEnd('\'), "C:\ProgramData\RIS5"),
    @("$env:USERDOMAIN\$env:USERNAME", "CONTOSO\$fakeUser"),
    @("$env:COMPUTERNAME\", "RIS-APP01\"),
    @($env:USERNAME, $fakeUser)
)
$pairsJson = ConvertTo-Json -Compress -InputObject @($pairs | ForEach-Object { , @($_[0], $_[1]) })
$mask = @"
(() => { const esc = s => s.replace(/[.*+?^`${}()|[\]\\]/g, '\\`$&');
  const pairs = $pairsJson.map(([f, t]) => [new RegExp(esc(f), 'gi'), t]);
  const walker = document.createTreeWalker(document.body, NodeFilter.SHOW_TEXT); let n, count = 0;
  while ((n = walker.nextNode())) { let t = n.nodeValue; for (const [p, r] of pairs) t = t.replace(p, r); if (t !== n.nodeValue) { n.nodeValue = t; count++; } }
  document.querySelectorAll('input, textarea').forEach(i => { for (const [p, r] of pairs) i.value = i.value.replace(p, r); });
  return count; })()
"@

$process = Start-Process $edge -PassThru -ArgumentList @("--headless=new", "--disable-gpu", "--user-data-dir=$edgeProfile",
    "--auth-server-allowlist=localhost", "--window-size=$W,$ViewHeight", "--remote-debugging-port=9335", "about:blank")
$script:id = 0
try {
    $target = $null
    for ($i = 0; $i -lt 30 -and -not $target; $i++) {
        Start-Sleep -Milliseconds 500
        try { $target = (Invoke-RestMethod "http://127.0.0.1:9335/json") | Where-Object { $_.type -eq "page" } | Select-Object -First 1 } catch { }
    }
    if (-not $target) { throw "Headless Edge did not start." }
    $socket = [System.Net.WebSockets.ClientWebSocket]::new()
    $socket.ConnectAsync([uri]$target.webSocketDebuggerUrl, [Threading.CancellationToken]::None).Wait()

    function Call($method, $params) {
        $script:id++
        $message = @{ id = $script:id; method = $method; params = $params } | ConvertTo-Json -Depth 6 -Compress
        $bytes = [Text.Encoding]::UTF8.GetBytes($message)
        $socket.SendAsync([ArraySegment[byte]]::new($bytes), "Text", $true, [Threading.CancellationToken]::None).Wait()
        $buffer = [byte[]]::new(64MB); $builder = [Text.StringBuilder]::new()
        while ($true) {
            $result = $socket.ReceiveAsync([ArraySegment[byte]]::new($buffer), [Threading.CancellationToken]::None).Result
            [void]$builder.Append([Text.Encoding]::UTF8.GetString($buffer, 0, $result.Count))
            if ($result.EndOfMessage) {
                $response = $builder.ToString() | ConvertFrom-Json
                if ($response.id -eq $script:id) { return $response }
                $builder.Clear() | Out-Null
            }
        }
    }
    function Viewport([int]$height) { Call "Emulation.setDeviceMetricsOverride" @{ width = $W; height = $height; deviceScaleFactor = 1; mobile = $false } | Out-Null }
    function Eval($expression) { (Call "Runtime.evaluate" @{ expression = $expression; awaitPromise = $true; returnByValue = $true }).result.result.value }
    function Go($path) {
        Call "Page.navigate" @{ url = "$base$path" } | Out-Null
        Start-Sleep -Milliseconds 800
        for ($i = 0; $i -lt 40; $i++) {
            if (Eval "!!window.Blazor && document.readyState === 'complete' && document.querySelectorAll('.mud-main-content *').length > 20") { break }
            Start-Sleep -Milliseconds 400
        }
        Start-Sleep -Seconds 2
    }
    function Shot($name, [int]$MaxHeight = 1100) {
        Eval $mask | Out-Null
        $metrics = Call "Page.getLayoutMetrics" @{}
        $h = [Math]::Min([int]$metrics.result.cssContentSize.height, $MaxHeight)
        # Taller pages: grow the viewport, so the side menu (viewport height) reaches the bottom of the picture.
        if ($h -gt $ViewHeight) { Viewport $h; Start-Sleep -Milliseconds 800; Eval $mask | Out-Null }
        $data = (Call "Page.captureScreenshot" @{ format = "png"; captureBeyondViewport = ($h -le $ViewHeight); clip = @{ x = 0; y = 0; width = $W; height = $h; scale = 1 } }).result.data
        [IO.File]::WriteAllBytes((Join-Path $OutDir "$name.png"), [Convert]::FromBase64String($data))
        if ($h -gt $ViewHeight) { Viewport $ViewHeight }
        "saved $name.png ($h px)"
    }
    function Click($js) { $r = Eval $js; Start-Sleep -Seconds 2; if ($r -ne $true -and $r -notmatch '^ok') { "  click: $r" } }
    # Language-neutral selectors.
    $step = { param($n) "(() => { const b = [...document.querySelectorAll('button')].find(x => x.innerText.trim().startsWith('$n.')); if (!b) return 'no step $n'; b.click(); return true })()" }
    $titled = { param($pattern, $index = 0) "(() => { const b = [...document.querySelectorAll('button[title]')].filter(x => /$pattern/.test(x.title))[$index]; if (!b) return 'no $pattern'; b.click(); return true })()" }
    $tab = { param($index) "(() => { const t = [...document.querySelectorAll('.mud-tab')][$index]; if (!t) return 'no tab'; t.click(); return true })()" }
    function Want($name) { $Only.Count -eq 0 -or $Only -contains $name }

    Viewport $ViewHeight
    # Preferences of this Edge profile: language, light theme, chart style A.
    Go "/culture/set?culture=$culture&redirect=/"
    Go "/prefs/set?theme=Light&chart=Neon&redirect=/"

    # name = file in img/<lang>; the second number is the maximum picture height.
    if (Want "dashboard") { Go "/"; Shot "dashboard" 1000 }
    if (Want "jobs") { Go "/jobs"; Shot "jobs" 1000 }
    if (Want "job-overview") { Go "/jobs/Finance/Vendor%20invoices%20import"; Shot "job-overview" 800 }
    if (Want "job-history") { Go "/jobs/Finance/Vendor%20invoices%20import"; Click (& $tab 1); Shot "job-history" 900 }
    if (Want "run-details") { Go "/jobs/Finance/Exchange%20rates"; Click (& $tab 1); Click "(() => { const r = document.querySelector('.mud-table-body tr'); if (!r) return 'no row'; r.click(); return true })()"; Shot "run-details" 900 }
    if (Want "wizard-type") { Go "/jobs/new"; Shot "wizard-type" 1000 }
    if (Want "editor-d365") { Go "/jobs/Finance/Vendor%20invoices%20import/edit"; Click (& $step 2); Shot "editor-d365" 900 }
    if (Want "editor-files") { Go "/jobs/Finance/Vendor%20invoices%20import/edit"; Click (& $step 3); Shot "editor-files" 1000 }
    if (Want "editor-schedule") { Go "/jobs/Finance/Exchange%20rates/edit"; Click (& $step 4); Shot "editor-schedule" 1300 }
    if (Want "editor-summary") { Go "/jobs/Finance/Exchange%20rates/edit"; Click (& $step 6); Shot "editor-summary" 1000 }
    if (Want "editor-upload") { Go "/jobs/Sales/Sales%20orders%20upload/edit"; Click (& $step 2); Shot "editor-upload" 900 }
    if (Want "plan") { Go "/plan"; Shot "plan" 800 }
    if (Want "history") { Go "/history"; Click "(() => { const b = [...document.querySelectorAll('.mud-toggle-item, button')].find(x => /^(TYDZIE.|WEEK)$/i.test(x.innerText.trim())); if (!b) return 'no week'; b.click(); return true })()"; Shot "history" 1000 }
    if (Want "connections") { Go "/connections"; Shot "connections" 500 }
    if (Want "connection-dialog") { Go "/connections"; Click (& $titled "Edyt|Edit"); Shot "connection-dialog" 900 }
    if (Want "sftp-dialog") { Go "/sftp-servers"; Click (& $titled "Edyt|Edit"); Shot "sftp-dialog" 900 }
    if (Want "audit") { Go "/audit"; Click "(() => { const b = document.querySelector('.mud-table-body tr td button'); if (!b) return 'no row'; b.click(); return true })()"; Shot "audit" 700 }
    if (Want "maintenance") { Go "/maintenance"; Click "(() => { const b = [...document.querySelectorAll('button')].find(x => /^(POKA.,? CO|SHOW WHAT)/i.test(x.innerText.trim())); if (!b) return 'no preview'; b.click(); return true })()"; Shot "maintenance" 1500 }
    if (Want "bulk") { Go "/jobs"; Click "(() => { const c = [...document.querySelectorAll('.mud-table-body tr input[type=checkbox]')].slice(0, 3); c.forEach(x => x.click()); return c.length ? true : 'no rows' })()"; Shot "bulk" 600 }
    if (Want "history-filter") {
        Go "/history"
        Click "(() => { const i = document.querySelector('.ris-history-filters input'); if (!i) return 'no filter'; i.focus(); i.value = 'Sales'; i.dispatchEvent(new Event('input', { bubbles: true })); return true })()"
        Click "(() => { const s = [...document.querySelectorAll('.ris-history-filters .mud-select')].find(x => /Wynik|Result/.test(x.innerText)); if (!s) return 'no select'; const t = s.querySelector('.mud-input'); ['mousedown','mouseup','click'].forEach(e => t.dispatchEvent(new MouseEvent(e, { bubbles: true }))); return true })()"
        Click "(() => { const items = [...document.querySelectorAll('.mud-popover-open .mud-list-item')]; const f = items.find(x => /^B.?.?d$|^Fail/i.test(x.innerText)); if (!f) return 'no item: ' + items.map(x => x.innerText).join('|'); f.click(); return true })()"
        Eval "document.body.click()" | Out-Null; Start-Sleep -Seconds 1
        Shot "history-filter" 1200
    }
    if (Want "access") { Go "/access"; Shot "access" 1100 }
    if (Want "access-local") { Go "/access"; Click "(() => { const i = [...document.querySelectorAll('.ris-filter-bar input')].find(x => /DOMEN|DOMAIN/.test(x.placeholder || '')); if (!i) return 'no input'; i.focus(); i.click(); i.value = 'r'; i.dispatchEvent(new Event('input', { bubbles: true })); return true })()"; Shot "access-local" 900 }
    if (Want "app-registrations") { Go "/app-registrations"; Shot "app-registrations" 600 }
    if (Want "app-registration-dialog") { Go "/app-registrations"; Click "(() => { const b = [...document.querySelectorAll('.ris-actions button')][0]; if (!b) return 'no edit'; b.click(); return true })()"; Shot "app-registration-dialog" 1000 }
    if (Want "help") { Go "/help/05-configuring-jobs"; Shot "help" 1400 }
    if (Want "help-faq") { Go "/help/09-faq"; Shot "help-faq" 900 }
}
finally {
    Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
}

if ($Sheet) {
    # All pictures as thumbnails in one image: a quick check for wrong pages, open dialogs and unmasked names.
    Add-Type -AssemblyName System.Drawing
    $files = Get-ChildItem (Join-Path $OutDir "*.png") | Sort-Object Name
    $cols = 5; $tw = 400; $th = 330; $rows = [Math]::Ceiling($files.Count / $cols)
    $bitmap = New-Object System.Drawing.Bitmap ($cols * $tw), ($rows * $th)
    $g = [System.Drawing.Graphics]::FromImage($bitmap); $g.Clear([System.Drawing.Color]::White); $g.InterpolationMode = "HighQualityBicubic"
    $font = New-Object System.Drawing.Font "Segoe UI", 11
    for ($i = 0; $i -lt $files.Count; $i++) {
        $img = [System.Drawing.Image]::FromFile($files[$i].FullName)
        $x = ($i % $cols) * $tw; $y = [Math]::Floor($i / $cols) * $th
        $scale = [Math]::Min(($tw - 8) / $img.Width, ($th - 28) / $img.Height)
        $g.DrawImage($img, $x + 4, $y + 22, [int]($img.Width * $scale), [int]($img.Height * $scale))
        $g.DrawString($files[$i].BaseName, $font, [System.Drawing.Brushes]::Black, $x + 4, $y + 2)
        $img.Dispose()
    }
    $sheetPath = Join-Path (Split-Path $OutDir -Parent) "sheet-$Lang.png"
    $bitmap.Save($sheetPath); $g.Dispose(); $bitmap.Dispose()
    "contact sheet: $sheetPath"
}
