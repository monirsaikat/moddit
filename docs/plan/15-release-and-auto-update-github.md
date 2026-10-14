# 15 — Release Pipeline and Auto-Update on GitHub

GitHub is the single backbone for source, CI, signing orchestration, release hosting, auto-update feeds, the
download page, engine pack distribution and package-manager submission. This chapter is the complete design;
tasks M0.10 and M6.2 implement it. Items tagged **[M0]** ship in milestone 0.

## Repositories

| Repo | Visibility | Contents |
|------|------------|----------|
| `<owner>/modit` | Private (default; see Q7) | Source, tests, workflows, docs. Protected `main`. |
| `<owner>/modit-releases` | **Public** | GitHub Releases with all installer assets, Velopack feed files, `RELEASE_NOTES.md`; GitHub Pages site (download page, `Modit.appinstaller`, `rollout.json`, `packs/index.json`, admin docs HTML); issue tracker for users. No source. |
| `<owner>/modit-packs` | Public | GitHub Releases hosting engine pack zips (LibreOffice 350 MB fits the 2 GB per-asset limit) + signed `manifest.json` per pack version. |
| `<owner>/winget-pkgs` (fork) | Public | Fork used by the winget automation to open PRs against `microsoft/winget-pkgs`. |

Why a separate public releases repo: the Velopack `GithubSource` and App Installer need anonymous HTTPS access.
Private repos would require an access token inside the app (extractable) and share the 5,000 req/h token limit
across all users. Anonymous access is rate-limited at 60 req/h **per client IP**, which a daily check never
approaches. If the owner later open-sources the project, the two repos collapse into one.

## Versioning and channels

- SemVer via Nerdbank.GitVersioning (`version.json`), `1.4.2`; pre-releases `1.5.0-beta.3`. MSIX/MSI get
  `1.4.2.0` / `1.5.0.<height>`; Velopack uses full SemVer.
- Tags drive releases: `v1.4.2` → **stable**; `v1.5.0-beta.3` → **beta** (GitHub pre-release). Nothing else
  publishes.
- Velopack channels per RID: `win-x64-stable`, `win-arm64-stable`, `win-x64-beta`, `win-arm64-beta`. Each channel has
  its own `releases.<channel>.json` feed file uploaded to the release. Velopack requires one full + one delta
  package per channel per release.
- Beta users receive stable releases too when the stable version is higher (the updater on channel `beta` queries
  `prerelease: true` which includes stable releases on GitHub).
- Hotfix: branch from the tag, cherry-pick, tag `v1.4.3`. Rollback: see below.

## GitHub Actions workflows (`modit` repo)

### `ci.yml` [M0] — pull requests and `main`
Runners `windows-2022` (x64) and `windows-11-arm` (arm64) matrix. Steps: restore with lock files, fetch native
(cached by manifest hash), build Release both RIDs, unit/native/engine/golden tests, license + vulnerability +
secret scans, coverage gate, unsigned Velopack + portable artifacts for manual testing (retained 7 days),
UI smoke on packaged build. Required status checks for merge.

### `release.yml` [M0] — on `push: tags: ['v*']`
```
permissions: { contents: write, id-token: write }
jobs:
  build (matrix rid: win-x64, win-arm64):
    - checkout (fetch-depth 0 for nbgv + git-cliff)
    - setup .NET 10, nbgv get-version → outputs
    - tools/fetch-native.ps1 -Rid ${rid}
    - dotnet publish src/Modit.App -r ${rid} -c Release -p:PublishReadyToRun=true -p:WindowsAppSDKSelfContained=true -p:SelfContained=true
    - dotnet publish Modit.Engine.Host / Modit.Cli / Modit.Service (Native AOT)
    - azure/login (OIDC federated credential, subject locked to repo + tag ref pattern)
    - sign all .exe/.dll in publish dirs: azure/trusted-signing-action (endpoint, account, profile; SHA-256; RFC 3161 timestamp)
    - vpk download github --repoUrl https://github.com/<owner>/modit-releases --channel win-${rid}-${channel}   # previous release for deltas
    - vpk pack ... --channel win-${rid}-${channel} --azureTrustedSignFile tools/release/trusted-signing.json   # signs Setup.exe, Update.exe, nupkg contents
    - portable zip, cli zip (signed contents), SBOM (cyclonedx-dotnet + native manifest merge), THIRD-PARTY-NOTICES
    - [M6] MSI (wix build), Burn bundle, MSIX bundle (makeappx + sign), appinstaller render
    - upload-artifact: release-${rid}
  publish (needs: build):
    - download artifacts
    - git-cliff --latest → RELEASE_NOTES.md (conventional commits since previous tag; sections Features/Fixes/Performance/Security)
    - checksums.txt (SHA-256 of every asset)
    - vpk upload github --repoUrl https://github.com/<owner>/modit-releases --token ${RELEASES_REPO_TOKEN} --publish --releaseName "Modit ${version}" --tag v${version} --pre (if beta) --merge   # one upload per channel, --merge keeps a single release
    - gh release upload (remaining assets: msi, msix, portable, cli, sbom, notices, checksums, RELEASE_NOTES.md) --repo <owner>/modit-releases
    - repository_dispatch → modit-releases `pages.yml` (regenerate download page) and, if stable, `winget.yml`
  verify (needs: publish): on a clean windows runner
    - download ModitSetup-x64.exe from the just-published release, `signtool verify /pa`, install `--silent`, run `Modit.Cli --version`, assert equals tag
    - tools/release/verify-update-path.ps1 -From <previous stable or beta> -To <this> (installs previous Setup, launches app with MODIT_UPDATE_CHECK_NOW=1, waits for restart, asserts version) — fails the release if the update path breaks
    - on failure: `gh release edit --draft` to pull the release and open an issue automatically
```
Secrets: `RELEASES_REPO_TOKEN` (fine-grained PAT, contents:write on `modit-releases` and `modit-packs` only),
`AZURE_CLIENT_ID`/`TENANT_ID`/`SUBSCRIPTION_ID` (OIDC, not secrets), `WINGET_PAT` (fork + public_repo).
Environment `release` with required reviewers (one approval before `publish` runs) for stable tags; betas run
without approval.

### `nightly.yml` — schedule 02:00 UTC
Perf budgets on self-hosted Baseline, fuzz 2 h, pack-dependent conversion tests, install matrix smoke on
Hyper-V VMs (self-hosted), dependency vulnerability report, and a **nightly channel** optional build
(`win-x64-nightly`, uploaded to a rolling `nightly` pre-release that is deleted/recreated; off by default, enabled by
`NIGHTLY_ENABLED` variable).

### `native-refresh.yml` — monthly schedule + manual
Checks upstream releases of PDFium, QPDF, resvg, NetVips, Tesseract; opens a PR updating `native/manifest.json`
with new SHA-256s, runs full CI including golden tests; labels `native-refresh`. Golden diffs attached as PR images.

### `codeql.yml`, Dependabot (`nuget`, `github-actions`, weekly, grouped), `dependabot-automerge.yml` for patch bumps when CI is green.

## Workflows in `modit-releases` repo

### `pages.yml` — on `release: published` and `repository_dispatch`
Generates the static download page from the latest stable + beta release JSON (`gh api`): OS/arch detection,
buttons, checksums, release notes, enterprise links; writes `Modit.appinstaller` with the bundle URL of the
latest stable; publishes `rollout.json` (see staged rollout); copies admin docs HTML built in the source repo
(uploaded as a release asset `docs-site.zip`). Deployed with `actions/deploy-pages`.

### `winget.yml` — on `release: published` (non-prerelease) or dispatch
Runs `vedantmgoyal9/winget-releaser` (or `wingetcreate update --submit`) twice: `Modit.Modit` (Velopack exe,
`installerType: exe`, silent switch `--silent`, `installerSwitches.custom: --installto`), and `Modit.Modit.MSI`
[M6]. Opens PRs against `microsoft/winget-pkgs` from the fork using `WINGET_PAT`. Moderation takes hours to days;
the download page never depends on winget.

### `issues` — issue forms with a "Collect diagnostics" instruction; `stale` bot; labels synced from source repo.

## Signing

- **Azure Trusted Signing** account + certificate profile (Public Trust). Identity validation is done once by the
  owner (organization). GitHub OIDC federated credential on an Entra app registration restricted to
  `repo:<owner>/modit:ref:refs/tags/v*` (and `environment:release`). Role `Trusted Signing Certificate Profile
  Signer` on the account.
- Signed: every `.exe`/`.dll` we build, third-party native DLLs (re-signed after hash verification; original
  signatures, if any, are replaced; recorded in SBOM), Velopack `Setup.exe`/`Update.exe`/`.nupkg` contents, MSI,
  Burn bundle (with `insignia`-equivalent `wix burn detach/reattach`), MSIX bundle (publisher must equal certificate
  subject), the sparse package. Timestamp server `http://timestamp.acs.microsoft.com`.
- Dev builds without Azure access: `tools/release/dev-cert.ps1` makes a self-signed cert, build is labeled
  "Unsigned development build" in About and the updater refuses to update from signed channels to unsigned
  (channel `dev` only).
- The updater **verifies Authenticode on every downloaded package's contents before applying** (Velopack applies
  only after extraction; we add a post-download check in `Modit.Update` using `WinVerifyTrust` on `Modit.App.exe`
  inside the nupkg and assert the signer subject equals the embedded expected publisher). Defense in depth against
  a compromised releases repo token.

## In-app updater (`Modit.Update`) [M0]

```csharp
// Program.Main — must run before any other code (Velopack handles install/update/uninstall hooks)
VelopackApp.Build()
    .OnFirstRun(InstallHooks.FirstRun)
    .OnAfterInstallFastCallback(InstallHooks.Register)
    .OnAfterUpdateFastCallback(InstallHooks.Register)
    .OnBeforeUninstallFastCallback(InstallHooks.Unregister)
    .Run();

// UpdateService (singleton, DI)
var source = policy.UpdateUrl is { } mirror
    ? new SimpleWebSource(mirror)                                   // internal mirror (air-gapped), same feed layout
    : new GithubSource("https://github.com/<owner>/modit-releases", accessToken: null, prerelease: channel == Channel.Beta);
var mgr = new UpdateManager(source, new UpdateOptions { ExplicitChannel = $"win-{rid}-{channel}", AllowVersionDowngrade = false });
if (!mgr.IsInstalled) return;                                       // portable / dev run → updater off
var info = await mgr.CheckForUpdatesAsync();                        // null → up to date
if (info is null || !Rollout.IsEligible(info.TargetFullRelease.Version, machineHash, rolloutJson)) return;
await mgr.DownloadUpdatesAsync(info, p => progress.Report(p), ct);  // deltas first, full fallback; verify signature
await SignatureCheck.VerifyDownloadedPackageAsync(mgr, info);       // our Authenticode check
state.PendingVersion = info.TargetFullRelease.Version;              // UI: "Restart to update" InfoBar
// On user action or on exit (setting "Install updates when I close Modit", default on):
mgr.ApplyUpdatesAndRestart(info);  // or mgr.WaitExitThenApplyUpdates(info) on close
```

Behavior:
- Check on startup after 10 s (never delays startup), then every 24 h while running; also on "Check for
  updates". Jitter ±30 min to avoid synchronized bursts.
- Download in background on metered connections only if setting allows (`NetworkInformation` cost check);
  default: wait for unmetered.
- UI: `InfoBar` on Home and a badge on Settings; "Restart now" / "Later" (re-ask in 24 h) / "Skip this version".
  Never auto-restart while documents are open and dirty; apply on exit instead. Release notes dialog shows the
  `RELEASE_NOTES.md` section fetched from the release body (cached).
- Deferral policy `UpdateDeferDays` (0–30): the app waits N days after a release before offering it, so
  enterprises using the per-user flavor get a buffer.
- Pending update + user closes app → `WaitExitThenApplyUpdates` applies silently; next launch shows "Updated to X".
- Rollback: Settings → About → "Previous version" lists the prior full package still in `packages\` (we keep the
  last 2 full packages; Velopack keeps 1 by default, so our hook copies the previous one to `packages\rollback\`).
  Clicking reinstalls it via `Update.exe --apply` with the old nupkg and sets `AllowVersionDowngrade` for one
  launch; then the updater skips the bad version (`SkippedVersions` setting) until a newer one appears.
- Emergency kill switch: `rollout.json` can set `"blocked": ["1.4.2"]` → clients on that version are offered the
  previous stable (pipeline re-publishes the previous full package under a new patch tag, e.g. `1.4.3` =
  rebuilt `1.4.1`; downgrade offers are never automatic).
- CLI: `modit update check` (prints JSON with current, available, channel, feed), `modit update apply` (downloads
  and applies, exit code 8 on error), `modit update channel beta|stable`.
- MSI flavor: `mgr.IsInstalled` is false (not a Velopack install) → `MsiUpdateNotifier` queries the same GitHub
  release JSON (`/repos/<owner>/modit-releases/releases/latest`), compares versions, shows notify-only InfoBar with
  release notes and a "Download MSI" link; if `AllowUserUpdatesForMachineInstall=1` and the user is an admin,
  "Install" downloads the MSI, verifies signature, runs `msiexec /i … /passive` (UAC) and relaunches.
- MSIX flavor: updater UI hidden; App Installer handles it. Store builds: hidden.
- Policies: `UpdateCheck` (0 disables everything including MSI notifier), `UpdateChannel`, `UpdateUrl` (mirror
  base), `UpdateDeferDays`, `UpdateMeteredAllowed`, `UpdateAutoApplyOnExit`.

## Staged rollout [M6]

`rollout.json` on GitHub Pages: `{ "stable": { "version": "1.4.2", "percent": 25, "blocked": [] } }`.
Client eligibility: `percent >= (SHA-256(machineGuid + version) % 100)`. The release pipeline publishes with
`percent: 10` by default for stable (configurable via tag annotation or workflow input), and a manual
`rollout.yml` dispatch raises it to 50 and 100; betas are always 100. Clients that are not yet eligible see
nothing. If `rollout.json` is unreachable, clients treat it as 100 (availability over caution; the kill switch
is for emergencies and is also cached locally for 7 days).

## Engine packs on GitHub [M5]

- `modit-packs` releases: one release per pack version, tag `office-libreoffice/24.8.3`, assets `pack.zip`
  (≤ 2 GB), `manifest.json`, `manifest.json.sig` (Ed25519, key in app). Index `packs/index.json` on Pages lists
  pack id → versions → compatible app version range → asset URLs → SHA-256 → license → size.
- Downloader: resumable (HTTP Range) to `%LOCALAPPDATA%\ModitData\engines\.downloads`, verifies SHA-256 and
  signature, extracts with the archive safety rules, atomically renames into place.
- Mirror: `tools/mirror-release.ps1 -Dest \\server\share\modit` downloads a release and selected packs into a
  folder laid out exactly as the feeds expect (`releases.<channel>.json`, nupkgs, `packs/index.json`, MSI,
  appinstaller rewritten to the mirror URL). Point `UpdateUrl` and `EnginePackSource` at a file share or internal
  web server. Fully offline operation verified in the install matrix.

## Release notes and changelog

- Conventional commits → `git-cliff` with `cliff.toml` grouping: ✨ Features, 🐛 Fixes, ⚡ Performance,
  🔒 Security, 📦 Dependencies, 🧰 Enterprise/Admin. Breaking changes section from `!` commits.
- `RELEASE_NOTES.md` bundled in the app (for offline What's new) and used as the GitHub release body.
- Release body also contains: SHA-256 table, minimum OS, known issues (from `docs/release-notes/<version>.md` if
  present, authored by hand for stable releases), and the enterprise deployment link.

## Release checklist (stable)

1. All nightly gates green for 3 consecutive nights; no open P0/P1.
2. `docs/release-notes/<version>.md` written (known issues, upgrade notes); `version.json` bumped by nbgv.
3. Tag `v<version>` on `main`; `release.yml` runs; approve the `release` environment gate.
4. `verify` job green (fresh install + update path from previous stable and from latest beta).
5. Download page updated (pages.yml), winget PRs opened (winget.yml), announce.
6. Monitor GitHub issues for 48 h at `percent: 10`, raise rollout to 50 then 100 via `rollout.yml`.
7. Hotfix if needed via tag; kill switch via `rollout.json` if severe.

## Testing the pipeline itself [M0]

- `tools/release/verify-update-path.ps1 -From <tag> -To <tag>`: clean user profile, install From, launch with
  `MODIT_UPDATE_CHECK_NOW=1 MODIT_UPDATE_FEED=<local or github>`, wait for process restart, assert `--version`
  = To, assert settings preserved, assert associations present, uninstall, assert clean (uses
  `verify-clean-uninstall.ps1`).
- `tools/release/local-feed.ps1`: builds two consecutive versions locally and serves a Velopack feed from a
  folder (`SimpleWebSource` over `file://` or a local Kestrel) so the updater is testable without GitHub.
- Unit tests for `Rollout.IsEligible`, version comparison across channels, `SkippedVersions`, deferral math,
  signature verification (tampered nupkg fixture → refused).
- `release.yml` has a `workflow_dispatch` dry-run input that builds and signs but publishes to a draft release in
  a `modit-releases-staging` repo (or the same repo as draft) for pipeline changes.

## Failure modes and handling

| Failure | Handling |
|---------|----------|
| GitHub API rate limit (403 with `X-RateLimit-Remaining: 0`) | Back off to next day; never retry in a loop; log once |
| Release published without one asset (partial upload) | `verify` job fails → release flipped to draft automatically; clients ignore drafts |
| Delta apply fails | Velopack falls back to full package automatically; we log and count |
| Signature mismatch on downloaded package | Refuse, delete, show "Update could not be verified" with link to release page; report in diagnostics |
| Mirror unreachable (enterprise) | Silent; InfoBar only after 14 days without a successful check when `UpdateWarnStale=1` |
| App killed during apply | Velopack's `Update.exe` is transactional (swaps `current\`); next launch runs the newest valid version |
| Trusted Signing outage during release | `release.yml` fails before publish; rerun later; nothing half-published |
