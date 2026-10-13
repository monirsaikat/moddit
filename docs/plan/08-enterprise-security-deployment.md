# 08 — Enterprise, Security, Deployment

## Deployment artifacts

| Artifact | Audience | Notes |
|----------|----------|-------|
| `ModitSetup-x64.exe`, `ModitSetup-arm64.exe` (Velopack) | Consumers, BYOD, non-admin users | Per-user, no UAC, auto-update from GitHub Releases with deltas; `--silent`, `--installto <dir>` |
| `Modit.msixbundle` (x64 + arm64) + `Modit.appinstaller` | Intune, Store | Auto-update via App Installer; `HoursBetweenUpdateChecks=24`, `ShowPrompt`, `UpdateBlocksActivation=false` |
| `Modit-x64.msi`, `Modit-arm64.msi` (WiX v5) | SCCM/GPO/legacy | Per-machine, unpackaged, self-contained WinAppSDK, features: Core, ShellIntegration, CLI, Packs.* |
| `Modit-Setup.exe` (WiX Burn bootstrapper) | Manual installs | Chains: WebView2 Evergreen bootstrapper (if missing), VC++ runtime (if any native dep needs it — avoid), MSI |
| `Modit-portable-x64.zip` | Tech users, air-gapped tests | `portable.flag` next to exe → settings/cache in `.\data` |
| `Modit.admx` + `Modit.adml` | IT admins | Group Policy templates; also Intune OMA-URI docs |
| `modit-packs/*.zip` | Air-gapped | Offline engine pack installers |
| winget manifests | Everyone | `Modit.Modit` for MSIX and MSI installer types |
| SBOM `modit-<ver>.cdx.json` (CycloneDX) | Security teams | Generated per build; includes native binaries |
| `THIRD-PARTY-NOTICES.txt`, `CHANGELOG.md`, release notes, `checksums.txt` (SHA-256) | All | |

### Silent install contracts

```
msiexec /i Modit-x64.msi /qn ALLUSERS=1 ADDLOCAL=Core,ShellIntegration,CLI INSTALLDIR="C:\Program Files\Modit" DISABLEUPDATES=1 LICENSEKEY=XXXX-XXXX
Modit-Setup.exe /quiet /norestart /log install.log
Add-AppxProvisionedPackage -Online -PackagePath Modit.msixbundle -DependencyPackagePath ... -LicensePath ... (Store-signed) / -SkipLicense
```

MSI public properties: `INSTALLDIR`, `ADDLOCAL`, `DISABLEUPDATES`, `LICENSEKEY`, `LICENSEFILE`, `PACKSOURCE`,
`DEFAULTPDFHANDLER=0|1`, `SHELLMENU=0|1`, `TELEMETRY=0|1`. Upgrades: MajorUpgrade with `AllowSameVersionUpgrades`
off, `Schedule=afterInstallInitialize`. Uninstall removes everything except user data unless `REMOVEUSERDATA=1`.

### Code signing and integrity

- All EXEs, DLLs, MSIX, MSI, bootstrapper signed via **Azure Trusted Signing** (or OV/EV certificate) in CI with
  timestamping (RFC 3161). Native third-party DLLs are re-signed under our certificate after hash verification.
- `native/manifest.json` pins SHA-256 of every third-party binary; build fails on mismatch.
- Release builds are reproducible from tag; `SourceLink` enabled; symbols published to a symbol server.
- MSIX uses the same publisher identity as the sparse shell package (required for context menu).

## Updates

Full design in [15-release-and-auto-update-github.md](15-release-and-auto-update-github.md). Summary:

| Channel | Mechanism | Policy |
|---------|-----------|--------|
| Velopack (per-user) | In-app `UpdateManager` with `GithubSource` on the public releases repo; delta downloads; apply on exit or on demand; rollback | `UpdateCheck`, `UpdateChannel` (stable/beta), `UpdateUrl` (internal mirror of the Velopack feed), `UpdateDeferDays` |
| MSI (per-machine) | Same feed, **notify-only** ("Version X available; contact IT" + release notes); self-update only if `AllowUserUpdatesForMachineInstall=1` and user is admin (runs MSI with UAC) | as above |
| MSIX | App Installer from `Modit.appinstaller` on GitHub Pages (or internal `UpdateUrl` mirror), 24 h checks, or Store | `UpdateCheck=0` disables the in-app check; App Installer policy via MDM |
| Engine packs | Manifest feed on the releases repo (GitHub Pages JSON); never auto-update silently in managed mode | `EnginePackUpdates`, `EnginePackSource` |

Update checks are plain HTTPS GETs to `api.github.com` / `github.com` / `*.github.io` and send nothing but the
request itself (GitHub sees IP and user agent `Modit/<version> (<rid>)`). Policy can disable entirely or redirect
to an internal mirror produced by `tools/mirror-release.ps1` for air-gapped networks.

## Group Policy / MDM (ADMX)

Registry root: `HKLM\SOFTWARE\Policies\Modit` (machine) and `HKCU\SOFTWARE\Policies\Modit` (user; machine wins).
Every key: `DWORD` boolean or `REG_SZ`. Documented in `docs/admin/policies.md` generated from `PolicyCatalog.cs`.

| Policy | Type | Effect |
|--------|------|--------|
| `UpdateCheck` | 0/1 | Disable update checks |
| `UpdateUrl`, `UpdateChannel` | SZ | Internal update feed |
| `TelemetryEnabled`, `CrashReportsEnabled` | 0/1 | Force off (UI toggles locked) |
| `EnginePackDownloadsBlocked`, `EnginePackSource`, `EnginePacksAllowed` (multi-SZ) | | Control packs |
| `AllowMicrosoftOfficeAutomation` | 0/1 | Office COM path |
| `HtmlToPdfAllowNetwork` | 0/1 | WebView2 may fetch remote resources |
| `AllowUrlToPdf` | 0/1 | Convert URL feature |
| `AllowRemoveSecurity` | 0/1 | Unlock/remove permissions feature |
| `AllowRedaction`, `AllowSigning`, `AllowOcr`, `AllowMedia`, `AllowArchives` | 0/1 | Feature gates (hide in UI, error in CLI code 3) |
| `DefaultOutputFolder`, `OutputFolderLocked` | SZ/0-1 | Force outputs to a managed location |
| `AuditLogEnabled`, `AuditLogPath`, `AuditLogRetentionDays` | | Audit |
| `RecentFilesEnabled`, `ThumbnailCacheEnabled`, `CacheMaxMB` | | Privacy/caching |
| `LicenseKey`, `LicenseFilePath`, `LicenseServerUrl` (P2) | SZ | Provision license centrally |
| `DefaultPdfHandler` | 0/1 | Register as default PDF app (per-user prompt behavior) |
| `ShellIntegrationEnabled` | 0/1 | Context menus |
| `RenderHostPoolSize`, `BatchHostPoolSize`, `HostMemoryLimitMB` | DWORD | Performance tuning |
| `AllowedInputPaths`, `BlockedInputPaths` (multi-SZ) | | Restrict file locations |
| `DisableClipboard`, `DisableDragDropOut` | 0/1 | DLP-ish controls |
| `ProxyMode`, `ProxyUrl` | | System default / manual / none |
| `AnnotationAuthorName`, `AnnotationAuthorLocked` | SZ/0-1 | Standardize author field |
| `SignatureTsaUrl`, `SignatureRequireTimestamp`, `SignatureAllowedCertIssuers` | | Signing posture |

UI: Settings shows managed values with lock + "Managed by your organization"; `modit policy show` prints
effective policy with sources.

## Licensing (architecture; commercial terms per 13)

- License file: JSON payload (`product`, `edition: Free|Pro|Enterprise`, `seats`, `issuedTo`, `issuedAt`,
  `expiresAt?` (perpetual if null), `maintenanceUntil` (updates entitlement), `features[]`, `machineId?`) +
  Ed25519 signature (public key embedded; private key offline in owner HSM/vault).
- Activation modes: (a) key → online activation returns signed license bound to machine fingerprint (hashed
  machine GUID + volume serial, tolerant to 1 change); (b) offline: app generates `request.json`, admin uploads
  on portal, receives `license.json`; (c) volume license file deployed via policy, no activation, no binding.
- Grace: 30 days offline tolerance for online-activated licenses; expiry warnings 30/7/1 days.
- Feature gating in `Core` (`ILicenseGate.Require(Feature.X)`), never only in UI; CLI exit code 6 when unlicensed.
- Trial: 14 days full features, then Free edition (view, merge/split/rotate/convert images basic) — subject to 13.
- No DRM theatre: gating is honest; tampering is a legal matter, not an arms race.

## Audit log

- JSONL, one line per operation: `{ts, user (SID + name), machine, app version, operation, params (redacted:
  no passwords; paths hashed if `AuditHashPaths`), inputs [{path/hash, size}], outputs, duration, result,
  errorCode, licenseEdition}`.
- Written by `Core` for GUI, CLI, watch folders alike; rotated daily; retention per policy; location per policy
  (UNC paths supported, failure to write never blocks the operation but is surfaced in the UI once).
- Windows Event Log provider `Modit` for install/uninstall/policy-block events (optional).

## Security hardening

### Process isolation
- Engine hosts run with a **restricted token** (`CreateRestrictedToken` with `DISABLE_MAX_PRIVILEGE`, SID
  deny for Administrators), **low integrity level** where file access permits (we pass file data via mapped
  sections opened by the UI process → hosts need no file ACL rights for inputs; outputs written by the UI
  process from host-produced buffers for small results, or hosts write to a temp dir ACL'd for low integrity).
- **Job Object**: kill-on-close, memory limit, active process limit 1 (external tools get their own job),
  `JOB_OBJECT_UILIMIT_*` to block desktop/clipboard access.
- **No network** in hosts: Windows Filtering Platform rule is overkill; use `WSAAccept`-less design + policy +
  firewall-friendly behavior. Hosts simply have no networking code; audited via analyzer banning `System.Net` in
  host assemblies.
- External tools (LibreOffice, FFmpeg, Pandoc) run in their own Job Object with limits, `CREATE_NO_WINDOW`,
  minimal environment, working dir in temp, `-env:UserInstallation` isolated profile for LibreOffice, macros
  disabled via LibreOffice registry modifications (`MacroSecurityLevel=3`) in the pack's profile template.
- WebView2 for HTML→PDF: `AreDefaultScriptDialogsEnabled=false`, `IsWebMessageEnabled=false`, no
  `AddHostObjectToScript`, downloads blocked, new windows blocked, `file://` only unless policy allows network,
  `--disable-features=msSmartScreenProtection` **not** used (keep defaults), separate user data folder with
  `IsGeneralAutofillEnabled=false`, cleared after job.

### Input handling
- PDFium built without V8/XFA → no PDF JavaScript execution ever; `/OpenAction` and `/Launch` never executed.
- Links: external URLs open via `Launcher.LaunchUriAsync` after confirmation dialog showing the URL; `file:` and
  `javascript:` schemes blocked.
- Attachments: opening embedded files extracts to temp and uses ShellExecute only after SmartScreen-equivalent
  warning; policy can block.
- Archives: zip slip, bomb ratio, count, size, symlink/reparse checks (see 05).
- Images: libvips limits (`VIPS_MAX_COORD`), reject > 500 MP unless explicitly allowed; decompression bomb
  heuristics (PNG/JPEG dimension vs. file size).
- Fonts in PDFs: parsed by PDFium's FreeType (sandboxed host).
- Paths: canonicalize, reject reparse-point traversal into protected dirs for outputs, long path aware.
- Office COM path: `AutomationSecurity=ForceDisable`, `DisplayAlerts=0`, timeouts, separate host.

### Data protection
- Passwords never persisted; in memory as `SecureString`-equivalent pinned buffers cleared after use; never logged.
- Recovery journals contain edit metadata only, never content; cleared on successful save.
- Thumbnail cache can be disabled by policy (sensitive documents); "Clear all data" in Settings.
- Temp files created with `FILE_ATTRIBUTE_TEMPORARY`, securely deleted (overwrite once) for redaction and
  signing intermediates.
- Clipboard: optional `DisableClipboard` policy; redaction "Apply" clears undo history and recovery for the doc.

### Crypto
- PDF encryption AES-256 (R6) via QPDF (its own implementation; FIPS mode note in docs). Signatures via Windows
  CNG (`System.Security.Cryptography`), FIPS-validated modules when OS FIPS policy enabled. Hashing SHA-256+.
  License verification Ed25519 (`NSec` or .NET 10 built-in if available; fallback RSA-PSS 3072 if FIPS strictness
  required — decision in 13).

### Supply chain
- Dependabot/Renovate for NuGet; `dotnet list package --vulnerable` in CI; native binaries pinned by hash and
  sourced from official releases only; SBOM published; `Directory.Packages.props` central versions; lock files
  (`packages.lock.json`) committed with `RestoreLockedMode`.
- Build on GitHub Actions `windows-2022`/`windows-11-arm` runners + self-hosted Baseline for perf; OIDC to Azure
  for signing; no long-lived secrets.

### Threat model summary

| Threat | Control |
|--------|---------|
| Malicious PDF exploits parser | Out-of-process restricted host; PDFium kept current (monthly native refresh); no JS |
| Zip bomb / path traversal | Limits + canonicalization |
| Data exfiltration via converter network access | Hosts have no network; WebView2 network off by default; policies |
| Tampered update | Signed packages; HTTPS; MSIX identity; Velopack signature verification |
| Malicious engine pack | Signed manifests + per-file SHA-256; policy whitelist |
| Credential/password leakage | No persistence, no logs, memory clearing |
| Privilege escalation via installer | MSI per-machine install only writes Program Files/HKLM; no services unless Watch-folder service feature chosen; service runs as a virtual service account with minimal rights |

## Watch folders service (P1)

- Per-user background task (preferred; runs when user logged in) or Windows Service (`Modit.Service.exe`,
  feature-selectable in MSI) running as `NT SERVICE\ModitWatch` virtual account with explicit folder grants.
- Config in `%PROGRAMDATA%\Modit\watch\*.json`: folder, filters, recipe, output, error folder, stability delay
  (file fully written detection: size unchanged 2 s + exclusive open), concurrency, retry policy, log.
- Idempotency: processed ledger (SQLite) keyed by path + size + mtime + hash.
- Management via `modit watch ...` CLI and a Settings page.

## Documentation deliverables for IT

- `docs/admin/deployment.md` (Intune, SCCM, GPO walkthroughs with screenshots), `policies.md` (generated),
  `cli.md` (generated from System.CommandLine), `engine-packs.md`, `security-whitepaper.md`, `sbom-and-licenses.md`,
  `troubleshooting.md` (log locations, diagnostics bundle), `release-notes/`.
