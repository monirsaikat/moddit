# 05 — Conversion Engine

Namespace root: `Modit.Convert` (abstractions + registry in Core), implementations in `Modit.Convert.Images`,
`.Vector`, `.Documents`, `.Data`, `.Media`, `.Archives`, `.Pdf`.

## Abstractions

```csharp
record FormatId(string Id);                          // "png", "pdf", "docx", "mp4" (lowercase, canonical)
record FormatDescriptor(FormatId Id, string DisplayName, string[] Extensions, string[] MimeTypes,
                        FormatFamily Family, Sniffer? Sniff, FormatTraits Traits); // Traits: Lossy, MultiPage, Animated, Vector, HasAlpha, Container

interface IConverter {
    ConverterInfo Info { get; }                      // Name, EngineId, Priority, RequiresEnginePack
    IReadOnlyList<(FormatId From, FormatId To, ConversionQuality Quality)> Edges { get; }
    bool IsAvailable(IEngineAvailability env);       // engine pack installed? WebView2 present? Office present?
    OptionsSchema OptionsFor(FormatId from, FormatId to); // JSON schema for UI/CLI generation
    Task<ConversionResult> ConvertAsync(ConversionInput input, ConversionOutput output, ConvertOptions options,
                                        IProgress<ConversionProgress> progress, CancellationToken ct);
}

sealed class ConverterRegistry {                    // builds the format graph
    ConversionPath? FindPath(FormatId from, FormatId to, PathConstraints c);  // Dijkstra, prefer direct & lossless
    IReadOnlyList<FormatId> TargetsFor(FormatId from);                        // for UI "Convert to…" menu
}
```

- `ConversionInput`: file path or stream + sniffed format + metadata (page count, dimensions) when cheap.
- `ConversionOutput`: either single file, multiple files (per page), or directory; output naming pattern applied
  by the caller (`OutputNamer`).
- `ConversionQuality`: `Lossless`, `HighFidelity`, `Lossy`, `Heuristic` — drives path selection and UI warnings
  ("This conversion is heuristic; layout may differ").
- Options schemas are JSON Schema (draft 2020-12) generated from C# option records with attributes
  (`[Range]`, `[Choices]`, `[DisplayName]`, `[Advanced]`); the UI renders option panels from the schema via
  `OptionsFormControl`, and the CLI maps `--option key=value`.

## Format sniffing

`Sniffer` checks magic bytes first (PNG, JPEG, GIF, WebP RIFF, AVIF/HEIC ftyp brands, TIFF II*/MM*, BMP, ICO, PDF
`%PDF-`, ZIP (then inspect for `[Content_Types].xml` → OOXML; `mimetype` → ODF/EPUB), 7z, RAR, GZ, XZ, BZ2, MP4
ftyp, Matroska EBML, OGG, FLAC, WAV RIFF, MP3 ID3/sync, SVG via XML prolog + `<svg`, JSON/YAML/CSV by content
heuristics with extension as tiebreaker). Extension mismatch → warn, trust content.

## Engine packs

| Pack | Contents | Size | Default |
|------|----------|------|---------|
| `ocr-tesseract` | tesseract.dll, leptonica, `eng` tessdata_best | 40 MB | optional |
| `ocr-lang-*` | per language tessdata_best | 5–30 MB each | optional |
| `office-libreoffice` | LibreOffice portable program dir | 350 MB | optional |
| `media-ffmpeg` | ffmpeg.exe, ffprobe.exe (LGPL build) | 60 MB | optional |
| `docs-pandoc` | pandoc.exe | 50 MB | optional |
| `archive-7zip` | 7za.exe | 2 MB | optional |
| `pdf-to-svg-poppler` | pdftocairo.exe + deps (GPL, separate process) | 30 MB | optional, disclosed |

Pack manifest (`manifest.json`): `id`, `version`, `rid`, `files[] {path, sha256, size}`, `license {name, url, textFile}`,
`entryPoints`, `minAppVersion`. Downloaded from GitHub Releases of the public `modit-packs` repo via the
`packs/index.json` feed on GitHub Pages (see 15), signed manifest (Ed25519), resumable download, SHA-256 verified. Install locations: per-user default, per-machine when run elevated or via MSI feature
(`ADDLOCAL=PackOffice,...`). Air-gapped: `modit packs install <path-to-pack.zip>`. Policy `EnginePacksAllowed`
whitelist, `EnginePackSource` override URL, `EnginePackDownloadsBlocked`.

UI: a "Converters" settings page lists packs with status, size, license, Install/Remove; a conversion that needs a
missing pack shows an inline "Install X (350 MB)" card instead of a dead end.

## Domain designs

### Raster images (`Modit.Convert.Images`)

- Engine: NetVips in a batch host. Streaming (`Image.NewFromFile(..., access: Access.Sequential)` where format
  allows). Per-op thread pool 2; parallelism across files handled by scheduler.
- Supported inputs: PNG, JPEG, WebP, AVIF, HEIC/HEIF (libheif or WIC), GIF (animated → frames), TIFF (multi-page),
  BMP, ICO (via our parser → PNG frames), TGA (via libvips magick loader if available, else own decoder), JXL, PSD
  (flattened composite via libvips magick loader; if unavailable, own PSD composite-image reader), SVG (resvg).
- Outputs: PNG (palette option, compression level, interlace), JPEG (quality, progressive, chroma subsampling,
  optimize Huffman, background for alpha), WebP (quality, lossless, near-lossless, effort, animated), AVIF
  (quality, speed, 10-bit), TIFF (compression: none/LZW/Deflate/JPEG, multi-page), GIF (palette, dither,
  animated), BMP, ICO (multi-size 16/24/32/48/64/128/256 from one source), JXL (if libjxl present).
- Operations (composable `ImageOp[]` applied in order): resize (fit/fill/stretch/percent/longest-edge/exact
  with kernel lanczos3/mitchell/nearest), crop (rect/aspect/center/smart-attention via `vips_smartcrop`),
  rotate (90s + arbitrary with background), flip, auto-orient from EXIF, strip metadata (keep/strip EXIF, ICC,
  XMP; keep copyright option), color profile convert to sRGB, background fill, pad to size, sharpen, grayscale,
  gamma, quality. Batch "rename pattern" `{name}_{width}x{height}.{ext}`.
- Animated → animated (GIF↔WebP↔AVIF) preserve frame delays; animated → static takes first frame or
  "export all frames".
- HEIC on systems without HEVC extension: detect via WIC decoder enumeration; show actionable message with
  Store link `ms-windows-store://pdp/?ProductId=9NMZLZ57R3T7`.

### Vector (`Modit.Convert.Vector`)

- SVG → PNG/JPEG/WebP/AVIF/ICO: resvg (`resvg_parse_tree_from_data`, `resvg_render` to RGBA buffer) at target
  width/height/scale/DPI, background, then libvips encode. Fonts: system + optional font dirs option.
- SVG → PDF: WebView2 print-to-PDF of an HTML wrapper sized to the SVG viewBox in points (exact page size,
  zero margins, background off). Verify vector output by checking the PDF has path operators (not just an image).
- SVG optimize (P1): own minimal optimizer (strip editor metadata, comments, defaults, collapse whitespace,
  numeric precision) — not SVGO-complete; label as "basic".
- PDF → SVG (P3): optional poppler pack `pdftocairo -svg`; fallback "raster embedded SVG" (PNG inside `<image>`).

### Documents (`Modit.Convert.Documents`)

- **LibreOffice** adapter: `soffice.exe --headless --norestore --nolockcheck -env:UserInstallation=file:///<tmp>
  --convert-to <filter> --outdir <dir> <input>`. Filter map table (e.g. `pdf:writer_pdf_Export`,
  `docx:"MS Word 2007 XML"`, `xlsx:"Calc MS Excel 2007 XML"`, `png:draw_png_Export`). Timeout per MB; kill tree
  on cancel (Job Object). Concurrency: N isolated profiles (default 2). Pre-warm one instance when a document
  conversion UI is opened.
- **Microsoft Office COM** adapter (when installed and policy allows): late-bound COM in a dedicated
  `Modit.OfficeHost.exe` (x86/x64 matching Office bitness), `Word.Application` / `Excel.Application` /
  `PowerPoint.Application` with `ExportAsFixedFormat`, `DisplayAlerts=false`, `AutomationSecurity=
  msoAutomationSecurityForceDisable` (macros off), watchdog kill at timeout. Only one Office instance at a time.
- **Pandoc** adapter: `pandoc -f X -t Y` for md/rst/html/docx/epub/odt/latex text conversions; PDF via our HTML
  path (not LaTeX).
- **Own converters** (no pack needed): TXT→PDF, MD→PDF, HTML→PDF (WebView2), CSV↔XLSX (ClosedXML),
  DOCX→TXT/MD (OpenXML read: paragraphs, headings, lists, tables, images extracted), XLSX→CSV (per sheet),
  PPTX→PDF requires LibreOffice/Office; PPTX→TXT (notes + text) own.
- **Email**: `.eml` (MimeKit, MIT) / `.msg` (MsgReader, MIT) → HTML (headers table + body + inline images) →
  PDF; attachments → PDF attachments or converted and appended (option).
- **EPUB → PDF**: unzip, read OPF spine, concatenate XHTML with CSS into one HTML, WebView2 print; or
  LibreOffice. Pandoc for EPUB ↔ DOCX/MD.

### HTML (`Modit.Convert.Documents.Html`)

- `WebView2Printer` service: single hidden `CoreWebView2` per process (CLI creates message-only HWND), queue of
  print jobs, `NavigateToString`/`Navigate(file://)`, wait `NavigationCompleted` + `DOMContentLoaded` + optional
  `window.moditReady` promise/timeout for JS-heavy pages, then `PrintToPdfAsync(path, settings)`. Settings: paper
  size (A4/Letter/custom), orientation, margins, scale, header/footer template, background graphics, media type
  `print`/`screen`. Network access for URLs honors policy (`HtmlToPdfAllowNetwork`); default **blocked** for
  local files (`--disable-web-security` never; use `CoreWebView2EnvironmentOptions` + request filter to allow
  only `file://` and `data:`).

### Data (`Modit.Convert.Data`)

- CSV (Sep): dialect detection (delimiter, quote, header, encoding, BOM), type inference optional; → XLSX
  (ClosedXML, with number/date typing, autofit optional, freeze header), → JSON (array of objects / array of
  arrays), → XML, → YAML, → Markdown table, → HTML table, → Parquet (P3, Parquet.Net).
- XLSX → CSV (per sheet or selected), → JSON, → PDF (via LibreOffice pack; or own simple table-to-HTML→PDF).
- JSON ↔ YAML ↔ TOML ↔ XML (with conventions documented; lossy cases flagged), pretty/minify/sort keys,
  validate against schema (P2).
- Text utilities: encoding convert (UTF-8/16/ANSI codepages), BOM add/remove, line endings, trailing whitespace.

### Media (`Modit.Convert.Media`)

- `ffprobe -print_format json -show_format -show_streams` for inspection.
- Presets (JSON in `presets/media/*.json`): "MP4 H.264 1080p (hardware)", "MP4 small for messaging",
  "WebM VP9", "GIF from video (fps, width, palette)", "Extract audio (MP3 192k / AAC / FLAC / WAV)",
  "Trim (start/end, stream copy when possible)", "Rotate", "Mute", "Normalize loudness (EBU R128)".
- Hardware encoders: `h264_mf`, `hevc_mf` (Media Foundation), `h264_amf`, `h264_nvenc`, `h264_qsv` detected via
  `ffmpeg -encoders` once and cached; fallback `libopenh264` (LGPL-compatible) then `libx264` **never** (GPL).
- Progress: `-progress pipe:1 -nostats`, parse `out_time_us` vs duration. Cancel: kill process tree.
- Thumbnails/poster frame extraction; subtitle burn-in (P3).

### Archives (`Modit.Convert.Archives`)

- Create ZIP/TAR/TAR.GZ/TAR.XZ (SharpCompress), 7z (7-Zip pack), with compression level, password (ZIP AES-256
  via SharpCompress; 7z via pack), split volumes (pack).
- Extract any supported → folder with: zip-slip protection (normalize, reject `..` and absolute, reject
  reparse-point traversal), size limit (default 10 GB, policy), ratio limit (1:200), file count limit, long path
  support (`\\?\`), ADS/invalid name sanitization, timestamps preserved.
- Convert archive A → archive B streaming entry-by-entry without full extraction when possible.
- "Archive to PDF": list contents as PDF (P2), "Images in ZIP → PDF" direct path (P1).

### PDF converters (`Modit.Convert.Pdf`)

Wrap the PDF engine (04): PDF→images (DPI, format, page range, color/gray/bilevel, antialias, transparent
background for PNG, render annotations/forms toggle), images→PDF (page size: image size / A4 fit / Letter,
margins, orientation auto, one image per page or grid N-up, DPI metadata honored, EXIF orientation), PDF→TXT/
MD/HTML, PDF→DOCX (P2), PDF→XLSX/CSV (P2), PDF→PDF/A (P3), "Any → PDF" generic by chaining (DOCX→PDF via pack).

## Output naming and collisions

Pattern tokens: `{name}` `{ext}` `{page}` `{page:000}` `{index}` `{count}` `{date:yyyyMMdd}` `{time:HHmmss}`
`{width}` `{height}` `{parent}` `{format}` `{random:6}` `{counter}`. Collision policies: `Rename (name (1))`,
`Overwrite`, `Skip`, `Ask`. Output location: same folder / fixed folder / mirror source tree under root /
ask. Illegal character sanitization; max path handling with long-path awareness (manifest `longPathAware`).

## Progress and estimation

Each converter reports `ConversionProgress {Fraction?, Stage, ItemsDone, ItemsTotal, BytesDone?}`. Scheduler
estimates ETA with per-(converter, format) EMA of throughput stored in SQLite for next time. UI shows per-item and
aggregate progress; CLI shows a TTY progress bar or JSON lines (`--json`).

## CLI mapping

```
modit convert <inputs...> --to png [--dpi 150] [--pages 1-5] [--out <dir>] [--name "{name}_{page:000}"] [--overwrite|--skip|--rename]
modit pdf merge a.pdf b.pdf:1-3 --out merged.pdf [--bookmarks nested]
modit pdf split in.pdf --every 1 --out ./pages
modit pdf compress in.pdf --preset email --out small.pdf
modit pdf protect in.pdf --user-password X --owner-password Y --no-print --no-copy
modit pdf ocr scan.pdf --lang eng+deu --engine tesseract --out ocr.pdf
modit pdf watermark in.pdf --text "CONFIDENTIAL" --opacity 0.2 --angle 45
modit pdf bates in.pdf --prefix ABC --start 1 --digits 6
modit pdf sign in.pdf --cert-thumbprint ... --reason "Approved" --tsa https://...
modit pdf redact in.pdf --pattern ssn --pattern "regex:..." --out redacted.pdf --report report.json
modit recipe run my-recipe.json <inputs...>
modit watch add <folder> --recipe r.json --out <dir> --error-dir <dir>
modit packs list|install|remove <id>
modit update check|apply [--channel beta]     # GitHub Releases-backed updater (see 15)
modit --json ...   # machine-readable events on stdout
# exit codes: 0 ok, 1 partial (some items failed), 2 invalid args, 3 blocked by policy, 4 engine pack missing,
#             5 operation error, 6 unlicensed feature, 7 cancelled, 8 update/installer error
```

`System.CommandLine` for parsing; every `Operation` record maps 1:1 to a subcommand with options generated from
the same options schema used by the GUI, so parity is structural, not manual.
