# 14 — Installation, First Run, Repair, Uninstall

"Production ready" starts before the first window opens. This chapter specifies every install path end to end,
the first-run experience, self-repair, migration between install flavors, and a residue-free uninstall. Items
tagged **[M0]** are built in milestone 0 (tasks M0.10/M0.11); **[M6]** are enterprise additions.

## Install flavors and coexistence

| Flavor | Location | Rights | Updates | Identity | Who |
|--------|----------|--------|---------|----------|-----|
| **Velopack** `ModitSetup-<rid>.exe` [M0] | `%LOCALAPPDATA%\Modit\` (`current\` + `packages\`) | None (per-user) | Self-update from GitHub Releases (deltas) | Unpackaged; sparse package adds identity for Win11 context menu | Consumers, BYOD, non-admins |
| **MSI** `Modit-<rid>.msi` / Burn `Modit-Setup-Enterprise.exe` [M6] | `%PROGRAMFILES%\Modit\` | Admin | Notify-only (IT deploys) unless policy | Unpackaged + sparse package registered per-machine | Enterprises (SCCM/GPO/Intune Win32) |
| **MSIX** `Modit.msixbundle` [M6] | WindowsApps | None (per-user) / provisioned | App Installer (appinstaller) or Store | Full MSIX identity | Intune MSIX, Store |
| **Portable** `Modit-portable-<rid>.zip` [M0] | Any folder, `portable.flag` present | None | Disabled; "Check for updates" opens release page | None (no context menu, no associations) | Tech users, USB, evaluation |

Coexistence rules (enforced by every installer and by the app at startup):
- Only one flavor per machine/user is supported. Each installer detects the others (registry keys
  `HKCU\Software\Modit\Install` / `HKLM\Software\Modit\Install` with `Flavor`, `Version`, `Path`, MSIX package
  family presence) and refuses with a clear message and the command to remove the other flavor. MSI may
  optionally migrate from Velopack: `MIGRATEFROMUSERINSTALL=1` runs the Velopack uninstaller silently and keeps
  user data.
- Portable can run alongside any flavor; it never registers anything and uses `.\data\` for all state.

## Velopack installer flow [M0]

`vpk pack` configuration (in `tools/build.ps1`):

```
vpk pack --packId Modit --packVersion <semver> --packDir publish/win-x64 --mainExe Modit.App.exe
         --packTitle "Modit" --packAuthors "<Publisher>" --icon assets/modit.ico --splashImage assets/splash.gif
         --framework webview2 --channel win-x64-stable --shortcuts Desktop,StartMenu
         --exclude ".*\.pdb" --releaseNotes RELEASE_NOTES.md
         --azureTrustedSignFile tools/release/trusted-signing.json   # CI only; dev builds unsigned & labeled
```

User experience:
1. Double-click `ModitSetup-x64.exe` (SmartScreen clean because signed with Trusted Signing). Splash shows for
   the ~3–6 s install (self-contained .NET + WinAppSDK ≈ 80–90 MB extracted).
2. Velopack installs WebView2 Evergreen if absent (`--framework webview2`), creates shortcuts, runs the app with
   `--veloapp-install <version>` first (our hook: register file associations for `.pdf` "Open with", `.svg`,
   `.moditrecipe`, protocol `modit://`, register sparse package for Win11 context menu, create the per-user
   scheduled task for watch folders only when a watch folder is configured, write
   `HKCU\Software\Modit\Install`), then launches normally with `--first-run`.
3. Silent/scripted: `ModitSetup-x64.exe --silent`, `--installto "D:\Apps\Modit"`, `--no-shortcuts` (we honor via
   hook env), `--channel beta` not supported by Setup itself; channel is a build-time property, so publish
   `ModitSetup-x64-beta.exe` for the beta channel.
4. ARM64 devices downloading the x64 setup: the download page serves the right file via user-agent/`navigator.
   userAgentData.architecture` detection; the x64 app also detects running under emulation on ARM64 and offers the
   native build on first run.

Velopack hooks implemented in `Modit.App` `Program.Main` before anything else:

```csharp
VelopackApp.Build()
    .OnFirstRun(v => InstallHooks.FirstRun(v))            // show onboarding
    .OnAfterInstallFastCallback(v => InstallHooks.Register(v))     // associations, sparse package, registry
    .OnAfterUpdateFastCallback(v => InstallHooks.Register(v))      // re-register on update (paths unchanged, but idempotent)
    .OnBeforeUninstallFastCallback(v => InstallHooks.Unregister(v))// remove everything we registered
    .Run();
```

Hooks must complete in < 15 s and never show UI (Velopack kills slow hooks). They log to `%LOCALAPPDATA%\Modit\logs\install.log`.

## MSI / Burn flow [M6]

- Burn bundle `Modit-Setup-Enterprise.exe`: chains WebView2 Evergreen bootstrapper (detect by registry
  `{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}` key), then `Modit-<rid>.msi`. UI: WiX standard bootstrapper with our
  theme (logo, license, install dir, features). Silent: `/quiet /norestart /log`.
- MSI features: `Core` (required), `ShellIntegration` (context menu + associations), `CLI` (adds to PATH via
  `Environment` component), `WatchService` (installs `Modit.Service` as `NT SERVICE\ModitWatch`), `DesktopShortcut`.
  Engine packs are not MSI features (too large); IT pre-stages them to `%PROGRAMFILES%\Modit\engines` or uses
  `PACKSOURCE=\\server\share\modit-packs` which the app treats as a local mirror.
- Properties documented in 08. Per-machine registry `HKLM\Software\Modit\Install`. ADMX files copied to
  `%PROGRAMFILES%\Modit\admx\` and optionally to `PolicyDefinitions` with `INSTALLADMX=1`.
- Upgrade: `MajorUpgrade DowngradeErrorMessage` and `Schedule="afterInstallInitialize"`; settings untouched
  (they live in user profile). Running instances closed via `util:CloseApplication` with prompt in UI mode,
  forced in silent mode after `Modit.App --save-and-exit` (graceful: triggers autosave journals).
- Uninstall: `REMOVEUSERDATA=1` deletes `%LOCALAPPDATA%\Modit` for the current user only; otherwise leaves data.

## MSIX flow [M6]

- Package identity `Modit.Modit`, publisher from Trusted Signing certificate subject. Capabilities:
  `runFullTrust` (needed for engine host processes, Job Objects, named pipes to our own processes).
- `Modit.appinstaller` on GitHub Pages with `MainBundle Uri` pointing at the GitHub Release asset URL for the
  bundle, `OnLaunch HoursBetweenUpdateChecks="24" ShowPrompt="true"`, `AutomaticBackgroundTask`.
  (GitHub Pages serves `.appinstaller` with a generic MIME type; App Installer accepts it when downloaded via
  the browser or `Add-AppxPackage -AppInstallerFile`. The `ms-appinstaller:` protocol is disabled by default on
  current Windows; the download page therefore links the file directly and documents the policy to re-enable the
  protocol for managed devices.)
- Intune: upload bundle with dependencies (none; self-contained WinAppSDK) as "Windows app (Win32)" or LOB.
- Store: optional post-1.0 submission; Store build sets `IsStoreBuild` so the in-app updater is replaced by a
  "Updates are managed by the Microsoft Store" line (Store policy).

## Portable mode [M0]

- `portable.flag` next to `Modit.App.exe` → all state in `.\data\` (settings, cache, queue, logs, recovery,
  engines). No registry writes, no associations, no sparse package, no updater. Settings → About shows
  "Portable mode".
- `modit portable export <zip>` / `import` to move settings between a portable and an installed copy.

## Prerequisites and runtime checks [M0]

At startup (`Startup/PrereqCheck` ETW event, < 20 ms):

| Check | Failure handling |
|-------|------------------|
| OS ≥ 10.0.17763 | Installer blocks; app shows dialog and exits |
| WebView2 Runtime present (registry `pv` key) | Features needing it show an inline "Install WebView2" card that launches the Evergreen bootstrapper (downloaded from Microsoft, verified by signature); not a startup blocker |
| Windows App SDK runtime (unpackaged, non-self-contained builds only; our builds are self-contained so this never fails) | Dialog with download link |
| Native engine DLLs present and hash-matching `native/manifest.json` | "Installation damaged" → offers **Repair** (Velopack: re-download full package and reinstall; MSI: `msiexec /fa`; MSIX: `Reset` via Settings link) |
| Writable `%LOCALAPPDATA%\Modit` | Falls back to `%TEMP%\Modit` with a warning banner |
| HEVC extension (for HEIC) | Deferred; checked when a HEIC file is opened |
| Engine packs integrity (lazy, per pack, on first use) | Offers re-download |

## First-run experience [M0]

Triggered by `--first-run` (Velopack `OnFirstRun`), by MSI `FIRSTRUN=1` launch after install, or when
`settings.json` is absent. A single `FirstRunPage` inside the main window (not a separate wizard window), four
cards, every step skippable, total ≤ 30 s:

1. **Welcome** — one sentence, version, "What's new" link (release notes from the bundled `RELEASE_NOTES.md`).
2. **Appearance** — Light / Dark / System; Mica on/off preview; compact density toggle.
3. **Make Modit your PDF app?** — shown only if policy `DefaultPdfHandler` is unset and flavor ≠ portable; button
   opens Windows Settings default apps page (`ms-settings:defaultapps?registeredAppUser=Modit`) since apps cannot
   silently set defaults on Windows 10/11; explains that.
4. **Converters** — list of engine packs with sizes and a single "Install recommended (OCR languages for your UI
   language)" button; everything else later from Settings. Shows policy lock state when managed.

Then Home. First run also: creates the recipes folder with 5 sample recipes, seeds Recent with nothing, writes
`settings.json` with schema version, and records `InstallFlavor`, `InstalledVersion`, `FirstRunCompletedAt`.

Upgrades (version changed since last run): show a **What's new** InfoBar on Home with "Read notes" (renders
`RELEASE_NOTES.md` section for the installed version in a dialog) and "Dismiss"; never a modal.

## Settings → About [M0]

- Version (SemVer + build hash + channel), flavor, install path, data path, engine versions (PDFium, QPDF,
  libvips, resvg), Windows App SDK version, WebView2 version.
- **Check for updates** (hidden in portable/Store/policy-disabled; shows "Managed by your organization" when
  locked), channel switch stable/beta (beta switch warns; switching to stable from a higher beta version waits
  for the next stable ≥ current), "View release notes", "Open release page on GitHub".
- **Repair installation**, **Reset settings** (keeps recipes and license unless unchecked), **Clear caches**,
  **Collect diagnostics** (zip), **Open logs folder**, **Third-party notices**, **License** (key, edition, seats,
  expiry, Deactivate), **Uninstall** (opens Windows Apps settings page).

## Repair and self-healing [M0]

- `Repair installation`: Velopack → `UpdateManager` re-downloads the current full package and applies it (same
  version reinstall is supported via `--veloapp-reinstall` style flow: we call `vpk`-less path by downloading the
  full nupkg and invoking `Update.exe --apply`); MSI → `msiexec /fa {ProductCode}` with UAC; MSIX → opens
  `ms-settings:appsfeatures-app` with instructions.
- Startup self-heal: missing shortcuts or registry associations are re-created silently (idempotent `Register`);
  stale sparse package version mismatch → re-register.
- Corrupted `settings.json` → backed up to `settings.corrupt.<ts>.json`, defaults loaded, InfoBar shown.
- Corrupted SQLite queue/thumbnail DB → recreated (queue items lost are listed in the InfoBar with "Export list").

## Uninstall [M0]

- Velopack: Windows Apps → Uninstall runs `Update.exe --uninstall` → our `OnBeforeUninstall` hook removes:
  file associations and protocol (`HKCU\Software\Classes\Modit.*`, `Applications\Modit.App.exe`, `RegisteredApplications`),
  sparse package (`PackageManager.RemovePackageAsync`), scheduled tasks, context menu registry (Win10 classic),
  `HKCU\Software\Modit\Install`, jump list. Then a tiny WinUI-free dialog (Win32 `TaskDialog`): "Keep my settings,
  recipes and license?" default Yes; No → deletes `%LOCALAPPDATA%\Modit`. Silent uninstall never prompts and keeps data.
- MSI: standard; same hook logic in a custom action (deferred, runs as user for HKCU parts via `Impersonate=yes`).
- MSIX: OS removes everything in the package; per-user data in `LocalState` is removed by the OS; our sparse
  package is not used in MSIX flavor.
- Verification script `tools/release/verify-clean-uninstall.ps1`: snapshots registry/filesystem before install,
  after uninstall, and diffs; allowed residue list is explicit (user data when kept, Windows' own MRU entries).

## Migration between flavors and versions

- Settings schema versioned; `SettingsMigrator` upgrades step by step; downgrade (rollback) tolerated by ignoring
  unknown keys and keeping a backup `settings.pre-<version>.json`.
- Velopack → MSI: MSI property `MIGRATEFROMUSERINSTALL=1` (default 1 in UI mode with a checkbox) uninstalls the
  per-user copy silently keeping data; app reads the same `%LOCALAPPDATA%\Modit`.
- Portable → installed: `modit portable export` or Settings → Import settings.
- Engine packs installed per-user remain valid for MSI installs (search order: per-machine, then per-user).

## Install telemetry

None. Installers do not phone home. The only network activity is the updater and the pack downloader, both
policy-controllable and both documented in 15.

## Download page (GitHub Pages, `modit-releases` repo) [M0]

Static page generated by `pages.yml` from the latest release metadata: detects OS/arch, shows the primary
button (Velopack setup for the user's architecture), "Other downloads" (MSI, MSIX, portable, CLI), SHA-256 list,
release notes, "Enterprise deployment guide" link, system requirements, and verification instructions
(`signtool verify /pa /v ModitSetup-x64.exe` — signed by `<Publisher>`). No analytics scripts.
