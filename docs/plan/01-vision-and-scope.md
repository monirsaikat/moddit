# 01 — Vision and Scope

## Vision

A Windows-native desktop application that feels like a first-party Microsoft tool, opens a 500-page PDF in under a
second, and handles the long tail of "I need this file in that format" without sending anything to the cloud.
One product, two faces: a polished GUI for individuals and a scriptable CLI/batch engine for enterprises.

## Positioning

| Dimension | Modit stance |
|-----------|--------------|
| Deployment | Local only. No account required. No cloud processing. Optional update check only. |
| Performance | Native engines, GPU-composited canvas, out-of-process workers, aggressive caching. |
| UI | Fluent 2 / WinUI 3. Mica, dark/light/high-contrast, keyboard-first, UIA-accessible. |
| Enterprise | MSIX + MSI, ADMX policies, offline activation, audit log, silent install, CLI, watch folders. |
| Trust | Code-signed, SBOM shipped, no AGPL, sandboxed engine processes, JavaScript in PDFs disabled. |
| Price model | Decided by owner (see 13). Architecture assumes perpetual license key + optional volume licensing. |

## Target users

1. **Individual power user** — merges scans, compresses PDFs for email, converts WebP to PNG, trims a video.
2. **Office/legal/finance teams** — Bates numbering, redaction, watermarking, form flattening, signing, compare.
3. **IT administrators** — silent deploy via Intune/SCCM, lock down features with policy, no telemetry, offline.
4. **Automation operators** — CLI and hot folders: "every PDF dropped here gets OCR'd, compressed, and moved."

## Non-goals (explicit)

- No cloud sync, collaboration, or web app.
- No cross-platform. No Electron. No web UI shell.
- No in-place rich text reflow editing of PDF body text in v1 (object-level text edit only; see 04).
- No PDF creation from a WYSIWYG word processor. Modit edits/transforms documents; it is not Word.
- No DRM circumvention. Password removal requires the password.
- No video editor timeline. Media support is conversion/trim/compress presets only.
- No plugin marketplace in v1 (architecture leaves room; see 02).

## Feature inventory

Priority: **P0** = MVP (must ship in 1.0), **P1** = 1.0 target, **P2** = 1.x, **P3** = stretch / needs decision.

### PDF — Viewing

| Feature | Pri | Notes |
|---------|-----|-------|
| Open, tabbed documents, recent files, drag-drop | P0 | Multiple windows allowed |
| Continuous/single/facing page modes, zoom (fit width/page/custom), rotate view | P0 | |
| Thumbnail sidebar with virtualization | P0 | Drag to reorder (edit mode) |
| Outline/bookmarks panel, go-to page, page labels | P0 | |
| Text search with highlights, next/prev, whole word/case/regex | P0 | Regex P1 |
| Text selection, copy, copy as Markdown table (P3) | P0 | |
| Attachments panel, layers (OCG) toggle, properties/metadata | P1 | |
| Reading mode (distraction-free), full screen, presentation mode | P1 | |
| Night/sepia rendering filter, invert colors | P2 | |
| Print with page ranges, scaling, duplex, N-up | P0 | Windows print dialog |

### PDF — Pages and structure

| Feature | Pri | Notes |
|---------|-----|-------|
| Merge multiple PDFs with drag ordering, page-range selection per source, preserve bookmarks | P0 | QPDF |
| Split: by ranges, every N pages, by bookmark level, by file size, by blank-page detection, odd/even | P0 | Blank detection P1 |
| Reorder, rotate, delete, duplicate, insert blank, insert from file, move across documents | P0 | |
| Extract pages to new file(s) | P0 | |
| Crop pages (visual crop box editor), resize/scale to paper size, margins | P1 | |
| N-up, booklet imposition | P2 | |
| Edit bookmarks (add/rename/nest/delete), auto-generate from headings heuristics | P1 | |
| Metadata editor (Info dict + XMP), remove metadata | P0 | |
| Attachments add/remove/extract | P1 | |
| Repair damaged PDF (QPDF reconstruct) | P0 | Auto-offer on open failure |
| Linearize (fast web view) | P1 | |

### PDF — Content and annotations

| Feature | Pri | Notes |
|---------|-----|-------|
| Highlight, underline, strikethrough, squiggly on text | P0 | |
| Sticky note, free text, callout | P0 | |
| Shapes: rectangle, ellipse, line, arrow, polygon, polyline | P1 | |
| Ink/freehand with pen pressure (Windows.UI.Input.Inking) | P1 | |
| Stamps (built-in library + custom image stamps) | P1 | |
| Comments panel listing all annotations, reply threads, status | P1 | |
| Flatten annotations | P0 | |
| Insert image, insert text block (new text object with font embedding) | P1 | |
| Edit/move/delete existing page objects (text runs, images, paths) | P2 | Object-level, no reflow |
| Replace image, extract image | P1 | |
| Watermark (text/image, tiling, opacity, rotation, page ranges) | P0 | |
| Header/footer, page numbers, Bates numbering | P0 | Bates = enterprise differentiator |
| Links: add/edit/remove URI and go-to links | P2 | |

### PDF — Forms

| Feature | Pri | Notes |
|---------|-----|-------|
| Fill AcroForms (text, checkbox, radio, combo, list, date) | P0 | PDFium formfill |
| Flatten forms | P0 | |
| Import/export form data FDF, XFDF, JSON, CSV (batch fill) | P1 | |
| Create/edit form fields (field designer) | P2 | |
| XFA | — | Not supported (PDFium built without XFA) |

### PDF — Security, compliance, output

| Feature | Pri | Notes |
|---------|-----|-------|
| Protect: user/owner password, AES-256, permission flags | P0 | QPDF |
| Unlock (with password), remove permissions restrictions when owner password known | P0 | |
| Compress/optimize: image downsample + recompress (JPEG/JPX/WebP→JPEG), remove unused objects, dedupe, strip metadata/thumbnails, object streams | P0 | Presets: Email / Balanced / Print / Custom |
| OCR to searchable PDF (invisible text layer), languages, auto-rotate, deskew | P0 | Windows OCR default, Tesseract engine pack |
| True redaction (remove underlying content, raster fallback), search-and-redact patterns (SSN, email, regex), redaction marks review | P1 | |
| Digital signatures: sign with Windows cert store / PFX, visible signature appearance, timestamp (RFC 3161), verify, PAdES-B/T | P1 | Own implementation |
| Compare two PDFs: visual diff overlay, text diff report | P2 | |
| PDF/A-2b conversion (best effort) + validation report | P3 | Needs decision on commercial validator |
| Sanitize: remove JavaScript, embedded files, links, metadata, hidden layers | P1 | |
| Accessibility tags inspection (read-only) | P3 | |

### PDF — Conversions (in and out)

| From → To | Pri | Engine |
|-----------|-----|--------|
| PDF → PNG/JPEG/WebP/TIFF/BMP (per page, DPI, color) | P0 | PDFium + libvips |
| PDF → multi-page TIFF | P1 | libvips |
| PDF → TXT, Markdown (heuristic headings/lists), HTML | P0/P1 | PDFium text + layout heuristics |
| PDF → DOCX (layout-preserving heuristic) | P2 | Own engine on OpenXML; fidelity caveat |
| PDF → XLSX/CSV (table extraction) | P2 | Own ruling/whitespace table detector |
| PDF → SVG | P3 | Optional engine pack (pdftocairo) or raster-embedded fallback |
| Images (PNG/JPEG/WebP/HEIC/TIFF/BMP/GIF/AVIF) → PDF | P0 | libvips + PDFium image objects |
| Scans → PDF with OCR | P0 | Composition of above |
| DOCX/XLSX/PPTX/ODT/RTF → PDF | P1 | LibreOffice engine pack or MS Office COM |
| HTML/MHTML/URL → PDF | P1 | WebView2 |
| Markdown → PDF | P1 | Markdig → HTML → WebView2 |
| TXT → PDF (monospace, wrap, page numbers) | P0 | HTML path |
| EML/MSG → PDF (with attachments as PDF attachments) | P2 | MsgReader → HTML → WebView2 |
| SVG → PDF (vector preserved) | P1 | WebView2 |
| EPUB → PDF | P2 | Pandoc/LibreOffice engine pack |

### Converters — non-PDF

| Domain | Formats | Pri | Engine |
|--------|---------|-----|--------|
| Raster | PNG, JPEG, WebP, AVIF, HEIC/HEIF, GIF (incl. animated), TIFF, BMP, ICO, TGA, JXL, PSD (read) | P0 (core 7) / P1 (rest) | libvips; WIC fallback for HEIC |
| Raster ops | Resize (fit/fill/exact/percent), crop, rotate/flip, quality, strip EXIF, color profile, background fill, format-specific options (lossless WebP, PNG palette, progressive JPEG) | P0 | libvips |
| Vector | SVG → PNG/JPEG/WebP/ICO/PDF; SVG optimize | P0 (raster) / P1 (PDF, optimize) | resvg; WebView2 |
| Icons | PNG/SVG → ICO multi-size; ICO → PNGs | P1 | libvips |
| Documents | DOCX ↔ PDF/ODT/RTF/TXT/HTML/MD; XLSX ↔ CSV/ODS/PDF; PPTX → PDF/PNGs | P1 | LibreOffice pack, Pandoc pack, own for CSV/XLSX |
| Data | CSV ↔ XLSX ↔ JSON ↔ XML ↔ YAML ↔ TOML; JSON pretty/minify; encoding and line-ending conversion | P1 | Managed libraries |
| Media | MP4/MKV/MOV/AVI/WebM/GIF; MP3/AAC/FLAC/WAV/OGG/OPUS; trim, extract audio, compress presets, resolution change, GIF from video | P1 | FFmpeg pack |
| Archives | ZIP/7z/TAR/GZ/BZ2/XZ create and extract; RAR extract; convert between | P1 | SharpCompress; 7-Zip pack for 7z write/RAR |
| Ebooks | EPUB ↔ MOBI/AZW3 | P3 | Calibre detection only (not bundled) |
| Fonts | TTF/OTF ↔ WOFF/WOFF2 | P3 | |

### Batch, automation, enterprise

| Feature | Pri |
|---------|-----|
| Batch queue: add N files, apply operation chain, output naming patterns, parallelism, pause/resume, persisted across restarts | P0 |
| Saved workflows ("recipes"): chain of operations with parameters, import/export JSON | P1 |
| CLI `modit.exe` with every operation, JSON output, exit codes, stdin/stdout piping | P0 (core ops) / P1 (all) |
| Watch folders (hot folders) running a recipe, with error folder and log | P1 |
| Windows Explorer context menu (Win11 IExplorerCommand + classic), file associations, "Open with", jump list | P0 |
| Policy control (ADMX/registry) for every network/engine/feature toggle | P1 |
| Audit log (JSONL) of operations, exportable | P1 |
| License: perpetual key, offline activation, volume key, trial mode | P1 |
| Localization: en-US P0; de, fr, es, pt-BR, ja, zh-CN P2 | |

## Success metrics for 1.0

- Cold start to interactive < 1.5 s on a 2020 mid-range laptop (see 07 for all budgets).
- Zero native crashes propagate to UI (engine hosts restart transparently).
- 100% of P0 features covered by automated tests; rendering golden tests pass on x64 and ARM64.
- Installs silently via `msiexec /qn` and via Intune MSIX with no user interaction.
- Passes Accessibility Insights for Windows FastPass with zero errors.
