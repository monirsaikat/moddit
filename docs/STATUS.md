# Build status

Updated: 2026-10-13. One line per playbook task (docs/plan/12). "Verified" means it was run, not just compiled.

## Milestone 0 — Foundations

| Task | State | Evidence |
|------|-------|----------|
| M0.1 Repo skeleton | Done | `dotnet build Modit.sln`: 0 warnings, 0 errors (x64). ARM64 build not run locally |
| M0.2 Native fetch | Done | `tools/fetch-native.ps1 -Rid win-x64,win-arm64` downloads PDFium chromium/8100 and QPDF 12.4.2, SHA-256 pinned |
| M0.3 PDFium bindings | Done | Exercised through the engine host tests (open, sizes, metadata, text, render, tiles) |
| M0.4 QPDF bindings | Done | Merge, AES-256 encrypt, error capture tested |
| M0.5 resvg bindings | **Not started** | Needs a source build; moved to M3.3 (ADR 0002 #9) |
| M0.6 IPC layer | Done, reduced | Named pipe + StreamJsonRpc + shared memory + Job Object + crash recovery tested. No host pool, restricted token or AOT yet (ADR 0002 #1, #4, #5) |
| M0.7 Core skeleton | Done | Page ranges, job scheduler, settings, policy reader, safe-save merge: 39 tests |
| M0.8 App shell | Done, reduced | Launches, tabs in title bar, Mica, themes, opens and renders PDFs, password prompt, drag-drop, single instance. Accessibility FastPass and startup timing not run |
| M0.9 CLI | Done | `modit pdf info|merge|render|text`, `--json`, exit codes; run by hand on fixtures. No automated CLI process tests yet |
| M0.10 Release pipeline + updater | Done locally | `tools/build.ps1` + `tools/release/verify-update-path.ps1`: silent install, self-update 0.1.0 → 0.1.1 by delta, relaunch, clean uninstall. **GitHub workflows are written but have never run**: no repository, token or signing account exists yet |
| M0.11 First run and install polish | Partial | Welcome card, "Open with" registration and residue-free uninstall are done and verified. Repair, reset settings, prerequisite checks and What's New are not |

Tests: 116 passing (`dotnet test --solution Modit.sln`).

## Version 0.2.0: PDF tools pulled forward from milestone 2

Added ahead of the roadmap because the 0.1 app could only open and read a PDF.

| Piece | State | Evidence |
|---|---|---|
| `PdfService` operations | Done | Merge, split, extract, rotate, protect, unlock, optimize, PDF to PNG. Safe-save contract on every one. Covered in `PdfToolsTests` |
| CLI | Done | `modit pdf split|extract|rotate|protect|unlock|optimize|images`; run by hand on fixtures |
| Home screen | Done | Tile for each tool under the Open card |
| Tool tabs | Done | One `ToolPage` for all eight tools. Merge, split, rotate and PDF to images were run through the UI by automation; protect, unlock, extract and optimize were only run through tests and the CLI |
| Viewer zoom | Done | 25 % to 400 %, fit width, Ctrl+Plus, Ctrl+Minus, Ctrl+0, Ctrl+wheel. Renders are capped at 16 megapixels per page |
| Launch argument | Done | `Modit.App.exe --tool merge a.pdf b.pdf` opens a tool with its input files |

Not done in the tools UI: drag to reorder, page thumbnails, validation messages are English only and come
from `PdfService`, and no automated UI tests.

## Version 0.3.0: image conversion pulled forward from milestone 3

| Piece | State | Evidence |
|---|---|---|
| Image engine in the host | Done | libvips 8.18.7 (NetVips 3.2.0). Reads PNG, JPEG, WebP, AVIF, HEIC, TIFF, GIF, BMP, ICO, SVG. Writes PNG, JPEG, WebP, AVIF, TIFF, GIF, BMP, ICO. EXIF rotation, CMYK to sRGB, animated GIF/WebP kept, size limits, metadata strip |
| `ImageConvertService` | Done | Batch, per-file failures, rename/skip/overwrite, verify-then-move. `ImageConvertTests` |
| Images to PDF | Done | `PdfService.FromImagesAsync`; JPEG embedded unchanged; page size image/A4/Letter, margins |
| Format sniffing | Done | `FormatSniffer` by content, tested |
| CLI | Done | `modit convert ... --to webp`, `modit pdf from-images`; run by hand on PNG, JPEG (EXIF-rotated, CMYK), animated GIF, SVG |
| App | Done | Home has a Convert section: Convert images, Images to PDF, PDF to images. Dropping images on Home opens the converter. Both tools were run through the UI by automation |
| Notices | Done | `THIRD-PARTY-NOTICES.md` and the libvips notice list are copied into every build |

Deviations: `docs/adr/0003-image-conversion-deviations.md` (SVG via librsvg instead of resvg needs owner approval).
Not done: converter registry and options schema (M3.1), SVG to vector PDF, JPEG XL/PSD/TGA, queue tab, Explorer
context menu.

## Version 0.4.0: page editing pulled forward from milestone 4

| Piece | State | Evidence |
|---|---|---|
| Organize pages editor | Done | Thumbnail grid: drag to reorder, rotate, duplicate, delete, blank pages, pages from other PDFs, undo, Save as. `OrganizeViewModelTests`; buttons driven by UI automation |
| Watermark, page numbers and headers, image or signature stamp | Done | PDFium editing in the host, placed on the page as seen (rotation aware). `PdfEditTests` check text and rendered pixel positions |
| Crop, flatten, remove pages, PDF to text | Done | `PdfEditTests` |
| CLI | Done | `modit pdf organize|remove-pages|watermark|page-numbers|add-image|crop|flatten|to-text`, chained by hand on fixtures and rendered |
| App | Done | Home has an "Edit pages" section; the PDF viewer has a Tools menu that opens any tool with the open file |

Details and limits: `docs/adr/0004-page-editing-tools.md`.

## What the owner must provide before a real release

1. A GitHub organization and two repositories: private source, public releases (plan 13 Q16). Then set
   `vars.RELEASES_REPO` and `secrets.RELEASES_REPO_TOKEN`, and change `UpdateDefaults.ReleasesRepositoryUrl`.
2. An Azure Trusted Signing account, or another code-signing certificate. Until then installers are unsigned
   and Windows SmartScreen will warn users.
3. Answers to plan 13 Q1 (name and publisher) and Q2 (licensing model).

## Next

Milestone 1, task M1.1: the Win2D `DocumentCanvas` with tile rendering, zoom and layouts.
