# 13 — Open Questions and Risk Register

## Decisions required from the product owner

Each has a **default assumption** the implementing model uses until answered. Answer by editing this file.

| # | Question | Default assumption | Impact if changed |
|---|----------|--------------------|-------------------|
| Q1 | Product name and publisher identity (company name, domain, support email)? | "Modit", publisher `CN=Modit`, domain placeholder `modit.example` | Package identity, signing, update URLs, ADMX namespace. Must be settled before M6.1. |
| Q2 | Commercial model: perpetual license + maintenance? Subscription? Free tier? | Perpetual key with 1 year of updates; 14-day trial; Free edition with view/merge/split/convert-images | Licensing module features (M6.4), feature gating map, Store listing. |
| Q3 | Minimum Windows version: Win10 1809, Win10 21H2, or Win11 only? | Win10 1809 (17763) | Win11-only would allow dropping classic context menu, Acrylic fallbacks, some WinRT API guards. |
| Q4 | Microsoft Store distribution in addition to direct MSIX/MSI? | Yes, Store-ready manifest but publish after 1.0 | Store policies (no auto-updater inside Store build, in-app purchase rules). |
| Q5 | Budget for a commercial PDF SDK "fidelity pack" (e.g. for PDF/A validation, PDF→DOCX fidelity)? | No; ship own heuristics, label them | PDF/A (P3) and DOCX export quality; roughly $5k–$30k/year depending on vendor. |
| Q6 | Telemetry: none at all, or opt-in anonymous usage + crash reports? | Opt-in, off by default, policy can force off | Removes `Modit.Update`/crash endpoints if "none". |
| Q7 | Update infrastructure confirmed as GitHub (public `modit-releases` repo + GitHub Pages)? Source repo private or public? | GitHub Releases + Pages for installers, updates, engine packs and the download page; source repo **private**, releases repo **public** | If source goes public, collapse to one repo. If GitHub is unacceptable, swap Velopack source to `SimpleWebSource` on any static host; the pipeline design in 15 stays. |
| Q16 | GitHub organization/user name and release repo name (`<owner>/modit-releases`)? | `modit-app/modit-releases` placeholder | Hard-coded update URL, Pages URL, winget manifests. Needed by M0.10. |
| Q17 | Crash-report endpoint: self-hosted Sentry, Azure Function, or none (GitHub cannot receive anonymous uploads)? | None in 1.0; "Report a problem" opens a prefilled GitHub Issue with diagnostics zip attached manually by the user | Removes automated crash telemetry. |
| Q8 | Crypto strictness: is FIPS 140-validated-only acceptable for licensing (Ed25519 is not FIPS)? | Ed25519 for license signatures (not security-critical), CNG for PDF signatures | If strict, switch license signatures to RSA-PSS/ECDSA P-256 via CNG. |
| Q9 | Should Office COM automation be offered at all (fidelity vs. "touches user's Office")? | Yes, off by default, user opt-in, policy gate | Removes `Modit.OfficeHost` if no. |
| Q10 | Ship GPL-licensed optional packs (Pandoc, poppler for PDF→SVG) as separate downloads with disclosure? | Pandoc yes (clear separate process); poppler pack deferred | PDF→SVG path, MD/EPUB/DOCX text conversions quality. |
| Q11 | Languages for 1.0 UI beyond en-US? | en-US only in 1.0; de/fr/es/pt-BR/ja/zh-CN in 1.1 | Translation vendor timing, string freeze date. |
| Q12 | Watch-folder service as a true Windows Service (runs without login) in 1.0? | Per-user background task in 1.0; service in 1.1 | Installer feature, service account, security review. |
| Q13 | Default PDF handler takeover prompt on first run? | Never prompt; offer in Settings; policy can set | User experience and enterprise expectations. |
| Q14 | Accent/brand color and app icon design? | System accent; placeholder icon (document with arrows) | Assets for Store and installer; design time. |
| Q15 | Hardware for self-hosted perf CI runner available? | Assume one Baseline-class machine | Without it, perf gates are informational only. |

## Risk register

| # | Risk | Likelihood | Impact | Mitigation | Trigger / fallback |
|---|------|------------|--------|------------|--------------------|
| R1 | WinUI 3 defects (TabView in title bar, Mica, DPI, memory leaks in ItemsRepeater) slow UI work | Medium | High | Pin WinAppSDK version; keep ViewModels framework-agnostic; maintain a known-issues list; use CommunityToolkit controls where WinUI is weak | If a blocker persists > 2 weeks: evaluate WPF + Fluent theme fallback (ADR) |
| R2 | Native AOT for `Engine.Host` conflicts with WinRT (Windows.Media.Ocr) or MessagePack | Medium | Medium | CsWinRT supports AOT since 2.1; test early in M0.6; keep OCR in a non-AOT host if needed | Fallback: R2R host instead of AOT (startup +100 ms) |
| R3 | PDFium API gaps (clip paths, per-glyph edits, outline writing, font subsetting) | High | Medium | Responsibilities split with QPDF; own content-stream tokenizer for redaction; our own subsetter in P2 | Budget extra days in M7.3/M7.6; raster fallbacks |
| R4 | Restricted-token/low-integrity hosts fail to open files or shared memory in some corporate configs | Medium | Medium | UI process opens files and passes handles/sections; feature flag `HostSandboxLevel` (Full/Reduced/None) with diagnostics | Auto-downgrade sandbox level with warning in logs |
| R5 | LibreOffice headless instability / long startup, profile locks | High | Medium | Isolated profiles, watchdog, pre-warm, retry once, clear error UX; Office COM alternative | Document fidelity expectations; recommend Office COM for enterprises |
| R6 | WebView2 runtime missing on locked-down Win10 images | Medium | Medium | Burn chain installs Evergreen; fixed-version runtime option via policy; features degrade with clear message | HTML→PDF unavailable until installed |
| R7 | HEIC decoding patents/decoder availability | Medium | Low | Use Windows HEVC extension when present; never bundle HEVC decoder | Clear Store link message |
| R8 | H.264 encoding quality via Media Foundation/OpenH264 lower than x264 | High | Low | Document; prefer hardware encoders; offer VP9/AV1 | Optional user-supplied FFmpeg build path (policy) |
| R9 | Performance budgets missed on Low profile | Medium | Medium | Tile budgets scale with RAM; reduce pool sizes; disable Mica | Document minimum spec |
| R10 | Golden rendering test flakiness across PDFium versions | High | Low | SSIM tolerance, controlled native refresh cadence with golden update PRs | Quarantine list with expiry |
| R11 | Digital signature interoperability (Acrobat shows yellow/red) | Medium | High | Follow PAdES/ISO 32000-2 precisely; test matrix with Acrobat, Edge, Chrome, Foxit; ESS attributes; proper `/ByteRange` | Ship signing as "beta" in 1.0 if any reader shows invalid |
| R12 | Redaction completeness (hidden text in forms, OCG, annotations, metadata, incremental updates) | Medium | High | Full rewrite (no incremental save) after redaction; sanitize pass; verification extraction; raster fallback | Default to rasterize-page mode for marked pages if any uncertainty |
| R13 | Licensing of fonts embedded when adding text (restricted embedding bits) | Medium | Low | Check OS/2 fsType; refuse restricted fonts with message; default to Segoe/Arial substitutes allowed | |
| R14 | Scope creep from the breadth of converters | High | Medium | P-levels; `IEngineAvailability` cards; CLI parity rule stops hidden half-features | Cut P2+ from 1.0 ruthlessly |
| R15 | ARM64 native binaries missing for a dependency (e.g. resvg, QPDF releases) | Medium | Medium | Build from source in `native-refresh.yml` with vcpkg/cargo for arm64 | Ship x64 only on ARM via emulation for that engine |
| R16 | MSIX sparse package for context menu fails on unpackaged (MSI) installs | Medium | Medium | Register sparse package from MSI custom action with external location; classic menu fallback | Win10-style cascaded menu only |
| R17 | Model-built code drifts from plan without record | Medium | Medium | ADR rule + CLAUDE.md pointers + DoD item 7 | Periodic owner review of `docs/adr` |

## Assumptions log

- Windows App SDK stable line supports Win10 1809+ and Native AOT for non-XAML processes.
- QPDF 11+ provides `qpdfjob-c` and JSON v2 with `--update-from-json`.
- bblanchon PDFium binaries continue monthly releases for win-x64 and win-arm64 without V8/XFA.
- NetVips native packages for win-arm64 exist (verify at M0.2; else build libvips arm64 or use x64 under emulation for images engine with ADR).
- resvg publishes a C API DLL for Windows; if arm64 is missing, build via `cargo build --target aarch64-pc-windows-msvc`.
