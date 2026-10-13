# 04 — PDF Engine Design

Namespace root: `Modit.Pdf` (managed façade in Core), `Modit.Engine.Host.Pdf` (adapters), `Modit.Native.Pdfium`,
`Modit.Native.Qpdf`.

## Responsibility split

| Concern | Engine | API surface |
|---------|--------|-------------|
| Render page/tile to bitmap | PDFium | `FPDF_RenderPageBitmapWithMatrix`, `FPDF_FFLDraw` (forms) |
| Text extraction, char boxes, search | PDFium | `FPDFText_LoadPage`, `FPDFText_GetCharBox`, `FPDFText_FindStart` |
| Links, bookmarks (read), page labels, metadata (read) | PDFium | `FPDFLink_*`, `FPDFBookmark_*`, `FPDF_GetPageLabel`, `FPDF_GetMetaText` |
| Annotations CRUD, appearance streams | PDFium | `FPDFAnnot_*`, `FPDFPage_CreateAnnot`, `FPDFAnnot_SetAP` |
| Form fill, field enumeration, flatten | PDFium | `FPDFDOC_InitFormFillEnvironment`, `FORM_*`, `FPDFPage_Flatten` |
| Page object insert/edit/remove (text, image, path) | PDFium | `FPDFPageObj_*`, `FPDFPage_InsertObject`, `FPDFPage_GenerateContent` |
| Image extraction (raw/decoded) | PDFium | `FPDFImageObj_GetImageDataDecoded`, `GetBitmap`, `GetImageFilter` |
| Save edited document (in-memory doc → bytes) | PDFium | `FPDF_SaveWithVersion` with `FPDF_INCREMENTAL` or full |
| Merge, split, page assembly, rotation (structural) | QPDF | `qpdfjob` JSON: `--pages`, `--rotate`, `--split-pages`, `--collate` |
| Encrypt/decrypt, permissions | QPDF | `--encrypt ... AES-256`, `--decrypt`, `--password` |
| Linearize, object streams, compress streams, remove unreferenced | QPDF | `--linearize`, `--object-streams=generate`, `--recompress-flate`, `--remove-unreferenced-resources=yes` |
| Repair | QPDF | reconstruction on open + `--check`, warnings surfaced |
| Low-level object edits: outlines (write), metadata (write), attachments, page labels, Info dict, XMP | QPDF | `--json` v2 read; `--update-from-json`; `--add-attachment`, `--remove-attachment` |
| Image recompression inside PDF (compress feature) | QPDF (locate image XObjects) + libvips (re-encode) + QPDF (replace stream via JSON) | |
| Digital signatures, redaction geometry, compare, OCR layer, Bates, DOCX export | **Ours** (managed), using the above as primitives | |

Rule: never implement with PDFium what QPDF does better structurally, and vice-versa. If both can do
something (e.g. rotate), prefer QPDF for save-time structural edits and PDFium only for live preview.

## Native binding layer

- `Modit.Native.Pdfium`: `LibraryImport` declarations for `pdfium.dll`, curated subset (~180 functions) across
  `fpdfview.h`, `fpdf_edit.h`, `fpdf_text.h`, `fpdf_annot.h`, `fpdf_formfill.h`, `fpdf_doc.h`, `fpdf_save.h`,
  `fpdf_structtree.h`, `fpdf_attachment.h`, `fpdf_ppo.h`, `fpdf_transformpage.h`, `fpdf_flatten.h`,
  `fpdf_searchex.h`, `fpdf_thumbnail.h`, `fpdf_progressive.h`, `fpdf_catalog.h`, `fpdf_signature.h` (read-only).
  Generated once with ClangSharpPInvokeGenerator, then hand-reviewed; `SafeHandle` wrappers for `FPDF_DOCUMENT`,
  `FPDF_PAGE`, `FPDF_TEXTPAGE`, `FPDF_BITMAP`, `FPDF_FORMHANDLE`, `FPDF_ANNOTATION`.
- `Modit.Native.Qpdf`: bindings for `qpdf-c.h` (`qpdf_init`, `qpdf_read`, `qpdf_write`, error handling) and
  `qpdfjob-c.h` (`qpdfjob_run_from_json`, `qpdfjob_create_from_json`, logger callbacks). Memory-based input/
  output via `qpdf_read_memory` / `qpdf_get_buffer`.
- Both DLL sets resolved via `NativeLibrary.SetDllImportResolver` from `<app>\native\<rid>\`.
- All PDFium calls happen on the host's `PdfiumThread` (a single-thread `TaskScheduler`). Managed façade
  methods are `async` and `Post` to that scheduler. A debug-build assertion checks thread identity.
- `FPDF_InitLibraryWithConfig` once per host with `m_pUserFontPaths` including Windows font dirs; `FPDF_DestroyLibrary` on shutdown.
- Document loading: `FPDF_LoadCustomDocument` with `FPDF_FILEACCESS` callbacks over a `MemoryMappedFile` of the
  source (no full read into managed heap). Password prompt loop on `FPDF_ERR_PASSWORD`.

## Rendering details

- Target: 32-bit BGRA premultiplied (`FPDFBitmap_BGRA`), `FPDF_ANNOT | FPDF_LCD_TEXT off | FPDF_NO_NATIVETEXT off`,
  `FPDF_RENDER_FORCEHALFTONE` off; `FPDF_REVERSE_BYTE_ORDER` as needed for Win2D (`DirectXPixelFormat.B8G8R8A8UIntNormalized` expects BGRA — matches PDFium's default).
- Tile render: compute matrix for (page, zoom, tile origin), clip rect = tile; `FPDF_RenderPageBitmapWithMatrix`.
  Forms drawn with `FPDF_FFLDraw` using the same matrix/clip (requires form handle initialized per document).
- Progressive render (`FPDF_RenderPageBitmap_Start` + `FPDF_RenderPage_Continue` with `IFSDK_PAUSE`) used for
  **large pages at high zoom** so cancellation can interrupt a long render within ~20 ms.
- Color management: PDFium renders sRGB; honor Windows HDR/advanced color by letting Win2D composite. Night mode
  via Win2D `ColorMatrixEffect`, not re-render.
- Print: render at printer DPI (300/600) in bands to `Windows.Graphics.Printing` `PrintDocument`; or vector print
  via `FPDF_RenderPage` to an HDC (GDI path) when the printer driver supports it — use GDI HDC path by default
  for vector fidelity, bitmap for "print as image" option.

## Text layer and search

- Per page: `FPDFText_LoadPage` → cache `CharInfo[] {unicode, bbox(page pts), fontSize, rotation}`; build
  words/lines by geometric clustering (gap > 0.3×fontSize → new word; baseline delta > 0.5×fontSize → new line).
- Search: `FPDFText_FindStart` per page for literal; regex search operates on our reconstructed line text with
  char-index back-mapping to boxes. Search runs in a render host as a background job yielding `(page, rects)`.
- Selection: hit-test via `FPDFText_GetCharIndexAtPos`, range selection yields rects via
  `FPDFText_CountRects`/`GetRect`. Column-aware selection by using detected text blocks.
- Copy: plain text, RTF (bold/italic heuristics off in v1), and "Copy as table" when a grid is detected (P3).

## Structural page operations (EditLog → QPDF)

EditLog compiles into a single `qpdfjob` JSON. Example: user merged B.pdf after page 3 of A.pdf, rotated page 5,
deleted page 7:

```json
{
  "inputFile": "A.pdf",
  "pages": [
    { "file": "A.pdf", "range": "1-3" },
    { "file": "B.pdf", "range": "1-z" },
    { "file": "A.pdf", "range": "4-6,8-z" }
  ],
  "rotate": ["+90:5"],
  "objectStreams": "generate",
  "outputFile": "out.pdf"
}
```

- Bookmarks: QPDF `--pages` drops outlines of secondary files; we rebuild outlines ourselves: read outlines of
  every source via `--json` (`"qpdf":{"outlines":...}` isn't exposed directly; parse `/Outlines` tree from
  objects), remap page refs via `--json` page object ids, then `--update-from-json` on the output. Merge UI option:
  "Keep bookmarks: nested under file names / flattened / none".
- Page labels similarly remapped. Form fields: QPDF handles AcroForm merging (`--pages` merges /AcroForm with
  field renaming when names collide). Annotations travel with pages.
- Split: `--split-pages=N`, by ranges, by outline level (we compute ranges from outline tree), by size
  (binary search on page count with trial writes to memory), by blank detection (render page at 50 dpi, compute
  luminance variance < threshold).

## Annotations

- Model: `Annotation {Id, PageIndex, Subtype, Rect, Color, Opacity, Author, Contents, Created, Modified, Flags,
  Subtype-specific (QuadPoints, InkLists, Vertices, LineEndings, Border, FontSize, Icon, StampName/Image)}`.
- Create via `FPDFPage_CreateAnnot`, set `/Rect`, `/QuadPoints`, `/C`, `/CA`, `/T`, `/Contents`, `/M`, ink via
  `FPDFAnnot_AddInkStroke`. PDFium generates appearance streams (`FPDFAnnot_SetAP` for custom; built-in AP
  generation covers highlight/underline/squiggly/strike/ink/square/circle/text). For FreeText and Stamps we
  generate our own AP content stream (text with embedded font via `FPDFText_LoadFont` into a form XObject is not
  supported directly — instead draw FreeText as page objects inside the AP by building the stream string and
  setting via `FPDFAnnot_SetAP`, with font resources referenced from a shared `/DR`).
- Replies/threads: `/IRT` + `/RT /R`, status via `/State`. Comments panel groups by `/IRT` root.
- Flatten: `FPDFPage_Flatten(FLAT_NORMALDISPLAY)` per page (also flattens forms); offer "flatten annotations only"
  by temporarily hiding fields (set `/F` hidden) — v1 uses PDFium's full flatten with a clear label.
- Stamps: built-in library as SVG (Approved, Draft, Confidential, Received, dynamic date/user) rasterized at
  300 dpi via resvg → image XObject AP; custom image stamps via libvips → JPEG/Flate.

## Forms

- Init `FPDF_FORMFILLINFO` v2 with callbacks (invalidate → tile cache invalidation, focus, set cursor).
- Fields enumerated via `FPDFPage_GetAnnotCount` + `FPDFAnnot_GetFormFieldType/Name/Value/Options`.
- Interaction: forward mouse/keyboard (`FORM_OnLButtonDown`, `FORM_OnChar`, ...) in page coordinates on the
  PDFium thread; or (default UI) render our own WinUI editors overlaid on field rects and write values with
  `FORM_SetFieldValue`-equivalents (`FPDFAnnot_SetStringValue(/V)` + `FORM_ForceToKillFocus` + regenerate AP).
  Decision: **our own WinUI editors** for accessibility and consistency; PDFium's internal widgets only for
  JavaScript-free calculations (none, JS is disabled).
- Import/export: FDF/XFDF parsers/writers (ours, small). CSV batch fill → N output PDFs.
- Field designer (P2): create widget annots with `/FT`, `/T`, `/Ff`, `/DA`, `/MK`; register in `/AcroForm /Fields`
  via QPDF JSON update at save time.

## Content editing (object level)

- Insert image: libvips normalizes → JPEG (photos) or PNG→Flate (graphics); `FPDFPageObj_NewImageObj`,
  `FPDFImageObj_LoadJpegFileInline` or `FPDFImageObj_SetBitmap`; matrix set; `FPDFPage_InsertObject`;
  `FPDFPage_GenerateContent`.
- Insert text: `FPDFText_LoadFont` (TrueType bytes from Windows font via DirectWrite enumeration, embed subset:
  PDFium embeds the full font; add our own subsetter in P2 using `FontSubsetter` over OpenType tables),
  `FPDFPageObj_CreateTextObj`, `FPDFText_SetText`, fill color, matrix.
- Edit existing text object: `FPDFTextObj_GetText` → change string → `FPDFText_SetText` (same font only; glyph
  availability check); move/scale via `FPDFPageObj_Transform`; delete via `FPDFPage_RemoveObject`.
- Extract images: iterate objects, `FPDFImageObj_GetImageDataDecoded` for raw JPEG/JPX pass-through or
  `GetBitmap` → libvips encode.

## Watermark, header/footer, page numbers, Bates

- Implemented as page object insertion on each page (text objects with embedded font, image objects), **not** as
  annotations (so they survive flattening and print reliably). Option "as annotation (removable)" for watermarks.
- Layout engine: our `PageStampLayout` computing positions from anchors (9-grid), margins, rotation (diagonal
  45°), tiling, opacity via ExtGState (`FPDFPageObj_SetFillColor` alpha → PDFium emits `/CA /ca` in GS).
- Bates: pattern `{prefix}{number:000000}{suffix}`, start number, increment per page or per document across a
  batch, position, font, also written to XMP (`modit:Bates`) for search.
- Page numbers: formats `1`, `1 of N`, `Page 1`, roman, with page-range filters; token engine shared with
  output-naming patterns.

## Security

- Protect: QPDF `--encrypt user owner 256 --print=full|low|none --modify=... --extract=n --annotate=n --`
  → always AES-256 (R6). Offer 128-bit AES only for "compatibility with very old readers" (explicit).
- Unlock: `--password=... --decrypt`. If only user password is known and owner restrictions apply, QPDF
  still decrypts (it's the owner's responsibility); we **require** the UI to show a legal notice checkbox when
  removing restrictions with user-only password (policy may forbid; see 08).
- Sanitize: remove `/JavaScript`, `/JS`, `/OpenAction`, `/AA`, `/EmbeddedFiles`, `/Launch` actions, hidden
  `/OCProperties`, metadata; implemented via QPDF JSON walk + `--update-from-json` with object removal.
- Permissions display: `FPDF_GetDocPermissions` bitfield → human readable.

## Compress / optimize

Pipeline per preset:
1. QPDF `--json` to enumerate image XObjects (dims, filter, bits, color space, size).
2. For each image above threshold: decode via PDFium `FPDFImageObj_GetBitmap` (handles masks/colorspaces) → libvips
   resize to target DPI (based on image placement size on page from object bbox) → encode JPEG q=preset (or keep
   lossless for images with alpha/SMask or line art detection: few unique colors → PNG/Flate).
3. Replace stream data via QPDF JSON (`"stream":{"data":base64}` with `/Filter /DCTDecode`) — for large images use
   `--update-from-json` with external file reference to avoid base64 blow-up.
4. Remove unreferenced resources, dedupe identical streams (hash), drop thumbnails (`/Thumb`), piece info, metadata
   (optional), unembed standard-14 fonts (optional), object streams, recompress Flate.
5. Report: before/after size, per-category savings, warnings (e.g. "JPX images converted to JPEG").

Presets: **Email** (150 dpi, q 70), **Balanced** (200 dpi, q 80), **Print** (300 dpi, q 90, lossless kept),
**Lossless** (structure only). Never compress images already smaller than target.

## OCR → searchable PDF

1. Per page: has text layer? (char count > threshold and not all whitespace) → skip unless "force".
2. Render at 300 dpi grayscale (libvips: deskew via `vips_rot` with Hough-estimated angle, adaptive threshold)
3. Engine: Windows OCR (`OcrEngine.TryCreateFromLanguage`, `SoftwareBitmap` input) or Tesseract (`TessBaseAPI` →
   `GetIterator` word boxes + confidences, hOCR optional).
4. Insert invisible text: one `FPDFPageObj_CreateTextObj` per word with `FPDFTextObj_SetTextRenderMode(FPDF_
   TEXTRENDERMODE_INVISIBLE)`, font = embedded sans (GlyphLessFont-like: embed a small TrueType; PDFium requires
   a real font), font size fitted to word box height, horizontal scale via matrix to word width.
5. Optional: apply the deskew/rotation to the page (`/Rotate` or re-render image-only pages as new image).
6. Save; verify by extracting text and comparing word count to OCR output.

## Redaction (P1)

- Marks: rectangles (manual, from text selection, from search/regex: SSN, credit card, email, phone, custom).
  Stored as `/Redact` annotations until "Apply".
- Apply (true removal), per page:
  1. Text: for each text object intersecting a mark, split the object: keep glyphs outside marks (rebuild text
     object with retained chars and adjusted positions via per-char matrices — PDFium lacks per-glyph removal,
     so we rebuild as multiple text objects using char boxes), remove glyphs inside.
  2. Images: for images intersecting marks, decode → black out region in image space (inverse CTM) → re-encode →
     replace.
  3. Paths: paths wholly inside → remove; partially → clip by adding a clipping path (PDFium: `FPDFPageObj_
     ...` lacks clip API → fallback: rasterize affected path objects' region... v1: remove path wholly inside;
     partial paths are covered by the overlay and flagged in the report).
  4. Draw filled black rectangle (or custom color/text "REDACTED") as page object.
  5. Annotations and form fields intersecting → removed. Bookmarks/metadata text matching patterns → offered.
  6. "Rasterize page" fallback option guarantees removal (page → image at 300 dpi + OCR text layer re-added
     excluding redacted boxes).
- Report: JSON + PDF list of applied redactions (page, rect, pattern, source). Mandatory verification pass:
  extract text from output and assert no redacted strings remain.

## Digital signatures (P1)

- Signing (PAdES-B-B / B-T, also legacy adbe.pkcs7.detached):
  1. Add signature field widget (rect, page) with appearance (name, date, reason, logo, optional raster of
     certificate info) via PDFium; save incrementally to temp.
  2. Our `PdfIncrementalWriter` appends an incremental update: `/Sig` dict with `/ByteRange [0 a b c]` placeholder
     and `/Contents <0000...>` reserved (e.g. 16 KB), `/SubFilter /ETSI.CAdES.detached`, `/M`, `/Reason`,
     `/Location`, update `/AcroForm /SigFlags 3`, xref stream.
  3. Hash bytes outside `/Contents` (SHA-256), build CMS via `SignedCms` with `X509Certificate2` from
     `StoreLocation.CurrentUser/My` (CNG keys, smart cards supported through Windows CSP/KSP) or PFX; add
     ESS signing-certificate-v2 attribute; optional RFC 3161 timestamp token (`Rfc3161TimestampRequest`) →
     B-T; LTV (DSS with OCSP/CRL) in P2.
  4. Write DER into the reserved hex slot; verify by re-reading.
- Verify: parse all `/Sig` fields (PDFium `FPDF_GetSignatureCount`, `FPDFSignatureObj_GetContents/ByteRange`),
  validate CMS (`SignedCms.CheckSignature`), chain build with `X509Chain` (revocation online if policy allows),
  byte-range coverage check (document modified after signing?), timestamp verification. Display in a
  "Signatures" panel with green/yellow/red status mirroring Acrobat semantics.
- Certify (DocMDP) P2.

## Compare (P2)

- Visual: render both at same DPI, align by page pairing (same index; or outline/text similarity), pixel diff
  with tolerance, cluster diff regions into rects; overlay view (old red / new blue / both) and side-by-side with
  synced scroll.
- Text: extract words with positions, diff via Myers on word sequences per page, highlight insertions/deletions;
  report export (PDF/HTML).

## PDF → text/markdown/HTML

- Reading order: XY-cut over text blocks; column detection; header/footer detection by repetition across pages;
  hyphenation join; list detection; heading detection by font size clusters (largest 2–3 clusters above body →
  H1–H3); tables via ruling lines (paths) or whitespace alignment (P2) → Markdown tables.
- HTML output: absolute-positioned (layout) or flowing (semantic) modes.

## PDF → DOCX (P2)

Own engine on `DocumentFormat.OpenXml`: paragraphs from the semantic model above, runs with font family/size/
bold/italic/color, images inline anchored, tables, page breaks per page, headers/footers if detected. Target:
"readable and editable", not pixel fidelity. Label in UI accordingly.

## PDF/A (P3)

Conversion best-effort: embed all fonts (fail if not embeddable), convert DeviceRGB/CMYK with output intent
sRGB ICC, remove JS/encryption/transparency groups issues, XMP with pdfaid. Validation: implement a subset of
checks; full veraPDF-grade validation requires a decision (see 13).

## Thumbnails and page previews

`FPDF_RenderPageBitmap` at fit-160px; use embedded `/Thumb` via `FPDFPage_GetDecodedThumbnailData` when present
and large enough. Disk cache keyed by `SHA-256(first 1 MB + size + mtime)` + page index + zoom class.

## Robustness

- Open timeouts (10 s for parse); page render timeout 5 s interactive (then progressive pause), 60 s batch.
- Fuzz corpus (see 11); any PDFium crash is a host restart + "This file could not be rendered" with retry/repair.
- Memory: cap per host via Job Object (default 2 GB; policy); PDFium `FPDF_LIBRARY_CONFIG` v3 with custom
  allocator hooks not needed; monitor working set and shed caches.
