# RIS 5 build, CI and releases

## Local build of the installer

Requirements: .NET 10 SDK, Inno Setup 6 (`winget install JRSoftware.InnoSetup`).

```powershell
.\scripts\build-installer.ps1 -Version 5.0.0          # artifacts\RIS5-Setup-5.0.0.exe (+ .sha256, ris-cli-5.0.0-win-x64.zip)
```

The script publishes `RIS.Server` and `RIS.Cli` self-contained for win-x64, checks that the UI assets are included and
compiles `Setup\RIS5\RIS5.iss` (keep it UTF-8 with BOM: it contains Polish texts).

## GitHub Actions

| Workflow | When | What |
|---|---|---|
| `ris5-ci.yml` (RIS 5 CI) | push to `master` / `feature/**`, pull requests, manual – only when RIS 5 files change | Release build, tests (without category `Integration`, results as `test-results` artefact), installer `5.0.0-ci.<run>` with SHA256 and CLI zip as artefact |
| `ris5-release.yml` (RIS 5 release) | tag `v5.x.y` or `v5.x.y-suffix` | Build with the tag version, tests, installer, GitHub release with generated notes (prerelease for suffixed tags) |

The 4.x solution is not built by these workflows.

## Release

1. Update `Version` in `src/Directory.Build.props` (base version of CI builds) and the docs if needed; merge to `master`.
2. Tag and push:
   ```
   git tag v5.0.0
   git push origin v5.0.0
   ```
3. The release appears under GitHub → Releases with `RIS5-Setup-5.0.0.exe`, its `.sha256` and the CLI zip.

Use a suffix (`v5.0.0-rc1`) for test releases; they are marked as prerelease.

## Long tests

The missed-runs test (category `Integration`, about 3.5 minutes) is not run in CI:

```
dotnet test test/RIS.Hosting.Tests --filter "TestCategory=Integration"
```
