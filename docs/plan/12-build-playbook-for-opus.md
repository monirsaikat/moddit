# 12 — Build Playbook for the Implementing Model (Opus)

This file is the execution contract. Read it last, after `README`, `01`, `02`, `03`, `09`. Then work through
the task list in order. Each task is sized to be finished, built, tested and run in one focused session.

## Operating rules

1. **Build the real thing.** No mock engines left in production code paths. Stubs are allowed only behind
   `IEngineAvailability` for missing engine packs, with a user-facing "install pack" path.
2. **Verify before claiming done.** Run `dotnet build` for x64, run the relevant tests, launch the app (or CLI)
   and exercise the feature on a fixture. Paste the command outputs into the task's PR/notes.
3. **Follow the plan; log deviations.** If an API named here does not exist or a library breaks, find the closest
   equivalent, record an ADR in `docs/adr/NNNN-*.md` (10 lines is fine), update the plan file, continue.
4. **Never change LOCKED decisions** without an ADR that the owner explicitly approves.
5. **Keep CLI parity.** Every Operation gets a CLI subcommand in the same task.
6. **Small commits, conventional messages.** One task → one branch → one squash merge.
7. **Fixtures first.** Before implementing a feature, add or generate the fixture(s) it will be tested on.
8. **No AGPL. Ever.** Check every new dependency's license against ADR-018 before adding.
9. **Strings, accessibility, dark mode, keyboard** are part of the task, not polish later.
10. **Ask the owner only via `docs/plan/13-open-questions-and-risks.md` updates**; otherwise proceed with the
    documented default assumption.

## Environment setup (once)

```
winget install Microsoft.DotNet.SDK.10 Microsoft.VisualStudio.2022.BuildTools  # with "Windows App SDK C# Templates", "Windows 11 SDK (10.0.26100)", MSVC + Windows SDK for native fetch scripts
winget install Microsoft.WindowsAppRuntime.1.7   # or latest stable
winget install wixtoolset.wixtoolset              # WiX v5 (dotnet tool alternative: dotnet tool install --global wix)
dotnet tool install --global nbgv
dotnet tool install --global vpk                    # Velopack CLI
dotnet tool install --global git-cliff              # or download binary; changelog from conventional commits
winget install GitHub.cli                           # gh, for release automation scripts
tools/fetch-native.ps1 -Rid win-x64,win-arm64
```

Verify: `dotnet --version` is 10.x; `native/bin/win-x64/pdfium.dll` exists; `dotnet build Modit.sln -p:Platform=x64` succeeds.

## Task list

Tasks are numbered `M<milestone>.<n>`. Each lists: **Goal**, **Touches**, **DoD** (in addition to the global
Definition of Done in 09).

### M0 — Foundations

- **M0.1 Repo skeleton** — Goal: solution, all projects empty but compiling; `Directory.Build.props` with
  analyzers, nullable, warnings-as-errors, `Directory.Packages.props`, `.editorconfig`, `global.json`, `CLAUDE.md`,
  CI building x64 + ARM64. Touches: root, `src/*`, `.github/workflows/ci.yml`. DoD: CI green on empty projects.
- **M0.2 Native fetch** — Goal: `native/manifest.json` + `tools/fetch-native.ps1` downloading PDFium
  (bblanchon, no V8), QPDF (official release zip with DLLs), resvg (C API release) for both RIDs; SHA-256 verify;
  MSBuild target copying to `$(OutDir)native\<rid>\`. DoD: clean clone → script → build → DLLs next to outputs.
- **M0.3 PDFium bindings** — Goal: `Modit.Native.Pdfium` with `LibraryImport` for the curated subset (04),
  SafeHandles, `PdfiumLibrary.Init/Destroy`, resolver. Tests: load fixture, page count, size, render page to BGRA
  → encode PNG via a tiny test helper → compare dimensions. DoD: tests pass x64 and ARM64 (ARM64 may run on
  emulation/CI runner).
- **M0.4 QPDF bindings** — Goal: `Modit.Native.Qpdf` for `qpdfjob-c` + `qpdf-c` memory in/out, logger callbacks
  (progress + warnings). Tests: merge two fixtures; encrypt/decrypt; `--check` on damaged fixture reports
  warnings. DoD: as above.
- **M0.5 resvg bindings** — Goal: `Modit.Native.Resvg` parse+render; system fonts. Test: render fixture SVG to
  RGBA 512px. DoD: as above.
- **M0.6 IPC layer** — Goal: `Engine.Contracts` (interfaces `IRenderService`, `IDocumentService`, `IStructureService`,
  DTOs), `Engine.Host` exe (StreamJsonRpc server over named pipe; MessagePack; shared memory ring; PDFium thread
  scheduler), `Engine.Client` (host launcher with Job Object + restricted token, pool, heartbeat, respawn, request
  re-issue). Tests: open/render/close via client; kill host → respawn → success; 10k RPC latency. DoD: tests;
  `Engine.Host` publishes Native AOT without warnings.
- **M0.7 Core skeleton** — Goal: `Operation` records for M2 set, `IOperationHandler`, `Job`, in-memory
  `JobScheduler` with priorities + cancellation, `ErrorCode` enum + `ModitException`, `ISettingsService` (JSON,
  versioned), `IPolicyProvider` stub reading registry, logging setup, DI extension methods. Tests: scheduler
  ordering/cancellation; settings migration. DoD: tests.
- **M0.8 App shell** — Goal: WinUI window with custom title bar + `TabView`, Mica, left rail `NavigationView`,
  Home page with drop zone (non-functional actions), Settings page shell with theme switch, light/dark/high
  contrast, DPI change handling, window placement persistence, single-instance activation, unpackaged + packaged
  profiles. DoD: app launches both ways; FastPass zero errors; startup ≤ 1.5 s measured with
  `tools/perf/measure-startup.ps1` (create it).
- **M0.9 CLI skeleton** — Goal: `Modit.Cli` with System.CommandLine, `--version`, `--json`, `pdf info` using
  Core + Engine.Client; exit code table; Native AOT publish. Tests: process tests. DoD: `modit pdf info
  fixtures/pdf/text-simple.pdf --json` returns valid JSON.
- **M0.10 Release pipeline v0 + in-app updater** — Goal: everything in 15 marked "M0": `tools/build.ps1
  -Package velopack|portable|cli`, `vpk pack` with icon/splash/shortcuts/associations/`--framework webview2`,
  Azure Trusted Signing via OIDC (or a self-signed dev cert when secrets are absent, clearly labeled "unsigned
  dev build"), `release.yml` triggered by `v*` tags publishing to the public `modit-releases` repo with deltas
  (`vpk download github` first), release notes via git-cliff, `beta` channel on pre-release tags, `Modit.Update`
  module (`VelopackApp.Build().Run()` first line of `Main`, `UpdateManager` + `GithubSource`, check on startup
  after 10 s and every 24 h, download in background, "Restart to update" InfoBar, Settings → About → Check for
  updates, release notes dialog, `modit update check|apply`), policy stubs `UpdateCheck/UpdateChannel/UpdateUrl`,
  portable-mode detection disables updater, `tools/release/verify-update-path.ps1`. DoD: the M0 exit criterion
  about beta.1 → beta.2 delta update passes on a clean VM.
- **M0.11 First-run and install polish** — Goal: everything in 14 marked "M0": first-run page (theme, default
  app offer per policy, engine packs teaser), prerequisite check (WebView2, WinAppSDK runtime when unpackaged),
  "Repair installation" and "Reset settings" in Settings, clean uninstall hook (Velopack `--veloapp-uninstall`
  handler removing registry associations, context menu sparse package, scheduled task), migration of settings
  between portable and installed, install telemetry none. DoD: install → first run → uninstall leaves no
  registry/filesystem residue except user data when `Keep my settings` chosen (test script).

### M1 — Viewer

- **M1.1 DocumentCanvas core** — Win2D `CanvasVirtualControl`; page layout engine (continuous/single/facing);
  viewport math; tile keys/buckets; placeholder drawing; input (wheel, ctrl+wheel anchored zoom, pinch, drag-pan
  with space, keyboard). Tests: layout math property tests. DoD: scroll a 1000-page fixture smoothly with gray
  page rectangles and page numbers.
- **M1.2 Tile pipeline** — Client coalescer, priorities, cancellation flags, shared-memory upload to
  `CanvasBitmap`, `TileCache` tiers + budgets, low-res preview tile, crossfade. DoD: budgets for scroll/zoom
  pass; perf overlay shows cache stats.
- **M1.3 Open flows** — File picker, drag-drop, args, protocol, recent files (SQLite), password prompt, repair
  offer, document tab lifecycle, dirty/close prompts (no edits yet), error InfoBars. DoD: all fixtures open or
  fail with specific messages.
- **M1.4 Thumbnails + Outline panes** — Virtualized strip with disk cache, click-to-navigate, current page sync;
  Outline tree from PDFium bookmarks with page-dest resolution; page labels in navigator. DoD: budgets.
- **M1.5 Text layer, selection, search** — Host text service (char boxes, words/lines clustering), selection
  overlay + copy, find bar + Search pane with results list, regex option, highlight overlays, link hit-test with
  URL confirmation. DoD: search budgets; selection works on rotated pages and CJK fixture.
- **M1.6 Print** — `PrintManager` integration, preview, ranges, scale, N-up, GDI vector path via host render to
  EMF/HDC or raster bands fallback. DoD: prints to "Microsoft Print to PDF" and output opens correctly.
- **M1.7 Image and SVG viewer tabs** — Same canvas, single page, libvips decode in host (shared memory), resvg
  render on zoom; EXIF orientation; animated preview (first frame + play toggle P2). DoD: all image fixtures open.
- **M1.8 Golden tests + perf harness** — Rendering golden tests (20 pages), E2E bench project with budget table
  and baseline JSON, nightly workflow. DoD: nightly green.

### M2 — Page operations

- **M2.1 EditLog + undo/redo + recovery journal** — Core model, virtual page mapping used by canvas, journal
  writes, restart recovery prompt. Tests: property tests. DoD: reorder/rotate/delete in Pages mode reflect
  instantly in viewer with undo.
- **M2.2 Pages mode UI** — `PageOrganizerGrid` (ItemsRepeater), multi-select, rubber band, drag reorder, hover
  toolbar, insert blank/from file, cross-tab drag, range box with grammar + live count, zoom slider. DoD: UI
  tests for reorder + delete.
- **M2.3 Save pipeline** — EditLog→qpdfjob compiler, outlines/labels remap, PDFium incremental save for
  content edits (later), atomic replace, verification, Save/Save As/Save Copy, crash injection tests. DoD: tests.
- **M2.4 Merge + Split + Extract tools** — Merge dialog, Split pane (modes), Extract, output naming, open result
  option; Operation handlers + CLI subcommands. DoD: round-trip preservation tests; budgets.
- **M2.5 Metadata, repair, linearize, properties pane** — QPDF JSON read/write; Properties dialog with
  security info and permissions; `pdf metadata|repair|linearize` CLI. DoD: tests.
- **M2.6 Job scheduler persistence + Queue flyout + audit v0** — SQLite queue, resume on start, Queue UI,
  cancellation, basic JSONL audit. DoD: kill app mid-batch → resumes on start.

### M3 — Conversion engine

- **M3.1 Convert registry, sniffing, naming, options schema** — Core pieces + JSON schema generation + unit
  tests + `OutputNamer` tokens + collision policies. DoD: tests.
- **M3.2 Images converter (libvips)** — All P0 formats/ops, animated handling, ICO, HEIC via WIC fallback,
  metadata controls; host-side execution with streaming; `convert` CLI. DoD: matrix tests; 100-image budget.
- **M3.3 Vector converter (resvg + WebView2)** — SVG→raster, SVG→PDF vector; `WebView2Printer` service for App
  and CLI (message-only HWND); HTML/MD/TXT→PDF with print CSS and settings. DoD: vector check test; CLI works
  headless.
- **M3.4 PDF↔images + PDF→TXT converters** — Page-parallel across hosts; options (DPI, color, range, transparent
  PNG); images→PDF layout options and EXIF; TXT extraction with reading-order v0. DoD: tests + budgets.
- **M3.5 Convert Hub + FormatPicker + OptionsFormControl + Queue tab** — Full UI, inline pack-missing cards
  (packs come in M5; show "coming" state wired to `IEngineAvailability`), toasts, open folder, retries. DoD: UI
  smoke test converts fixtures.
- **M3.6 Recipes v0 + context menu (sparse package)** — Single-operation recipe JSON, `recipe run` CLI,
  `Modit.Shell` IExplorerCommand for "Convert with Modit ▸" and "Open with Modit", classic registry menu for
  Win10. DoD: menu appears on Win11 packaged install; actions launch the app with files.

### M4 — PDF tools

- **M4.1 Annotation model + render + create/edit/delete** — Highlight/underline/strike/squiggly from selection,
  note, free text (own AP), shapes, properties pane, comments pane, PDFium save path integrated with EditLog
  content edits, undo. DoD: golden + schema tests; Acrobat opens output (manual checklist item).
- **M4.2 Ink + stamps + flatten** — InkCanvas overlay → ink annots; stamp library (SVG→AP) + custom image
  stamps; flatten command; `pdf flatten|stamp` CLI. DoD: tests.
- **M4.3 Forms** — Field enumeration, WinUI editors overlay, value writing + AP regeneration, flatten forms,
  FDF/XFDF/JSON/CSV import/export, batch fill CLI. DoD: round-trip tests.
- **M4.4 Watermark / header-footer / page numbers / Bates** — `PageStampLayout`, font embedding via
  `FPDFText_LoadFont`, live preview overlay, batch across documents with continuous Bates, CLI. DoD: golden tests.
- **M4.5 Protect / unlock / permissions / sanitize v0** — QPDF encrypt/decrypt, legal notice UX, permissions
  display, sanitize (JS, actions, embedded files), CLI. DoD: tests incl. opening encrypted output in Edge (manual).
- **M4.6 Compress** — Image enumeration/replacement pipeline with libvips, presets, estimator, report, CLI.
  DoD: ≥ 60% on scanned fixture with SSIM ≥ 0.92; budget.
- **M4.7 OCR (Windows OCR)** — Preprocess (libvips), `Windows.Media.Ocr` in host (WinRT from AOT host — if
  problematic, run OCR in UI-process background service; record ADR), invisible text layer insertion, skip-if-
  text, language picker, CLI. DoD: accuracy test; search works on OCR output.
- **M4.8 Bookmarks editor, attachments, page labels, crop, insert image, extract images** — QPDF JSON edits +
  PDFium page objects; crop handles overlay; CLI for each. DoD: tests.
- **M4.9 Recipes v1 (chains) + Recipe runner UI** — Multi-step recipes with parameter binding, import/export,
  UI to compose from existing tools (list-based, not visual graph). DoD: recipe chaining OCR→compress→watermark
  runs from GUI and CLI.

### M5 — Engine packs and remaining converters

- **M5.1 Pack manager** — Manifest format, Ed25519 manifest signature, download with resume + SHA-256, per-user/
  per-machine install, offline install, removal, Settings → Converters page, inline install cards, policies.
  DoD: tests with a local HTTP fixture server.
- **M5.2 LibreOffice + Office COM adapters** — Isolated profiles, filter map, concurrency, timeouts, pre-warm,
  `Modit.OfficeHost` with watchdog, policy gate, converters for DOCX/XLSX/PPTX/ODT/RTF→PDF and office↔office.
  DoD: fixtures convert; 200-run stability test (nightly).
- **M5.3 Pandoc + own document converters + email + EPUB** — Pandoc adapter; DOCX→TXT/MD own; EML/MSG→PDF;
  EPUB→PDF. DoD: text expectations tests.
- **M5.4 Data converters** — CSV/XLSX/JSON/YAML/XML/TOML/Markdown-table; encodings; large-file streaming.
  DoD: property tests on round trips where lossless.
- **M5.5 Media converters (FFmpeg)** — Pack, probe, presets, hardware encoder detection, progress, trim,
  extract audio, GIF; UI preset chips with custom options. DoD: ffprobe assertions.
- **M5.6 Archive converters** — Create/extract/convert with safety limits; 7-Zip pack for 7z write/RAR; "images
  in ZIP → PDF" path. DoD: malicious fixtures rejected.
- **M5.7 Tesseract engine** — Pack + bindings + language packs; hOCR/TSV; engine selection in OCR UI/CLI. DoD:
  accuracy test ≥ Windows OCR on fixtures.
- **M5.8 Watch folders** — Per-user background task + optional service; config, stability detection, ledger,
  error folder, `modit watch` CLI, Settings page. DoD: 100-file drop test.

### M6 — Enterprise

- **M6.1 Enterprise packaging** — MSIX bundle + appinstaller published to GitHub Pages; sparse package for shell
  (registered by MSI custom action and by Velopack post-install hook); WiX MSI with features/properties; Burn
  bundle with WebView2 chain; `tools/build.ps1 -Package msix|msi|bundle`; install matrix script on Win10 22H2 and
  Win11 VMs. DoD: matrix passes; MSI → MSI major upgrade keeps settings; Velopack ↔ MSI coexistence rules enforced
  (installer refuses to install the other flavor and explains).
- **M6.2 Enterprise update paths + winget + mirror** — MSI notify-only updater and admin self-update path;
  `winget.yml` opening PRs for both package ids on stable releases; `tools/mirror-release.ps1` producing an
  internal mirror (Velopack feed + appinstaller + MSI + packs) for `UpdateUrl`; staged rollout `rollout.json`
  honored by the updater; third-party notices generator from manifest + NuGet; appinstaller update test.
  DoD: signed artifacts verify with `signtool verify /pa`; mirror install + update works offline.
- **M6.3 Policy module** — Catalog, registry reader, overlay into options, managed badges, `policy show`, every
  gate from 08 wired, generated `docs/admin/policies.md` + ADMX/ADML. DoD: per-policy tests.
- **M6.4 Licensing** — License format, verifier, activation flows (online stub endpoint contract documented,
  offline files, volume), trial, feature gates in Core, Settings page, CLI. DoD: gate tests; tamper tests.
- **M6.5 Audit complete + Event Log** — Rotation, UNC, redaction options, event log provider, `audit export`.
  DoD: tests.
- **M6.6 Shell integration complete + admin docs** — Associations, jump list, protocol, default handler prompt
  policy, Win10 classic menu; deployment/CLI/packs/security/troubleshooting docs. DoD: docs reviewed; menus work.

### M7 — Advanced PDF

- **M7.1 Incremental writer + signatures (sign)** — `PdfIncrementalWriter` (xref stream, object append), sig
  field + appearance, CMS via `SignedCms`, cert picker (store + PFX), RFC 3161 timestamp, CLI. DoD: Acrobat +
  Edge validity (manual matrix with screenshots), openssl verify test.
- **M7.2 Signature verification + Signatures pane** — Parse sigs, verify CMS/chain/byte ranges/timestamps,
  status UI. DoD: tampered fixture detection tests.
- **M7.3 Redaction** — Marks (manual/selection/search patterns), apply engine (text split, image blackout, path
  removal, overlay), rasterize fallback, report, verification pass, confirmation UX, CLI. DoD: zero-residue tests.
- **M7.4 Compare** — Visual diff + text diff, Compare tab, report export. DoD: synthetic change detection test.
- **M7.5 Semantic text engine → MD/HTML/DOCX/XLSX** — Reading order, headings, lists, tables (ruling +
  whitespace), DOCX via OpenXML, XLSX tables, HTML flowing/absolute. DoD: expectation tests on fixtures; labeled
  "heuristic" in UI.
- **M7.6 Object-level edit + imposition + extras** — Move/scale/delete objects, text string edit same-font,
  replace image; N-up/booklet; blank-page split; bookmarks auto-generate. DoD: golden tests.

### M8 — Hardening and release

- **M8.1 Perf pass** — Run full budgets on all profiles; fix; soak test 8 h; leak test. DoD: green.
- **M8.2 Fuzz campaign** — 24 h per entry point; fix crashers; add regressions. DoD: zero UI-affecting crashes.
- **M8.3 Accessibility + localization pass** — Narrator walkthrough, FastPass, pseudo-loc snapshots, string
  freeze, copy edit. DoD: audit doc signed.
- **M8.4 Release engineering** — Beta channel, crash reporting opt-in endpoint, release notes, Store assets,
  rollback runbook, 1.0 tag. DoD: artifacts published and installable from winget.

## Session template for each task

```
1. Read the task line in 12, the relevant plan section(s), and CLAUDE.md.
2. Add/generate fixtures. Write failing tests for the DoD.
3. Implement. Keep CLI parity in the same change.
4. dotnet build -p:Platform=x64 (and ARM64 at least once per milestone).
5. dotnet test <affected projects>. Run the app/CLI and exercise the feature on fixtures.
6. Update docs (plan deviations → ADR). Conventional commit. Report: what changed, test output, how to verify.
```

## Prompt scaffolds (copy-paste for each session)

> You are implementing task **M0.3 PDFium bindings** of the Modit plan. Read `docs/plan/README.md`,
> `docs/plan/02-architecture.md`, `docs/plan/03-technology-decisions.md`, `docs/plan/04-pdf-engine.md`
> (sections "Native binding layer", "Rendering details"), `docs/plan/09-repository-structure-and-conventions.md`,
> and the task's DoD in `docs/plan/12-build-playbook-for-opus.md`. Do not change LOCKED decisions. Produce
> working code, tests and run them. Finish by reporting commands run and their results.

Replace the task id and file list for each session. For UI tasks add `06-ui-ux-and-design-system.md`; for
converters `05-conversion-engine.md`; for packaging/policy `08-enterprise-security-deployment.md`; for perf
tasks `07-performance-engineering.md`; for tests `11-testing-and-quality.md`; for install/first-run
`14-installation-and-first-run.md`; for release/update work `15-release-and-auto-update-github.md`.

## Releasing during development

Every milestone ends with a beta release so the installer and updater are exercised continuously:

```
git tag v0.<milestone>.0-beta.1 && git push --tags      # release.yml builds, signs, publishes to modit-releases (beta channel)
tools/release/verify-update-path.ps1 -From v0.1.0-beta.1 -To v0.2.0-beta.1   # installs old, waits for update, asserts new version
```

Stable releases are tags without a pre-release suffix and additionally trigger winget PRs and the stable
Velopack channel. Never publish a release manually from the GitHub UI; the pipeline is the only publisher.
