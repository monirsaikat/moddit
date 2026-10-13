# 07 — Performance Engineering

Performance is a feature with budgets, measured in CI on fixed hardware profiles. "Fast" is defined numerically.

## Reference machines

| Profile | Spec | Role |
|---------|------|------|
| **Baseline** | 4-core/8-thread laptop (e.g. i5-1135G7), 8 GB RAM, SSD, integrated GPU, 1080p @ 125% | All budgets must pass |
| **Low** | 2-core/4-thread, 4 GB RAM, HDD, 1366×768 | Must remain usable (2× budgets) |
| **ARM64** | Snapdragon X class, 16 GB | Parity with Baseline |
| **High** | 16-core desktop, 32 GB, discrete GPU, 4K @ 150% | Must scale (parallel batch) |

## Budgets (Baseline, p95)

| Scenario | Budget |
|----------|--------|
| Cold start (process launch → Home interactive, R2R, warm disk cache) | ≤ 1.5 s (cold disk ≤ 3 s) |
| Warm start (second launch) | ≤ 0.8 s |
| Open 10-page text PDF → first page painted | ≤ 300 ms |
| Open 500-page, 100 MB scanned PDF → first page painted | ≤ 900 ms |
| Scroll at 100% zoom on text PDF | 60 fps, no frame > 33 ms; tiles visible ≤ 100 ms after stop |
| Zoom step (ctrl+wheel) | Scaled placeholder in same frame; exact tiles ≤ 150 ms |
| Thumbnail strip for 500 pages | First screen ≤ 400 ms, all visible as you scroll ≤ 50 ms each |
| Text search "the" in 500-page text PDF | First hit ≤ 200 ms, all hits ≤ 3 s, UI responsive |
| Merge 20 PDFs / 1,000 pages total (text) | ≤ 3 s |
| Split 1,000 pages to single files | ≤ 6 s |
| Rotate/delete/reorder in Pages mode | ≤ 16 ms UI response (EditLog), save ≤ 2 s for 1,000 pages |
| Compress 100 MB scanned PDF (Balanced) | ≤ 25 s (parallel across pages) |
| OCR 50-page scan (Windows OCR, 300 dpi) | ≤ 60 s (≈1.2 s/page), parallel 2–4 pages |
| Convert 100 JPEGs (12 MP) → WebP q80 | ≤ 20 s (≈5/s on 4 threads) |
| SVG (1 MB) → PNG 2048px | ≤ 150 ms |
| DOCX → PDF (LibreOffice, 20-page) | ≤ 6 s warm, ≤ 15 s cold (pack process start) |
| Memory: idle app | ≤ 120 MB private working set (UI process) |
| Memory: viewing 500-page scanned PDF | UI ≤ 400 MB; each render host ≤ 600 MB; total ≤ 1.5 GB |
| Memory: batch of 1,000 images | Bounded regardless of count (streaming), ≤ 1 GB total |
| Installer size (base, per RID) | ≤ 90 MB MSIX; engine packs separate |
| Disk cache | Default cap 1 GB, LRU, user-configurable |

## Startup

- **Publish**: self-contained, `PublishReadyToRun=true`, `TieredPGO`, `TieredCompilation` on, trimming
  `PublishTrimmed` with WinUI-compatible rd.xml where needed; `InvariantGlobalization=false` (we localize).
- **Measure**: `App()` → `OnLaunched` → first `Loaded` of Home; ETW events `Modit-App/Startup/*`.
- **Defer**: DI container builds only Home dependencies eagerly; engine hosts start lazily (or pre-warm one render
  host on a background thread 200 ms after Home paints when a file is being opened).
- **Avoid**: reflection-heavy JSON (use source-generated `JsonSerializerContext`), XAML `ResourceDictionary`
  bloat (merge per-page dictionaries lazily with `x:Load`), large static constructors, synchronous file IO on UI.
- **Prefetch**: recent-file thumbnails read from disk cache on a background thread.
- **Single instance**: `AppInstance` redirection makes "double-click second PDF" ≤ 200 ms to new tab.

## Tile renderer

- Tile 256×256 device px; zoom buckets as in 02; `VisibleTiles = viewport ∩ page rects` expanded by one tile
  margin in scroll direction (prefetch), computed per frame with zero allocations (struct enumerators).
- Requests coalesced per frame into one RPC batch per host, prioritized by distance from viewport center and
  cancelled if scrolled out before render starts (host checks a shared cancellation flag array).
- Host renders into shared-memory slots (ring of 64 × 256 KB); UI uploads via `CanvasBitmap.CreateFromBytes`
  on the UI thread — ≤ 0.3 ms per tile; max 24 uploads per frame to stay under 8 ms.
- Cache: `TileCache` with two tiers: GPU `CanvasBitmap` (budget 256 MB default, 128 MB on < 8 GB RAM) and
  encoded/raw RAM tier (512 MB) for fast re-upload; eviction by score = distance² × age.
- Zoom change: draw existing bucket scaled with `CanvasImageInterpolation.Linear` (text stays readable), schedule
  exact renders; after 120 ms idle, drop far buckets.
- Page layout cached in document points; recomputed only on layout mode/rotation change.
- Overlays (selection, hits, annotations in progress) drawn as geometry in the same `Draw`, with
  `CanvasCachedGeometry` for static sets.
- Text layer fetched lazily per page in background; cached in UI as `CharBox[]` (struct array), pooled.
- Hi-DPI: `CanvasVirtualControl.DpiScale` honored; tile grid in device pixels so 125%/150%/200% stay crisp.

## Engine hosts

- Pool sizes: render = clamp(cores/2, 1, 4); batch = clamp(cores-1, 1, 8); policy-overridable.
- Each host: Native AOT (startup ≤ 60 ms), `GCSettings.LatencyMode = SustainedLowLatency` for render hosts,
  `ServerGC=false`, `ConcurrentGC=true`, `TieredPGO`.
- PDFium: `FPDF_LoadCustomDocument` over memory-mapped input (no 100 MB managed array); per-document page
  cache limited to 8 loaded `FPDF_PAGE`s (LRU); text pages cached 16.
- libvips: `Concurrency=2`, `Cache.MaxMem=64MB`, `Cache.Max=100 ops`, sequential access, no `Image.ToArray`
  for large outputs — write via `WriteToStream`.
- Job Object limits: memory 2 GB (render), 4 GB (batch) default; on `JOB_OBJECT_MSG_JOB_MEMORY_LIMIT` → graceful
  cancel of current op and report "File too large for current limits (adjust in Settings/Policy)".
- Parallelism: scheduler assigns batch items to hosts round-robin; page-parallel operations (compress, OCR, PDF→
  images) split page ranges across hosts and merge results with QPDF.

## IPC

- MessagePack formatter, `UseLargeBuffer`, no per-message allocations beyond DTOs; DTOs are `struct` records
  where ≤ 64 bytes.
- Shared memory for anything > 16 KB (tiles, extracted images, OCR bitmaps). Slots are leased with generation
  numbers; stale completions ignored.
- Named pipe buffer 1 MB; `PipeOptions.Asynchronous | CurrentUserOnly`.
- Measured RPC overhead target ≤ 0.3 ms round trip for small calls.

## Memory discipline

- `ArrayPool<byte>.Shared` / `MemoryPool` for all transient buffers; `Span<T>` in parsers; no LINQ in hot paths
  (analyzer `Meziantou.Analyzer` rules enabled for allocations in `*.Hot` namespaces).
- `IDisposable` for every native handle (`SafeHandle`); analyzer CA2000 enforced.
- Trim thumbnails and tile cache on `MemoryManager.AppMemoryUsageLimitChanging` / low-memory notifications
  (`CreateMemoryResourceNotification`), and when minimized for > 30 s.
- Large documents: never materialize whole-document text; search streams page by page.

## Disk I/O

- Memory-mapped inputs; outputs via `FileStream` with 1 MB buffer, `FileOptions.SequentialScan` for reads,
  `WriteThrough` off (fsync at end).
- Temp files on the same volume as output for atomic `ReplaceFile`; `%TEMP%` only for intermediates; cleanup on
  completion and on startup (orphans older than 1 day).
- Thumbnail disk cache: SQLite index + files; writes batched in a background queue.

## Measurement and gates

- **Startup/UI**: Windows Performance Recorder profile `tools/perf/modit.wprp`; ETW events via `EventSource`
  `Modit-App`; `tools/perf/measure-startup.ps1` launches 10× and reports p50/p95.
- **Micro**: BenchmarkDotNet projects `bench/Modit.Bench.*` for tile math, range parser, text clustering, naming
  engine, CSV/XLSX, QPDF job build.
- **Macro**: `bench/Modit.Bench.E2E` (console, uses Core + hosts) runs the budget table on the fixtures corpus;
  results JSON compared to `bench/baselines/<machine-profile>.json` with ±10% tolerance; regression fails CI on
  the self-hosted Baseline runner, warns on hosted runners (noisy).
- **Frame timing**: in-app perf overlay (Ctrl+Shift+Alt+P) shows fps, frame time, tiles/frame, cache sizes,
  host RSS; `--perf-log` writes frame stats CSV for test runs.
- **Memory**: dotMemory Unit / `dotnet-counters` sampling in E2E bench; leak test opens/closes 200 documents and
  asserts working set returns within 10%.

## Known hot spots and mitigations

| Hot spot | Mitigation |
|----------|------------|
| PDFium single thread per host | Host pool; page-parallel splitting; prioritize interactive |
| Huge scanned pages (A0 at 600 dpi JPX) | Progressive render with pause; downsample on open for preview bucket; JPX decode is slow in PDFium → cache decoded image per page in host |
| Fonts loading on first render | Pre-warm PDFium font cache with Windows font dirs at host start |
| LibreOffice cold start (3–8 s) | Pre-warm when Convert Hub opens with office files; keep 1 instance warm 2 min |
| WebView2 first PrintToPdf (~1 s) | Create environment at first Convert Hub open |
| Thumbnail storm on open | Render visible first, then outward; disk cache; 160 px cap; JPEG q75 storage |
| GC pauses with many tiles | Tiles live in native GPU memory; managed side holds only small handles; pooled arrays |
| XAML `GridView` with 1,000+ items | `ItemsRepeater` with `UniformGridLayout` and recycling; async image loading |
| Large merge of 1,000 files | QPDF in one job (not N passes); streaming output; progress via QPDF logger callback |
