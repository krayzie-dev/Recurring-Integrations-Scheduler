---
name: refresh-manual
description: Refresh the RIS 5 user manual (PL/EN text in src/RIS.Server/wwwroot/manual) and its screenshots from the Contoso demo server after UI or behaviour changes. Use when the user asks to update the help, documentation or screenshots ("odśwież pomoc", "zrzuty", "screenshoty", "dokumentacja"), or when a change touched a page that appears in the manual.
---

# Refresh the manual and its screenshots

The user manual is part of the app (*Pomoc / Help*, `/help`). Source: `src/RIS.Server/wwwroot/manual/{pl,en}/NN-*.md` and
`img/{pl,en}/*.png`. Technical docs: `docs/ris5/*.md` (README, ui, operations, error-codes, parity-4x).
Screenshots come from the **demo server** (fake Contoso data), never from TEST/production or the installed service.

## 1. Decide what to change

- Look at the commits since the screenshots were last refreshed:
  `git log --format="%h %ad %s" --date=short -- src/RIS.Server/wwwroot/manual/img` vs `git log -- src/RIS.Server/Components`.
- Map changed pages to pictures: the page list is at the bottom of `scripts/manual/capture-screenshots.ps1` (name = file in
  `img/<lang>`). A change in the top bar or layout touches every picture: refresh all.
- Text: update **both languages in the same change** (same chapter files, same headings and anchors). Explicit anchors are
  `## Heading {#id}`; links between chapters `[text](06-daily-operations.md#id)`. Also check `docs/ris5/ui.md` and the
  other docs for the same feature.

## 2. Demo data

- Folder: `%LOCALAPPDATA%\RIS5-demo\data` (`ris.db` with Contoso jobs, instances, SFTP servers, access; `quartz.db` with
  history). A pristine copy is `%LOCALAPPDATA%\RIS5-demo\demo-data-*.zip`; restore it if the folder is missing or spoiled.
  There is no generator for `ris.db` - keep the zip.
- **Never** point anything at `C:\ProgramData\RIS5` (the scripts refuse it).
- Refresh the run history so the week and "last 24 h" end now (the server must be stopped - it holds `quartz.db`):

  ```powershell
  .\scripts\manual\start-demo-server.ps1 -Stop
  python -I scripts\manual\seed-demo-history.py "$env:LOCALAPPDATA\RIS5-demo\data\quartz.db"
  ```

  Python: `C:\Users\<user>\.local\bin\python3.14.exe` if `python` is only the Store alias. The seed is deterministic (random
  seed 42) and includes a few typical failures and the "Sales orders upload" job failing right now (dashboard *Needs attention*).

## 3. Demo server

```powershell
.\scripts\manual\start-demo-server.ps1
```

Builds `src/RIS.Server`, runs a copy of the output on http://localhost:5099 with the current Windows account as Admin and
Operator, and the **scheduler delayed by 30 days** - the demo has no real D365, so any run would fail and spoil the
history. (`.claude/launch.json` "ris-demo" has the same delay; never start the demo without it.) `/health` says
"Unhealthy" because of the delay - that is expected. Stop with `-Stop`.

## 4. Screenshots

```powershell
$out = "$env:TEMP\ris-manual-shots"
.\scripts\manual\capture-screenshots.ps1 -Lang pl -OutDir "$out\pl" -Sheet
.\scripts\manual\capture-screenshots.ps1 -Lang en -OutDir "$out\en" -Sheet
# one or a few: -Only maintenance,history
```

Headless Edge over CDP (no Node/Playwright needed), 1440 px wide, light theme, chart style A (Neon). Before each picture the
rendered text is masked: the real account -> `CONTOSO\jan.kowalski` / `CONTOSO\john.smith`, the computer name ->
`RIS-APP01\`, the demo data path -> `C:\ProgramData\RIS5`.

**Review before copying** - open `$out\sheet-pl.png` and `sheet-en.png` (all thumbnails), then the pictures that changed at
full size. Check:
- no real names: user, domain, computer, local paths (Access, Audit and Maintenance pages are the risky ones);
- the right page, tab, step or dialog; `click: ...` lines in the output mean a selector did not match (button text changed?);
- tall pages: the side menu reaches the bottom.

Then copy: `Copy-Item "$out\pl\*.png" src\RIS.Server\wwwroot\manual\img\pl\ -Force` (and `en`).

A **new picture**: add a line to the page list in `capture-screenshots.ps1`, reference it in both languages
(`![alt](img/name.png)`) - `ManualTests` requires the same pictures in both languages.

## 5. Finish

```powershell
dotnet test test\RIS.Server.Tests --filter "FullyQualifiedName~ManualTests"
.\scripts\manual\start-demo-server.ps1 -Stop
```

Commit the text, the pictures and script changes together, then push. Build an installer only when the user asks.

## Pitfalls

- PowerShell variable names ignore case: `$h` and `$H` are the same variable (the screenshot script once compared a value
  with itself - keep names distinct).
- A command blocked by the sandbox is blocked as a whole: if it also stopped a server, check that nothing old still listens
  on 5099 before trusting what you see (`Get-CimInstance Win32_Process -Filter "Name='dotnet.exe'"`).
- Selectors are language-neutral where possible (step numbers, `title` attributes, regexes for PL and EN button texts);
  when a button text changes, update the regex.
- Mixed line endings: the repo uses CRLF (`autocrlf=true`); write markdown with UTF-8 without BOM.
