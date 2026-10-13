# Modit — Master Plan Index

**Modit** (working name, from the repository folder) is a Windows-only, production-grade desktop application for
viewing, merging, splitting, editing and modifying PDFs, plus a broad file-conversion engine (images, vector,
documents, media, archives, data formats). Target audience: knowledge workers and enterprises that need a fast,
offline-capable, policy-controllable tool with a modern Fluent UI.

This folder is the complete plan. It is written to be executed by an implementing engineer or model
(Claude Opus) with minimal re-deliberation. Decisions marked **LOCKED** are not to be reopened without a new ADR.

## Reading order

| # | File | What it answers |
|---|------|-----------------|
| 01 | [01-vision-and-scope.md](01-vision-and-scope.md) | What we build, for whom, what we explicitly do not build, feature inventory with priorities |
| 02 | [02-architecture.md](02-architecture.md) | Process model, module boundaries, job engine, data flow, crash isolation |
| 03 | [03-technology-decisions.md](03-technology-decisions.md) | Stack ADRs: .NET 10 + WinUI 3, PDFium + QPDF, libvips, resvg, WebView2, FFmpeg, LibreOffice, licensing posture |
| 04 | [04-pdf-engine.md](04-pdf-engine.md) | PDF subsystem design: rendering, page ops, annotations, forms, security, OCR, signatures, redaction, compare |
| 05 | [05-conversion-engine.md](05-conversion-engine.md) | Converter registry, format matrix, engine packs, per-domain converter designs |
| 06 | [06-ui-ux-and-design-system.md](06-ui-ux-and-design-system.md) | Shell layout, screens, design tokens, interaction patterns, accessibility, localization |
| 07 | [07-performance-engineering.md](07-performance-engineering.md) | Budgets, tile renderer, memory management, startup, benchmarks |
| 08 | [08-enterprise-security-deployment.md](08-enterprise-security-deployment.md) | Packaging (MSIX/MSI), policies, licensing, audit, security hardening, updates, SBOM |
| 09 | [09-repository-structure-and-conventions.md](09-repository-structure-and-conventions.md) | Solution layout, naming, coding standards, DI, logging, error model |
| 10 | [10-roadmap-and-milestones.md](10-roadmap-and-milestones.md) | Phased delivery M0–M8 with exit criteria |
| 11 | [11-testing-and-quality.md](11-testing-and-quality.md) | Test pyramid, golden rendering tests, fuzzing, UI automation, CI gates |
| 12 | [12-build-playbook-for-opus.md](12-build-playbook-for-opus.md) | Step-by-step execution playbook, task granularity, definitions of done, prompts |
| 13 | [13-open-questions-and-risks.md](13-open-questions-and-risks.md) | Decisions only the product owner can make, and the risk register |
| 14 | [14-installation-and-first-run.md](14-installation-and-first-run.md) | Installer UX for every channel, prerequisites, first-run onboarding, repair, uninstall, migration, portable mode |
| 15 | [15-release-and-auto-update-github.md](15-release-and-auto-update-github.md) | GitHub repos, Actions workflows, signing via OIDC, Velopack + GitHub Releases auto-update, channels, deltas, rollback, staged rollout, winget, Pages, engine pack feed, air-gapped mirror |

## Locked decisions (summary)

- **Platform**: Windows 10 1809+ and Windows 11, x64 and ARM64. No macOS/Linux, ever, in this codebase.
- **Language/runtime**: C# on .NET 10 LTS. Native engines via P/Invoke (`LibraryImport`) or child processes.
- **UI**: WinUI 3 (Windows App SDK, latest stable), Fluent 2, Mica, MVVM via CommunityToolkit.Mvvm, DI via Microsoft.Extensions.
- **Document canvas**: Win2D `CanvasVirtualControl` with a tile-based renderer fed by out-of-process PDFium renders over shared memory.
- **PDF engines**: PDFium (render, text, page objects, annotations, forms) + QPDF (structure, merge/split, encryption, linearization, repair, object JSON). No AGPL libraries (no iText, MuPDF, Ghostscript) in the shipped product.
- **Raster images**: libvips via NetVips. **Vector**: resvg. **HTML/MD/SVG→PDF**: WebView2 print-to-PDF. **Media**: FFmpeg (LGPL build, engine pack). **Office**: LibreOffice headless (engine pack) with MS Office COM as optional high-fidelity path.
- **Process model**: UI process + pool of sandboxed engine host processes. Native code never runs in the UI process.
- **Distribution**: GitHub is the backbone. Velopack per-user installer with auto-update from GitHub Releases (delta
  updates, stable/beta channels) is the consumer channel; MSI via WiX v5 for per-machine enterprise deployment; MSIX +
  appinstaller on GitHub Pages for Intune/Store. All signed with Azure Trusted Signing from GitHub Actions via OIDC.
  Release pipeline exists from M0 so every milestone ships as an installable, self-updating build.
- **CLI**: first-class `modit.exe` headless CLI sharing the same core. Enterprise automation is not an afterthought.
- **Licensing posture**: product is commercial/closed; dependencies restricted to MIT/BSD/Apache/MPL/LGPL(dynamic)/GPL(separate process only, optional engine pack with disclosure).

## How to use this plan with Opus

1. Read `README.md`, `01`, `02`, `03`, `09`, `12`, and `15` fully before writing code.
2. Follow milestones in `10` in order. Do not start M(n+1) features before M(n) exit criteria pass.
3. Each task in `12` has a Definition of Done. Build, test, and run the app before declaring a task done.
4. When a plan statement conflicts with reality (API removed, library broken), write a short ADR in `docs/adr/` and continue. Do not silently diverge.
5. Items in `13` require the product owner's answer. Use the stated default assumption until answered.
