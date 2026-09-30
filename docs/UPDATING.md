# Windows portable updates

SED checks release metadata in the background after the main window opens. A successful check or a failed-feed result is cached for 24 hours. **Help → Check for updates** opens the update window; **Check now** bypasses that cache. The checkbox disables automatic checks. No package is downloaded or launched until its separate button is used.

Preview builds default to the preview channel. Stable builds default to stable; choosing preview is explicit. The updater orders the historic `0.4.0-mots.N-dev` family numerically, so `mots.10-dev` follows `mots.9-dev`. Equal versions and downgrades are never offered, including a changed archive digest under the same version. A stable release follows previews of the same base version. Unknown local version formats fail closed.

## Installation and recovery

**Download and prepare** creates a new portable installation beside the running copy. If that parent is unwritable, it uses `%LOCALAPPDATA%\SED\updates\versions`. The update window displays the actual destination. It never overwrites the running copy, project files, or an existing destination. Closing the window cancels an in-progress download/preparation; an already prepared directory remains available.

**Save recovery and restart** revalidates every staged file, saves the current level to the configured project's recovery folder, and opens that recovery JKL in the new editor. Other editor windows must first be closed to commit or cancel their pending changes. Serialization or recovery-write failure leaves the current editor open. The old session closes only when the new process acknowledges the expected release version and the exact recovery-file SHA256 after successfully opening the recovery document. Cancellation, an immediate process exit, missing acknowledgement, or a failed document open leaves the old editor open. A recovery copy and the old installation remain available after a later crash.

This is a portable handoff, not an in-place installer: existing shortcuts still open the old copy. Update those shortcuts to the displayed installation when ready. The old directory is the rollback copy; cleanup is manual. Settings in the user profile and external project/game paths are shared. Local plug-ins and untracked files inside the old installation are not copied into the verified new installation. The handoff restores the level through its JKL representation; transient selection, undo history, and uncommitted auxiliary-window edits are not transferred.

Automatic installation currently supports Windows x64. Other platforms display a manual-update explanation.

## Feed and package contract

The preferred feed is `https://richard-pemberton-research.darkahn.chatgpt.site/updates/sed/releases.json`. The fallback is `https://gitlab.com/codalilitari/SED-MotS/-/raw/main/updates/sed-releases.json`. Packages can use the portfolio's `/updates/sed/` subtree or GitLab's exact project `86965025` generic package subtree `/api/v4/projects/86965025/packages/generic/sed-mots/`. Every redirect is checked against the same project path allowlist. Query strings, userinfo, nonstandard ports, fragments, backslashes and percent escapes are rejected. There is currently no external CDN redirect exception.

```json
{
  "schemaVersion": 1,
  "releases": [
    {
      "version": "0.4.0-mots.16-dev",
      "channel": "preview",
      "platform": "win-x64",
      "packageUrl": "https://gitlab.com/api/v4/projects/86965025/packages/generic/sed-mots/0.4.0-mots.16-dev/SED-MotS-0.4.0-mots.16-dev-win-x64.zip",
      "sha256": "REPLACE_WITH_THE_64_HEX_DIGIT_ARCHIVE_SHA256",
      "sizeBytes": 123456789,
      "entryDirectory": "SED-MotS-0.4.0-mots.16-dev-win-x64"
    }
  ]
}
```

The example is a schema illustration, not a published-release receipt. `entryDirectory` must name the single top-level package directory, or be empty for a flat archive. Releases are immutable by `(platform, version)`; duplicate identities are refused. Unknown or duplicate JSON fields are refused. The feed is limited to 256 KiB, 100 releases and JSON depth 8. Packages are bounded to 1 GiB, 20,000 ZIP entries, 4 GiB total expanded data and 1 GiB per file. Invalid paths, duplicate case-insensitive names, Windows device names, alternate data streams, archive symlinks/reparse entries and special files are refused. A self-contained package must contain `Sed.App.exe`, `Sed.App.dll`, `Sed.App.runtimeconfig.json`, and `coreclr.dll` at its application root.

The download must match the advertised byte count and SHA256 before extraction. A private local receipt records every extracted file's byte count and SHA256. The launch API rechecks that receipt and all files and rejects added files (including extra DLLs), changes, missing files, and reparse links. Failed staging does not publish a partial installation. No feed can specify a command, arguments, shell script, or helper executable.

The trust root is HTTPS plus control of the allowlisted portfolio/GitLab project. The feed is not independently signed. A compromised authorized release host could publish a new executable and its matching hash; SHA256 detects mismatch/corruption, not publisher compromise. Same-user concurrent filesystem tampering is not a sandboxed threat model. The service's production `HttpClientHandler` disables automatic redirects so each hop is validated before the next request.

## Headless release acceptance

After `tools/Verify-Distribution.ps1` passes for a frozen Windows package,
`python tools/build_update_feed.py --package EXACT_WINDOWS_ZIP --output NEW_JSON`
creates the feed from its actual provenance, byte count and SHA256. It never
uploads anything and refuses an existing output. Publish the immutable package
before publishing that same feed to the portfolio and GitLab; verify anonymous
downloads and actual service staging before announcing availability. Never
replace package bytes under a previously published version.

`dotnet/tools/Sed.UpdateProbe` references only `Sed.Core`; it does not load Avalonia, user settings, recovery startup, or background GUI checks. Normal probe mode never launches the downloaded editor. Its executable is deliberately named `Sed.App.exe` so its separate original-fixture mode can exercise the same fixed executable path.

Build with .NET 10:

```powershell
dotnet publish dotnet/tools/Sed.UpdateProbe/Sed.UpdateProbe.csproj -c Release -r win-x64 --self-contained true -o build/update-probe
```

For release verification, create an existing disposable `current` directory containing marker files under a disposable installation parent. Download the public manifest/package anonymously first, then pass those exact files (offline mode never makes a request):

```powershell
build/update-probe/Sed.App.exe `
  --feed https://richard-pemberton-research.darkahn.chatgpt.site/updates/sed/releases.json `
  --manifest-file C:/release-check/releases.json `
  --package-file C:/release-check/SED-MotS-win-x64.zip `
  --current-version 0.4.0-mots.15-dev --channel preview `
  --current-dir C:/release-check/install/current `
  --install-root C:/release-check/install `
  --result C:/release-check/result.json
```

Omit both file arguments to acquire bytes using the service's HTTPS path. All arguments are explicit name/value pairs. The result records selected version, archive digest/size, staged directory and receipt, old-file hash preservation, and refusal to offer the now-equal version again. The current directory's parent is the preferred staging location; `--install-root` supplies fallback/cache storage. Use disposable paths rather than a live editor installation for this probe.

The separate integration fixture builds a package from the probe's own self-contained publish directory, stages it with the real service, launches the staged original program, and requires its expected marker and zero exit code. It refuses an existing output directory:

```powershell
build/update-probe/Sed.App.exe --self-test C:/release-check/new-fixture-run
```

This fixture proves staging and process launch, not production SED recovery/runtime compatibility. Public-package acceptance and GUI recovery handoff need their own receipts. Unit tests in `PortableUpdateTests` cover version/channel selection, malformed/cache manifests, project paths/redirects, hash/size failures, archive attacks, fallback staging, preservation, and staged tampering.
