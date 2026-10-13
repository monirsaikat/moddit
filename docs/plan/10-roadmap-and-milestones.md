# 10 — Roadmap and Milestones

Milestones are sequential. Each has **exit criteria** that must all pass before the next starts. Effort is given
in "focused engineering days" (FED) for a single strong engineer or an implementing model working with review;
calendar time depends on review cadence. Total to 1.0 ≈ 150–190 FED.

| Milestone | Theme | FED |
|-----------|-------|-----|
| M0 | Foundations: repo, native bindings, engine host, IPC, CI, **signed installer + GitHub auto-update pipeline** | 15–18 |
| M1 | Viewer MVP: open, render, scroll/zoom, thumbnails, search, print | 18–22 |
| M2 | Page operations + merge/split + save pipeline + CLI core | 15–18 |
| M3 | Conversion engine: images, SVG, PDF↔images, text/MD/HTML→PDF, Convert Hub, Queue | 18–22 |
| M4 | PDF tools: annotations, forms, watermark/header/Bates, protect, compress, OCR, metadata, bookmarks | 25–30 |
| M5 | Engine packs: LibreOffice, FFmpeg, Pandoc, 7-Zip, Tesseract; data & archive converters; recipes; watch folders | 18–22 |
| M6 | Enterprise: MSIX/MSI/ADMX, licensing, audit, policies, updates, shell integration, docs | 15–18 |
| M7 | Advanced PDF: signatures, redaction, sanitize, compare, PDF→DOCX/XLSX | 20–25 |
| M8 | Hardening: perf budgets, fuzzing, accessibility audit, localization, 1.0 release | 12–15 |

---

## M0 — Foundations

Deliverables:
- Solution skeleton per 09; `Directory.Build.props`, analyzers, central packages, `global.json`.
- `tools/fetch-native.ps1` + `native/manifest.json` pulling PDFium, QPDF, resvg (x64 + arm64) with SHA-256.
- `Modit.Native.Pdfium` / `.Qpdf` / `.Resvg` bindings (curated subsets) with smoke tests (load PDF, page count,
  render 1 page to PNG; qpdfjob merge two files; resvg render SVG).
- `Modit.Engine.Contracts/Client/Host`: named pipe JSON-RPC, shared-memory slots, host pool with respawn,
  Job Object + restricted token launcher, heartbeat, idempotent `OpenDocument/RenderTile/Close` calls.
- `Modit.Core` skeleton: `Operation`, `IOperationHandler`, `Job`, `IJobScheduler` (in-memory), `ErrorCode`,
  settings service, logging.
- `Modit.App` WinUI shell: window with Mica, `TabView` in title bar, left rail, empty Home, Settings page shell,
  theme switching; unpackaged and packaged launch profiles.
- `Modit.Cli` skeleton with `modit --version`, `modit pdf info <file>` (page count, size, encryption) via host.
- CI: build x64 + ARM64, unit tests, license scan, artifact upload.
- **Release pipeline v0 (continuous delivery from day one)**: `release.yml` on tag → signed Velopack installers for
  x64/arm64 published to the public `modit-releases` GitHub repo with delta packages, release notes from
  conventional commits, `beta` channel from pre-release tags; in-app updater (`Modit.Update` with Velopack
  `GithubSource`) with "Check for updates" in Settings and update toast; portable zip; CLI zip; checksums; SBOM.
  Every later milestone ends with a tagged beta release that installs and self-updates. See 15.

Exit criteria:
- `modit pdf info` works on all fixtures; killing a host mid-render results in automatic respawn and successful
  retry (test). CI green. App launches in < 1.5 s on Baseline (measured, R2R).
- Tag `v0.1.0-beta.1` produces a signed `ModitSetup-x64.exe` on GitHub Releases; installing it, then tagging
  `v0.1.0-beta.2`, results in the running app offering and applying a delta update and restarting into the new
  version (recorded as a test script `tools/release/verify-update-path.ps1`).

## M1 — Viewer MVP

Deliverables:
- `DocumentCanvas` (Win2D) with tile pipeline, zoom buckets, placeholders, continuous/single/facing layouts,
  rotation, smooth scroll, ctrl+wheel/pinch zoom, fit modes, page navigator, status bar.
- Thumbnails pane (virtualized, disk cache), Outline pane, Search pane + inline find bar with highlights.
- Text layer: selection, copy, link hit-testing with confirmation on external URLs.
- Open: file picker, drag-drop, command line, protocol activation, single-instance redirect, recent files,
  password prompt for encrypted PDFs, repair offer for damaged files.
- Print via `PrintManager` with ranges/scale/N-up (GDI vector path + raster fallback).
- Image viewer tab (PNG/JPEG/WebP/… via libvips in host) and SVG viewer (resvg) using the same canvas.
- Perf overlay; ETW events; startup measurement script.

Exit criteria:
- Budgets for open/scroll/zoom/thumbnail/search from 07 pass on Baseline. 1000-page and A0 fixtures usable.
- Accessibility FastPass zero errors on viewer screens. Golden rendering tests for 20 fixture pages pass on
  x64 and ARM64.

## M2 — Page operations, merge/split, save, CLI core

Deliverables:
- `EditLog` + undo/redo + recovery journal; Pages mode grid with drag reorder, rotate, delete, duplicate,
  insert blank/from file, cross-tab drag merge; range grammar (`PageRangeBox`).
- `EditLog → qpdfjob` compiler; outlines/page-labels remap; atomic save pipeline with verification; Save As;
  "Save a copy"; dirty-state and close prompts; autosave recovery on restart.
- Merge dialog; Split tool (ranges, every N, by bookmark level, by size, odd/even; blank-detection P1 later);
  Extract pages; Metadata editor (read/write via QPDF JSON); Repair/linearize commands.
- CLI: `pdf merge|split|extract|rotate|delete|info|repair|linearize|metadata`, `--json`, exit codes; CLI tests.
- Job scheduler with priorities, cancellation, persistence (SQLite), Queue flyout (minimal).
- Audit log writer (basic) and ErrorCode catalogue doc generation.

Exit criteria:
- Merge/split/reorder budgets pass; round-trip tests show outlines, labels, forms, annotations preserved across
  merges; property-based tests on range grammar; CLI parity for all M2 operations; power-loss simulation (kill
  during save) never corrupts the original.

## M3 — Conversion engine (core domains) + Convert Hub + Queue

Deliverables:
- `Modit.Convert` registry, sniffing, options schema generation, `OptionsFormControl`, `FormatPicker`,
  output naming engine, collision policies, path finding.
- `Convert.Images` (libvips: all P0 formats and ops; animated; ICO; HEIC via WIC fallback), `Convert.Vector`
  (resvg; SVG→PDF via WebView2), `Convert.Pdf` (PDF→images, images→PDF, PDF→TXT), `Convert.Documents.Html`
  (WebView2Printer: HTML/MD/TXT→PDF).
- Convert Hub tab; Queue tab (full); toasts; "Open folder"; per-file errors and retry.
- CLI `convert` with full options; recipes v0 (single operation JSON).
- Explorer context menu "Convert with Modit ▸" (sparse package) — may slip to M6 if identity issues arise.

Exit criteria:
- Format matrix tests: every advertised edge converts the fixture and output re-sniffs as the target format
  with expected dimensions/page count. 100-image batch budget passes. Memory bounded test passes (1,000 images).

## M4 — PDF tools

Deliverables:
- Annotations: highlight/underline/strike/squiggly, note, free text, shapes, ink (InkCanvas), stamps; Comments
  pane; properties pane; flatten; author default.
- Forms: fill with WinUI editors overlay, flatten, FDF/XFDF/JSON/CSV import/export.
- Watermark, header/footer, page numbers, Bates (live preview, page ranges, batch across docs).
- Protect/unlock (AES-256, permissions, legal notice), permissions display, sanitize (P1 subset).
- Compress with presets and estimate; report dialog.
- OCR (Windows OCR) → searchable PDF; language selection; skip-if-text; OCR preview text pane.
- Bookmarks editor; attachments pane; page labels editing; crop tool; insert image; extract images.
- CLI parity for all; recipes v1 (multi-step chains) + Recipe runner UI.

Exit criteria:
- Each tool has golden tests (render after operation) and structural tests (QPDF `--check` passes, Acrobat-
  compatible annotation dictionaries validated against a schema of required keys). OCR accuracy ≥ 95% word
  match on synthetic 300-dpi fixtures. Compress achieves ≥ 60% reduction on scanned fixture at Balanced with
  SSIM ≥ 0.92 vs original render.

## M5 — Engine packs and remaining converters

Deliverables:
- `Modit.Packs` manager (manifest, download with resume, verify, install per-user/per-machine, offline install
  CLI, license display), Settings → Converters page, inline install cards.
- Packs: `office-libreoffice`, `media-ffmpeg`, `docs-pandoc`, `archive-7zip`, `ocr-tesseract` + languages.
- `Convert.Documents` (LibreOffice adapter, Office COM adapter + `Modit.OfficeHost`, Pandoc, DOCX→TXT/MD own,
  email → PDF, EPUB → PDF), `Convert.Data` (CSV/XLSX/JSON/YAML/XML/TOML, encodings), `Convert.Media` (presets,
  hardware encoder detection, trim, extract audio, GIF), `Convert.Archives` (create/extract/convert, safety limits).
- Tesseract OCR engine option with hOCR output.
- Watch folders (per-user background + optional service), `modit watch` CLI, Settings page.

Exit criteria:
- Pack install/uninstall round trip offline and online; conversions succeed on fixtures; malicious archive
  fixtures rejected; LibreOffice parallel conversions with isolated profiles stable for 200 runs; watch folder
  processes 100 dropped files with zero duplicates or misses.

## M6 — Enterprise deployment

Deliverables:
- MSIX bundle + appinstaller on GitHub Pages + Store-ready manifest; sparse package for shell; WiX v5 MSI
  (features, properties, ADMX install), Burn bootstrapper with WebView2 chain; winget manifests with automated
  PRs; MSI notify-only updater path; internal mirror script for air-gapped sites; staged rollout (`rollout.json`);
  third-party notices generator. (Velopack channel, signing, SBOM and release notes already exist since M0.)
- `Modit.Policy` (registry reader, catalog, `modit policy show`), managed badges in Settings, all policy gates
  from 08 wired.
- `Modit.Licensing` (Ed25519 licenses, activation modes, trial, feature gates), License settings page,
  `modit license activate|status|deactivate`.
- Audit log complete (JSONL, rotation, UNC, event log).
- Shell integration complete (context menus Win10/Win11, associations, jump list, protocol).
- Admin docs set (deployment, policies, CLI, packs, security whitepaper, troubleshooting).

Exit criteria:
- Silent install/uninstall/upgrade matrix (MSI x64/arm64, MSIX, portable) passes on clean Win10 1809, Win10 22H2,
  Win11 VMs via automated lab script; every policy verified by test to lock UI and block CLI with exit code 3;
  license tampering detected; SBOM validates.

## M7 — Advanced PDF

Deliverables:
- Digital signatures (sign with cert store/PFX, visible appearance, RFC 3161 timestamp, verify, Signatures pane),
  incremental writer, byte-range verification.
- Redaction (marks, search patterns, apply with text/image/path handling, rasterize fallback, report,
  verification pass), sanitize complete.
- Compare (visual + text), Compare tab.
- PDF→Markdown/HTML semantic engine (reading order, headings, lists, tables basic), PDF→DOCX, PDF→XLSX/CSV tables.
- Object-level content edit (move/scale/delete/replace text string within same font, replace image).
- N-up/booklet, blank-page split detection, bookmarks auto-generation heuristic.

Exit criteria:
- Signatures verified as valid in Adobe Acrobat Reader and Microsoft Edge PDF viewer (manual matrix documented
  with screenshots); tampered-after-sign detected. Redaction verification: zero redacted strings remain in
  extracted text across pattern fixtures; raster fallback produces no residual text objects. Compare detects
  100% of synthetic injected changes.

## M8 — Hardening and 1.0

Deliverables:
- Full budget pass on Baseline/Low/ARM64/High; fix regressions; memory leak soak (8 h batch) passes.
- Fuzzing campaign (24 h per engine entry point) with all crashers fixed or confirmed host-isolated.
- Accessibility audit (manual Narrator pass of all screens + FastPass), high contrast polish, keyboard map doc.
- Localization: pseudo-loc pass, string freeze, en-US copy edit; de/fr/es/ja/zh-CN translation hand-off (P2
  shipping in 1.1).
- Documentation complete; release notes; Store listing assets; website download page artifacts.
- Beta channel with crash reporting enabled for opt-in testers; 2-week bake; 1.0 tag.

Exit criteria:
- Zero P0/P1 bugs open; all budgets green; install matrix green; security review checklist signed off; license
  scan clean; signed artifacts published; rollback plan documented.

---

## Post-1.0 backlog (1.x)

- Translations shipping; RTL UI; PDF/A conversion + validation (decision pending); LTV signatures and certify
  (DocMDP); form field designer; tear-off tabs; share contract; Parquet; subtitle burn-in; plugin host for
  signed converter assemblies; cloud-free team recipe sync via shared folder; MS Store release; Windows on ARM
  perf tuning for Snapdragon NPUs (OCR acceleration via Windows AI APIs when available).
