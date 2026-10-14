# 02 — Architecture

## Guiding principles

1. **Native code never runs in the UI process.** PDFium, libvips, resvg, QPDF, Tesseract run in engine host
   processes. A crash in a malformed PDF restarts a host; the window never disappears.
2. **One core, many fronts.** GUI, CLI, watch-folder service and shell extension all call the same
   `Modit.Core` operation pipeline. If a feature is not in the CLI, it is not done.
3. **Operations are data.** Every user action that transforms a file is an `Operation` record (name + typed
   parameters) that can be serialized, queued, replayed, logged and composed into recipes.
4. **Streams and budgets, not whole files in memory.** Every stage respects a memory budget and cancellation.
5. **Interactive beats batch.** The scheduler always prioritizes viewer rendering over background jobs.

## Process topology

```
┌─────────────────────────────────────────────────────────────────────────┐
│  Modit.App.exe  (WinUI 3, UI thread + background threads)               │
│  ┌──────────────┐ ┌──────────────┐ ┌──────────────┐ ┌───────────────┐  │
│  │ Shell/Views  │ │ ViewModels   │ │ Modit.Core   │ │ Tile Cache    │  │
│  │ (XAML)       │ │ (MVVM)       │ │ (Operations, │ │ (GPU + RAM)   │  │
│  └──────────────┘ └──────────────┘ │  Jobs, DI)   │ └───────────────┘  │
│                                    └──────┬───────┘                     │
│                                           │ EngineClient (JSON-RPC over │
│                                           │ named pipes + shared memory)│
└───────────────────────────────────────────┼─────────────────────────────┘
                                            │
        ┌──────────────────┬────────────────┼──────────────────┬──────────────────┐
        ▼                  ▼                ▼                  ▼                  ▼
┌───────────────┐  ┌───────────────┐ ┌───────────────┐ ┌───────────────┐ ┌────────────────┐
│ EngineHost #1 │  │ EngineHost #2 │ │ EngineHost #n │ │ WebView2 Host │ │ External tools │
│ (render pool) │  │ (render pool) │ │ (batch ops)   │ │ (HTML→PDF)    │ │ soffice.exe    │
│ PDFium, vips, │  │ PDFium, vips  │ │ QPDF, vips,   │ │ hidden window │ │ ffmpeg.exe     │
│ resvg         │  │ resvg         │ │ Tesseract     │ │ in UI process │ │ pandoc.exe     │
└───────────────┘  └───────────────┘ └───────────────┘ └───────────────┘ └────────────────┘
   Job Object + restricted token, memory cap, no network       spawned per job, stdout parsed
```

- **Modit.App.exe** — WinUI 3 app. Owns windows, tabs, view models, tile cache, job scheduler.
- **Modit.EngineHost.exe** — console-less process hosting native engines behind a JSON-RPC server
  (StreamJsonRpc, MessagePack formatter) over a named pipe. Pixel buffers travel via shared memory
  (`MemoryMappedFile`), never through the RPC channel. Launched in a Windows Job Object with memory and process
  limits and a restricted token (no network, low integrity where feasible). One instance per logical role:
  - **Render hosts** (pool, size = clamp(cores/2, 1, 4)): PDFium rendering, text extraction, thumbnails.
    PDFium is **not thread-safe**; each host serializes PDFium calls on one dedicated thread.
  - **Batch hosts** (pool, size = clamp(cores-1, 1, 8)): long-running operations (merge, OCR, convert).
- **Modit.Cli.exe** — references Core directly and spins up engine hosts the same way. Headless.
- **Modit.Service.exe** (P1) — Windows Service / per-user background task for watch folders. Same Core.
- **Modit.Shell.dll** — sparse MSIX package / COM server for Explorer context menu (`IExplorerCommand`),
  invokes `Modit.App.exe` with arguments via protocol activation (`modit://`) or command line.
- **External tool processes** — LibreOffice, FFmpeg, Pandoc, 7-Zip run as child processes with a strict
  argv contract, stdout/stderr parsers for progress, timeouts, and kill-on-cancel via Job Object.

### Why out-of-process rendering (and its cost)

Rendering a page requires a cross-process round trip (~0.2–0.5 ms on named pipes) plus a shared-memory read.
A 256×256 tile render in PDFium takes 1–20 ms. The IPC overhead is below 10% and buys total crash isolation,
per-document memory caps, and parallelism despite PDFium's single-thread constraint. Chrome, Edge and Acrobat
use the same model. **LOCKED.**

### Host lifecycle

- Lazy start on first need; keep-alive while documents are open; idle shutdown after 60 s with no documents.
- Health: heartbeat every 2 s; on 3 missed → kill, respawn, re-issue in-flight requests (requests are idempotent).
- Documents opened in a host are tracked by `DocumentHandle` in `Core`; on respawn, the client re-opens the file
  (and re-applies unsaved in-memory edits from the edit log — see "Edit model" below).
- Crash telemetry (opt-in): minidump of the host only, never UI, never file contents.

## Layering inside the UI process

```
Modit.App (WinUI)            → Views, controls, converters, resources, App lifecycle, activation
Modit.App.ViewModels         → ViewModels, navigation, commands, state (no WinUI types except for ICommand)
Modit.Core                   → Operations, Jobs, Scheduler, Recipes, Document model, Edit log, Settings, Policies
Modit.Engine.Contracts       → RPC interfaces, DTOs, shared-memory protocol (shared by App/CLI/EngineHost)
Modit.Engine.Client          → Pipe client, host pool, respawn logic, tile request coalescing
Modit.Engine.Host            → RPC server, engine adapters (PdfiumEngine, QpdfEngine, VipsEngine, ResvgEngine, OcrEngine)
Modit.Native.*               → Raw P/Invoke bindings (Pdfium, Qpdf, Resvg, Tesseract); nothing else
Modit.Convert.*              → Converter implementations grouped by domain, registered in ConverterRegistry
Modit.Tools.*                → External process wrappers (LibreOffice, FFmpeg, Pandoc, SevenZip)
Modit.Licensing / .Audit / .Policy / .Update   → Enterprise cross-cutting modules
```

Dependency rule: arrows point downward only. `ViewModels` never reference `Engine.Host` or `Native`.
Enforced with `Microsoft.CodeAnalysis.BannedApiAnalyzers` and project references.

## Core domain model

```csharp
// Modit.Core/Documents
record DocumentId(Guid Value);
sealed class OpenDocument {            // a document open in the UI
    DocumentId Id; string? Path; DocumentKind Kind; // Pdf, Image, Svg, Text, ...
    int PageCount; IReadOnlyList<PageInfo> Pages;   // size, rotation, label
    EditLog Edits;                                  // uncommitted structural edits
    bool IsDirty; SecurityInfo Security; Metadata Meta;
}
record PageInfo(int Index, SizeF PointSize, int Rotation, string? Label);

// Modit.Core/Operations
abstract record Operation(string Name);             // closed set of records, e.g.
record MergeOperation(IReadOnlyList<MergeSource> Sources, MergeOptions Options) : Operation("pdf.merge");
record SplitOperation(SplitMode Mode, ...) : Operation("pdf.split");
record ConvertOperation(string TargetFormat, ConvertOptions Options) : Operation("convert");
record OcrOperation(OcrOptions Options) : Operation("pdf.ocr");
// ... each has a JSON schema (generated) used by CLI, recipes, and policy validation.

interface IOperationHandler<TOp> where TOp : Operation {
    OperationCapabilities Capabilities { get; }     // which engines, estimated cost, cancellable, resumable
    Task<OperationResult> ExecuteAsync(TOp op, OperationContext ctx, CancellationToken ct);
}

// Modit.Core/Jobs
sealed class Job {                                   // a scheduled unit of work
    JobId Id; JobPriority Priority;                  // Interactive(0) > UserBatch(1) > Background(2)
    IReadOnlyList<JobStep> Steps;                    // DAG; each step = Operation + input/output bindings
    JobState State; Progress Progress; JobError? Error;
}
interface IJobScheduler { JobId Enqueue(JobSpec spec); void Cancel(JobId id); IObservable<JobEvent> Events; }
```

### Edit model (viewer edits before save)

Structural page edits in the GUI (reorder, rotate, delete, insert) are **not** applied to the file on every
click. They append to an `EditLog` (list of `PageEdit` records) and the UI shows a virtual page sequence.
Rendering requests map virtual page → (source document, source page, rotation delta). Save/Save As compiles the
`EditLog` into one QPDF job (one write, one pass). This gives instant UI, unlimited undo/redo, and crash-safe
recovery (EditLog is journaled to `%LOCALAPPDATA%\ModitData\recovery\<docid>.json` every edit).

Content edits (annotations, form values, inserted objects) are applied in the render host's in-memory PDFium
document immediately (so re-render reflects them) **and** journaled as `ContentEdit` records for recovery.
Save = PDFium `FPDF_SaveWithVersion` to a temp file → then QPDF pass for structural EditLog → atomic replace.

### Save strategy (atomic, safe)

1. Write to `<target>.modit-tmp` in the same directory (same volume ensures atomic rename).
2. `fsync`, verify by re-opening with QPDF `--check` (fast structural check).
3. `ReplaceFile` Win32 (preserves ACLs, alternate streams, creates backup if setting enabled).
4. Never write directly to the original. Never leave temp files on failure (cleanup in `finally` + startup sweep).

## Rendering pipeline (viewer)

```
Scroll/zoom event → Viewport (page layout in document points) → VisibleTiles(zoom level, tile = 256px device)
 → TileCache.TryGet(docId, vpage, zoomBucket, tileXY) → hit: draw CanvasBitmap
                                                      → miss: RenderRequest to render host (coalesced per frame,
                                                              prioritized by distance to viewport center)
 host: FPDF_RenderPageBitmap with clip to tile rect, flags LCD_TEXT off, ANNOT on, into shared-memory slot
 ← TileReady(slot) → UI thread: CanvasBitmap.CreateFromBytes (or CanvasDevice upload) → cache → invalidate region
```

- Zoom buckets: 25, 33, 50, 67, 75, 100, 125, 150, 200, 300, 400 % (device-pixel aware). Between buckets, draw
  nearest cached bucket scaled (bilinear) and schedule exact render.
- Low-res "preview" tile: whole page at ≤ 64 px width rendered first for instant placeholder.
- Cache: GPU budget (default 256 MB, policy-configurable), RAM budget (512 MB), LRU with viewport-distance scoring.
- Thumbnails: separate pipeline, 160 px wide, cached on disk (`%LOCALAPPDATA%\ModitData\cache\thumbs`) keyed by
  file hash + mtime + page.
- Text layer: per-page character boxes from `FPDFText_*` fetched lazily for selection/search/accessibility.

## Conversion pipeline

```
Input file(s) → Sniff (magic bytes + extension) → FormatDescriptor
 → ConverterRegistry.FindPath(from, to, constraints) → ConversionPath (1..n steps, e.g. DOCX→PDF→PNG)
 → Each step: IConverter.ConvertAsync(stream in, stream out, options, progress, ct)
 → Output naming (pattern engine: {name}_{page:000}.{ext}) → Collision policy (overwrite/rename/skip) → Write atomically
```

Multi-step paths are computed with Dijkstra over a weighted graph (weight = quality loss + cost). Direct paths
always win over transitive ones unless the direct converter is unavailable (engine pack not installed).

## Settings, policies, state

| Store | Location | Format |
|-------|----------|--------|
| User settings | `%LOCALAPPDATA%\ModitData\settings.json` | JSON, schema-versioned, migrated on upgrade |
| Policies (admin) | `HKLM\SOFTWARE\Policies\Modit` then `HKCU\SOFTWARE\Policies\Modit` | Registry (ADMX), read-only, override settings |
| Recipes | `%LOCALAPPDATA%\ModitData\recipes\*.json` + `%PROGRAMDATA%\Modit\recipes` (shared) | JSON |
| Job queue | `%LOCALAPPDATA%\ModitData\queue.db` | SQLite (Microsoft.Data.Sqlite) |
| Caches | `%LOCALAPPDATA%\ModitData\cache\` | Files, size-capped, safe to delete |
| Recovery | `%LOCALAPPDATA%\ModitData\recovery\` | JSON journals |
| Audit log | `%PROGRAMDATA%\Modit\audit\*.jsonl` (policy may redirect) | JSONL, rotated |
| Engine packs | `%LOCALAPPDATA%\ModitData\engines\<name>\<version>\` or `%PROGRAMFILES%\Modit\engines` (admin) | Files + manifest.json + SHA-256 |

Effective setting = policy value if present (locked in UI with a "Managed by your organization" badge), else user value, else default.

## Extensibility (designed, not shipped in v1)

- Converters and operation handlers are discovered via DI registration in assemblies named `Modit.Convert.*`.
  A future plugin host could load signed assemblies from `%PROGRAMFILES%\Modit\plugins`; keep `IConverter`
  and `IOperationHandler` interfaces in `Modit.Core.Abstractions` with semantic versioning from day one.
- Recipes are JSON; a future visual recipe builder is UI-only work.

## Threading model

- UI thread: XAML only. Never block. All ViewModel commands are `AsyncRelayCommand`.
- `Core` is thread-agnostic; uses `IProgress<T>` and `CancellationToken` everywhere.
- Engine client has a dedicated I/O thread per pipe. Tile completions are posted to the UI via `DispatcherQueue`
  batched per frame (CompositionTarget rendering event) to avoid per-tile dispatch overhead.
- Engine hosts: 1 RPC dispatch thread + 1 dedicated PDFium thread (single-threaded apartment for PDFium) +
  libvips internal thread pool (set to 2 per host).

## Error model

- `ModitException` hierarchy: `EngineException` (host crashed/returned error), `FormatException`
  (unsupported/corrupt), `AccessException` (password/permissions), `IoException`, `PolicyBlockedException`,
  `CancelledException`.
- Every error carries a stable `ErrorCode` (e.g. `PDF_ENCRYPTED_NEEDS_PASSWORD`) used by CLI exit codes,
  localization and docs. Codes are listed in `docs/errors.md` (generated from the enum).
- User-facing errors always have: what happened, why (if known), what to do next, "Copy details".

## Logging and diagnostics

- `Microsoft.Extensions.Logging` with a rolling file sink (`%LOCALAPPDATA%\ModitData\logs\`, 7 days, 50 MB cap) and
  ETW/EventSource provider `Modit-App` for WPA/PerfView traces.
- "Collect diagnostics" button zips logs + settings (redacted) + engine versions + minidumps.
- Never log file contents, file names at Information level (Debug only), or passwords.
