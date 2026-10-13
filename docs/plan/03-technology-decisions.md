# 03 — Technology Decisions (ADR digest)

Each entry: context → options → decision → consequences. Full ADRs live in `docs/adr/NNNN-*.md` once the repo
exists; this digest is the authoritative starting set. Status **LOCKED** means do not revisit without a new ADR
approved by the owner.

---

## ADR-001 Runtime: .NET 10 LTS, C# 14 — LOCKED

Options: .NET + C#; Rust + Tauri; C++ + Qt; Electron.
Decision: .NET 10 LTS. Highest velocity for a model-built codebase, first-class Windows integration (WinRT via
CsWinRT, COM, Win32), excellent native interop (`LibraryImport` source-gen), mature tooling, long support window.
Consequences: Native AOT for WinUI apps is supported since Windows App SDK 1.6 but has XAML reflection caveats;
plan for **trimmed, ReadyToRun (R2R) self-contained** publish for the app, and **Native AOT** for
`Modit.EngineHost.exe` and `Modit.Cli.exe` (no XAML). Verify AOT compatibility of every NuGet dependency in CI.

## ADR-002 UI framework: WinUI 3 (Windows App SDK) — LOCKED

Options: WinUI 3; WPF (.NET 9+ Fluent theme); Avalonia; MAUI.
Decision: WinUI 3 with latest stable Windows App SDK. Native Fluent 2 controls, Mica/Acrylic backdrops, modern
input (inking, touch, pen), Win2D integration, Microsoft's strategic direction, Store and MSIX native.
Fallback (documented, not planned): WPF with Fluent theme if a blocking WinUI defect appears; keep ViewModels
free of WinUI types so a swap is possible.
Consequences: ship as **packaged (MSIX)** primarily; also support **unpackaged** with self-contained Windows
App SDK runtime for MSI deployments (`WindowsAppSDKSelfContained=true`). Minimum OS: Windows 10 1809 (17763).

## ADR-003 Document canvas: Win2D CanvasVirtualControl + tile renderer — LOCKED

Options: Win2D; SwapChainPanel + raw Direct2D via Vortice; Image controls per page; WebView2 + pdf.js.
Decision: Win2D `CanvasVirtualControl` (region-invalidation, DirectX-backed, DPI-aware). Tiles arrive as BGRA
bytes in shared memory → `CanvasBitmap`. Overlays (selection, annotations in progress, search hits) drawn as
vector in the same `Draw` pass. Ink via `InkCanvas` layered on top in annotation mode.
Consequences: all render math in device pixels; keep per-frame allocations zero; use `CanvasRenderTarget` for
cached page overlays.

## ADR-004 PDF engine: PDFium (render/content) + QPDF (structure) — LOCKED

Options: PDFium; MuPDF (AGPL/commercial); iText (AGPL/commercial); PDFsharp (MIT, managed); Apryse/Foxit SDK
(commercial); Windows.Data.Pdf (render only).
Decision: **PDFium** (BSD-3, Chromium's engine, prebuilt via `bblanchon/pdfium-binaries`, win-x64 + win-arm64,
built without V8 and XFA) for rendering, text extraction, page object editing, annotations, form filling, image
insertion, save. **QPDF** (Apache-2.0, C API `qpdf-c.h` and `qpdfjob-c.h`, JSON object model
`--json` / `--update-from-json`) for merge/split/page assembly, encryption, decryption, linearization,
repair/reconstruct, object streams, bookmarks/outlines/metadata/attachments editing, dedupe.
No AGPL in the shipped product. Commercial SDKs reserved as optional future "fidelity packs" (see 13).
Consequences:
- Two engines, strict responsibility split (see 04). Both bound via own `Modit.Native.Pdfium` /
  `Modit.Native.Qpdf` projects using `LibraryImport` (AOT-friendly), generated from headers with ClangSharp once,
  then hand-curated.
- PDFium is single-threaded per document set → one PDFium thread per host, pool of hosts.
- Digital signatures, redaction, compare, PDF→DOCX, table extraction, bookmarks heuristics are **our code**.

## ADR-005 Raster imaging: libvips via NetVips — LOCKED

Options: libvips/NetVips; ImageMagick/Magick.NET; SkiaSharp; ImageSharp; WIC only.
Decision: libvips (LGPL-2.1, dynamic linking, `NetVips` + `NetVips.Native.win-x64/arm64` NuGet which bundles
codecs: libjpeg-turbo, libpng, libwebp, libtiff, giflib, libheif/AOM for AVIF, libjxl, librsvg, cgif).
Streaming, low memory, multi-threaded, fastest in class.
HEIC decode: NetVips native bundle includes libheif with free decoders for AVIF; HEVC (HEIC) decode uses
Windows **WIC + HEVC Video Extensions** when present (detect, fall back to clear error with Store link).
Consequences: run libvips only in engine hosts; set `NetVips.Concurrency=2` per host; set `NetVips.Cache` max
mem to 64 MB per host.

## ADR-006 Vector: resvg for SVG rasterization — LOCKED

Options: resvg (MIT, C API); librsvg (via libvips); Svg.Skia; WebView2.
Decision: resvg for SVG→raster (best conformance, fast, tiny). WebView2 `PrintToPdf` for SVG→PDF (vector kept).
Consequences: bind `resvg.h` in `Modit.Native.Resvg`; prebuilt DLLs from resvg releases; fonts via system
font dirs (`resvg_options_load_system_fonts`).

## ADR-007 HTML/Markdown/Text/Email → PDF: WebView2 print-to-PDF — LOCKED

Decision: `CoreWebView2.PrintToPdfAsync` with `CoreWebView2PrintSettings` (page size, margins, headers,
background graphics). Markdown via **Markdig** (BSD-2) with a bundled print stylesheet. Text via HTML with
`<pre>` and monospace font. `.eml/.msg` via **MsgReader** (MIT) to HTML.
WebView2 runs in the **UI process** (it is itself multi-process and sandboxed) in a hidden `CoreWebView2
Environment` with a dedicated user data folder under cache. For CLI, use `CoreWebView2Environment` without a
window via `CoreWebView2ControllerWindowReference` on a message-only HWND.
Consequences: WebView2 Runtime is an install dependency (Evergreen; present on Win11 and most Win10). MSI
bootstrapper installs it if missing; policy may point to a fixed-version runtime for air-gapped sites.

## ADR-008 OCR: Windows OCR default + Tesseract engine pack — LOCKED

Decision: `Windows.Media.Ocr` (zero install, uses OS language packs, good Latin accuracy) is the default engine.
**Tesseract 5** (Apache-2.0) + Leptonica as an optional engine pack with `tessdata_best` languages downloaded
on demand, for higher accuracy, more languages, hOCR/TSV output with confidences, and deterministic results
in CLI/server scenarios. Searchable PDF built by inserting invisible text objects (render mode 3) positioned by
word boxes via PDFium.
Consequences: pre-processing (deskew, binarize, despeckle) via libvips before OCR.

## ADR-009 Office documents: LibreOffice headless engine pack; MS Office COM when present — LOCKED

Decision: `soffice.exe --headless --convert-to` from a **LibreOffice** (MPL-2.0) engine pack (~350 MB, not in
base installer; downloaded or pre-staged by IT). Single global instance lock (LibreOffice does not tolerate
parallel instances sharing a profile) → use per-job `-env:UserInstallation=file:///...` isolated profiles to
allow N parallel instances. When Microsoft Office is installed and policy allows, offer "Use Microsoft Office
(highest fidelity)" via COM automation (`Word.Application.ExportAsFixedFormat`) in a dedicated host process with
a watchdog (Office COM hangs are common).
**Pandoc** (GPL-2.0, separate process, optional pack) for Markdown/RST/EPUB/DOCX text-centric conversions.
Consequences: engine packs have manifests, SHA-256, license text shown before download; policy can block.

## ADR-010 Media: FFmpeg LGPL build, engine pack — LOCKED

Decision: FFmpeg (LGPL build, no GPL components like x264 → use `libopenh264`/hardware encoders via
`h264_mf`/`hevc_mf` Media Foundation on Windows; `libvpx`, `libaom`, `libopus`, `libmp3lame` are LGPL/BSD).
Progress parsed from `-progress pipe:1`. Presets defined as JSON.
Consequences: output quality for H.264 relies on hardware MF encoders or OpenH264; document this.

## ADR-011 Archives: SharpCompress + optional 7-Zip pack — LOCKED

Decision: **SharpCompress** (MIT) for ZIP/TAR/GZ/BZ2/XZ/7z read and ZIP/TAR/GZ write; RAR read. Optional
**7-Zip** (`7za.exe`, LGPL + unRAR license) engine pack for 7z write and faster large archives.
Consequences: path traversal ("zip slip") checks, size/ratio limits (decompression bombs) in our layer.

## ADR-012 Data formats — LOCKED

CSV: **Sep** (MIT, fastest) with CsvHelper fallback for exotic dialects. XLSX: **ClosedXML** (MIT) for
read/write; for huge files, OpenXML SAX streaming. JSON: System.Text.Json. YAML: YamlDotNet. XML: System.Xml.
TOML: Tomlyn. Encoding detection: `UTF.Unknown`.

## ADR-013 IPC: StreamJsonRpc over named pipes + shared memory — LOCKED

Options: gRPC over named pipes; StreamJsonRpc; custom binary protocol; COM.
Decision: **StreamJsonRpc** (MIT, from Visual Studio team) with **MessagePack** formatter, header-delimited
over `NamedPipeServerStream`/`ClientStream` with `PipeOptions.Asynchronous`. Bulk pixel/data buffers through
`MemoryMappedFile` ring of slots (e.g. 64 slots × 256 KB for tiles, plus on-demand large buffers), with slot
handles passed in RPC messages. Pipe ACL: current user only. Pipe name includes a per-session random GUID.
Consequences: idempotent request design, request IDs, cancellation tokens propagate over RPC natively.

## ADR-014 MVVM and DI — LOCKED

**CommunityToolkit.Mvvm** (source generators: `[ObservableProperty]`, `[RelayCommand]`), **Microsoft.Extensions.
DependencyInjection/Hosting/Logging/Options**. Navigation via our own `INavigationService`. Messaging via
`WeakReferenceMessenger`. No Prism, no ReactiveUI.

## ADR-015 Persistence — LOCKED

`Microsoft.Data.Sqlite` (bundled SQLite) for job queue, recent files, thumbnail index. JSON for settings and
recipes. No EF Core (startup cost, AOT friction).

## ADR-016 Packaging, distribution and updates: GitHub-centric — LOCKED

Three install channels, one release pipeline, GitHub as the distribution backbone (details in 15):

- **Velopack installer (`ModitSetup-x64.exe`, `-arm64.exe`) — primary consumer channel.** Per-user install to
  `%LOCALAPPDATA%\Modit`, no admin rights, one-click Setup with splash, shortcuts, file associations, WebView2
  prerequisite install (`--framework webview2`), **auto-update from GitHub Releases** via `GithubSource` with
  Zstandard delta packages, channels `stable` and `beta` (GitHub pre-releases), in-app "What's new", rollback to
  the previous full package. Velopack cannot install to Program Files; that is by design here (per-user channel).
- **MSI via WiX v5 (`Modit-x64.msi`, `-arm64.msi`) + Burn bootstrapper — enterprise per-machine channel.**
  Silent install, features, ADMX, SCCM/GPO/Intune Win32 app. Updates: in-app checker against the same GitHub
  Releases feed notifies only (no self-update for per-machine) unless policy `AllowUserUpdatesForMachineInstall`;
  IT deploys the new MSI. Major-upgrade semantics.
- **MSIX bundle + `.appinstaller` — Intune/Store channel.** Assets on GitHub Releases, `Modit.appinstaller`
  served from GitHub Pages (`https://<owner>.github.io/modit-releases/Modit.appinstaller`), update check every
  24 h via App Installer, Store submission optional after 1.0.
- **Portable zip** (`portable.flag`, settings beside exe, updater disabled, "check for updates" opens release page).
- **winget**: `Modit.Modit` (Velopack exe, `installerType: exe` with `/VERYSILENT`-style switches Velopack supports:
  `--silent`) and `Modit.Modit.MSI`; PRs to `microsoft/winget-pkgs` opened automatically on each stable release.
- **Signing**: every binary, installer and package signed with **Azure Trusted Signing** from GitHub Actions via
  OIDC (`azure/login` federated credential → `azure/trusted-signing-action`). No certificate files in CI.
- **Repositories**: private `modit` (source) and public `modit-releases` (release assets, release notes, Pages
  site, `rollout.json`, pack manifests). Updaters only ever talk to the public repo, so no tokens ship in the app
  and the 60 req/h unauthenticated GitHub API limit is per client IP, which is ample for a daily check.
- CI produces per release: `ModitSetup-x64.exe`, `ModitSetup-arm64.exe`, Velopack `*-full.nupkg` + `*-delta.nupkg`
  + `releases.win-x64-stable.json`, `Modit-x64.msi`, `Modit-arm64.msi`, `Modit-Setup-Enterprise.exe` (Burn),
  `Modit.msixbundle`, `Modit.appinstaller`, `Modit-portable-x64.zip`, `modit-cli-x64.zip`, SBOM, notices,
  `checksums.txt`, `RELEASE_NOTES.md`.

## ADR-017 Licensing of our product — assumption pending owner (see 13)

Architecture: Ed25519-signed license JSON (keys: product, edition, seats, expiry, features, hardware-binding
optional), offline activation via signed request/response files, volume keys without per-machine activation,
14-day trial with full features and watermark-free output (edition gating is per-feature flag). Validation lives in
`Modit.Licensing` and is checked in `Core` before executing gated operations, never only in the UI.

## ADR-018 Third-party license policy — LOCKED

Allowed in-process: MIT, BSD, Apache-2.0, MPL-2.0 (file-level copyleft is fine), LGPL (dynamic linking only,
original DLLs unmodified, relink instructions in third-party notices). Allowed as separate optional process with
disclosure: GPL (LibreOffice is MPL anyway; Pandoc GPL; 7-Zip LGPL/unRAR). **Forbidden**: AGPL, SSPL,
"non-commercial" licenses, anything without a license. CI runs a license scanner (`nuget-license` + a manifest
for native binaries) and fails on unknown/forbidden licenses. `THIRD-PARTY-NOTICES.txt` generated per build.

## ADR-019 Telemetry and crash reporting — LOCKED

Opt-in only, off by default, policy can force off. Crash reporting: own minidump uploader to an owner-controlled
endpoint (or Sentry self-hosted) — never includes document data. Usage telemetry: counts of operations by type,
durations, engine versions, OS version. Nothing identifying files or users.

## ADR-020 Localization — LOCKED

`.resw` resources with `x:Uid`; `Microsoft.Windows.ApplicationModel.Resources`. String keys documented; use
pseudo-localization build in CI to catch hard-coded strings. Pluralization via `Humanizer` only for en-US; other
languages use explicit plural resource variants.

---

## Dependency manifest (initial)

| Purpose | Package / binary | License | Form |
|---------|------------------|---------|------|
| UI | Microsoft.WindowsAppSDK, Microsoft.Windows.SDK.BuildTools | MIT / MS | NuGet |
| Canvas | Microsoft.Graphics.Win2D | MIT | NuGet |
| MVVM | CommunityToolkit.Mvvm, CommunityToolkit.WinUI.* (Controls, Behaviors, Converters) | MIT | NuGet |
| DI/Logging | Microsoft.Extensions.* | MIT | NuGet |
| PDF render/edit | PDFium (bblanchon/pdfium-binaries, no V8, no XFA) | BSD-3 | Native DLL (x64, arm64) |
| PDF structure | QPDF (qpdf.dll + deps) | Apache-2.0 | Native DLL (x64, arm64) |
| Raster | NetVips + NetVips.Native.win-x64 / win-arm64 | MIT / LGPL-2.1 | NuGet |
| SVG | resvg (C API) | MIT | Native DLL |
| HTML→PDF | Microsoft.Web.WebView2 | MS | NuGet + Runtime |
| OCR | Windows.Media.Ocr; Tesseract 5 + Leptonica (pack) | OS / Apache-2.0 | WinRT / Native |
| IPC | StreamJsonRpc, MessagePack | MIT | NuGet |
| Markdown | Markdig | BSD-2 | NuGet |
| Email | MsgReader | MIT | NuGet |
| Archives | SharpCompress; 7-Zip (pack) | MIT; LGPL+unRAR | NuGet; exe |
| Data | Sep, ClosedXML, YamlDotNet, Tomlyn, UTF.Unknown | MIT/Apache | NuGet |
| SQLite | Microsoft.Data.Sqlite | MIT | NuGet |
| Office | LibreOffice (pack); Pandoc (pack) | MPL-2.0; GPL-2.0 | exe |
| Media | FFmpeg LGPL build (pack) | LGPL-2.1 | exe |
| Crypto | System.Security.Cryptography (CNG), System.Security.Cryptography.Pkcs | MIT | BCL |
| Installer | WiX v5, MSIX packaging tools, Velopack | MS-RL / MIT | Tooling |
| Tests | xUnit v3, FluentAssertions (v6, Apache), Verify, BenchmarkDotNet, FlaUI, Appium WinAppDriver | MIT/Apache | NuGet |
| Analyzers | Microsoft.CodeAnalysis.NetAnalyzers, BannedApiAnalyzers, Meziantou.Analyzer, Roslynator | MIT | NuGet |

Version pinning: central `Directory.Packages.props`; native binaries pinned by SHA-256 in
`native/manifest.json` and fetched by `tools/fetch-native.ps1` (cached in `native/bin/<rid>/`).
