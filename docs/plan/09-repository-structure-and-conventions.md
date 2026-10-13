# 09 — Repository Structure and Conventions

## Solution layout

```
modit/
├─ Modit.sln
├─ Directory.Build.props            # LangVersion, Nullable, TreatWarningsAsErrors, analyzers, versioning
├─ Directory.Packages.props         # central package versions
├─ global.json                      # SDK pin (10.0.x)
├─ .editorconfig                    # style + analyzer severities
├─ nuget.config
├─ CLAUDE.md                        # instructions for the implementing model (pointers into docs/plan)
├─ README.md
├─ docs/
│  ├─ plan/                         # this plan
│  ├─ adr/                          # ADRs (NNNN-title.md), start with 0001-stack.md digest from plan 03
│  ├─ admin/                        # IT docs (generated + written)
│  ├─ dev/                          # dev setup, native refresh, release process
│  └─ errors.md                     # generated from ErrorCode enum
├─ native/
│  ├─ manifest.json                 # {name, version, url, sha256, license, files[]} per binary per RID
│  ├─ bin/win-x64/ bin/win-arm64/   # fetched, git-ignored
│  └─ headers/                      # pdfium/, qpdf/, resvg/, tesseract/ headers used for binding generation
├─ src/
│  ├─ Modit.App/                    # WinUI 3 (packaged + unpackaged configs)
│  │  ├─ App.xaml(.cs), MainWindow.xaml
│  │  ├─ Views/ (pages, dialogs), Controls/ (custom controls), Themes/ (Tokens.xaml, Generic.xaml, HighContrast)
│  │  ├─ Services/ (WinUI-specific: dialogs, file pickers, DispatcherQueue adapters, WebView2Printer)
│  │  ├─ Strings/en-US/Resources.resw
│  │  ├─ Assets/ (icons, illustrations SVG→PNG at build), Package.appxmanifest, app.manifest
│  ├─ Modit.App.ViewModels/         # pure .NET, CommunityToolkit.Mvvm
│  ├─ Modit.Core/                   # Operations, Jobs, Scheduler, Documents, EditLog, Recipes, Settings, Policy, Naming, Errors
│  ├─ Modit.Core.Abstractions/      # IConverter, IOperationHandler, IEngine*, DTOs, FormatIds (stable API)
│  ├─ Modit.Engine.Contracts/       # RPC interfaces, shared memory protocol
│  ├─ Modit.Engine.Client/          # host pool, pipe client, respawn, tile request coalescer
│  ├─ Modit.Engine.Host/            # EXE (Native AOT): RPC server, PdfiumEngine, QpdfEngine, VipsEngine, ResvgEngine, OcrEngine
│  ├─ Modit.Native.Pdfium/          # LibraryImport bindings + SafeHandles only
│  ├─ Modit.Native.Qpdf/
│  ├─ Modit.Native.Resvg/
│  ├─ Modit.Native.Tesseract/
│  ├─ Modit.Pdf/                    # managed PDF logic: EditLog→qpdfjob compiler, outlines remap, signatures, redaction, compare, OCR layer, Bates, text semantics
│  ├─ Modit.Convert/                # registry, sniffing, naming, path finding
│  ├─ Modit.Convert.Images/ .Vector/ .Documents/ .Data/ .Media/ .Archives/ .Pdf/
│  ├─ Modit.Tools/                  # external process runners: LibreOffice, Office COM, FFmpeg, Pandoc, SevenZip, Poppler
│  ├─ Modit.OfficeHost/             # EXE for Office COM automation
│  ├─ Modit.Packs/                  # engine pack manager (manifest, download, verify, install)
│  ├─ Modit.Licensing/ Modit.Audit/ Modit.Policy/ Modit.Update/
│  ├─ Modit.Cli/                    # EXE (Native AOT) System.CommandLine
│  ├─ Modit.Service/                # EXE watch-folder service
│  └─ Modit.Shell/                  # Explorer IExplorerCommand COM server (C# AOT ComWrappers) + sparse package
├─ tests/
│  ├─ Modit.Core.Tests/ Modit.Pdf.Tests/ Modit.Convert.*.Tests/ Modit.Engine.Tests/ Modit.Native.Tests/
│  ├─ Modit.Rendering.GoldenTests/  # pixel golden tests (Verify + ImageSharp compare)
│  ├─ Modit.Cli.Tests/              # process-level CLI tests
│  ├─ Modit.App.UITests/            # FlaUI/WinAppDriver
│  ├─ Modit.Fuzz/                   # SharpFuzz harness + corpus runner
│  └─ fixtures/                     # small PDFs/images/etc. (LFS for > 1 MB), licenses noted
├─ bench/
│  ├─ Modit.Bench.Micro/ Modit.Bench.E2E/ baselines/
├─ installer/
│  ├─ msix/ (manifests, appinstaller template, sparse package for shell)
│  ├─ wix/ (Product.wxs, Features, Bundle.wxs, ADMX/)
│  ├─ velopack/
│  └─ winget/
├─ packs/                           # engine pack definitions (manifest templates, build scripts)
├─ tools/
│  ├─ fetch-native.ps1 build.ps1 sign.ps1 release.ps1 gen-bindings.ps1 gen-policies-doc.ps1 pseudo-loc.ps1
│  └─ perf/ (wprp, scripts)
├─ .github/
│  ├─ workflows/ (ci.yml, release.yml, nightly.yml, native-refresh.yml, winget.yml, pages.yml, codeql.yml, dependabot auto-merge)
│  ├─ ISSUE_TEMPLATE/ (bug.yml with diagnostics-zip instructions, feature.yml, format-request.yml), PULL_REQUEST_TEMPLATE.md
│  ├─ dependabot.yml, CODEOWNERS, release-drafter.yml or cliff.toml (changelog)
└─ (second repository `modit-releases`, public: release assets, Pages site, appinstaller, rollout.json, packs feed — see 15)
```

## Naming and style

- C# 14, `<Nullable>enable</Nullable>`, `<ImplicitUsings>enable</ImplicitUsings>`, `TreatWarningsAsErrors=true`,
  `AnalysisLevel=latest-all` with a curated `.editorconfig` suppressing noisy rules once, not per-file.
- File-scoped namespaces; one type per file; `sealed` by default; records for DTOs and Operations; `readonly
  struct` for small math types (`PagePoint`, `DevicePixel`, `TileKey`).
- Async: `Async` suffix, always accept `CancellationToken` last, never `async void` except event handlers in
  App; `ConfigureAwait(false)` in libraries (not in App/ViewModels).
- No `dynamic`, no reflection in hot paths, no `Task.Run` in libraries (callers decide), no static mutable state
  except caches with explicit lifetime.
- Logging via `ILogger<T>` with `LoggerMessage` source generators; structured properties; `EventId` per message.
- Public APIs in `Core.Abstractions` documented with XML docs; everything else internal (`InternalsVisibleTo`
  tests).
- Units: page geometry in **PDF points (1/72 in)** as `double`; device in `int` pixels; DPI explicit in names
  (`widthPx`, `widthPt`, `dpi`). Never bare `float x`.
- Error handling: throw `ModitException` subclasses with `ErrorCode`; never swallow; catch at job boundaries to
  produce `JobError`; UI maps codes to localized messages.
- Strings: no user-visible literal strings outside `.resw`; analyzer `CA1303` on for App/ViewModels.

## Dependency injection

- `Microsoft.Extensions.Hosting` `HostApplicationBuilder` in App, CLI, Service, Host. Each assembly exposes
  `IServiceCollection AddModitCore()`, `AddModitConverters()`, `AddModitPdf()`, ... extension methods.
- Converters/handlers registered as singletons; registry builds graph once; options via `IOptions<T>` bound from
  settings + policy overlay (`PolicyAwareOptionsProvider`).
- ViewModels transient per tab; services singletons; `IDispatcher` abstraction for UI thread marshalling.

## Build and run

```
tools/fetch-native.ps1 -Rid win-x64,win-arm64     # downloads+verifies pdfium, qpdf, resvg, tesseract into native/bin
dotnet build Modit.sln -c Debug -p:Platform=x64
dotnet run --project src/Modit.App -p:Platform=x64   # unpackaged dev run (WindowsAppSDKSelfContained)
dotnet test Modit.sln --filter Category!=UI
dotnet test tests/Modit.App.UITests                   # requires built app, interactive session
tools/build.ps1 -Configuration Release -Rid win-x64 -Package msix|msi|portable
```

- Debug runs **unpackaged** for fast F5; packaged (MSIX) run via `Modit.App (Package)` launch profile to test
  identity-dependent features (context menu, notifications, protocol).
- `RuntimeIdentifiers` win-x64;win-arm64; `Platforms` x64;ARM64 in WinUI projects; native DLLs copied to
  `$(OutDir)native\$(RuntimeIdentifier)\` via MSBuild targets reading `native/manifest.json`.

## Versioning and branching

- Trunk-based: `main` always releasable; feature branches short-lived; squash merges; Conventional Commits
  (`feat(pdf): ...`, `fix(ui): ...`, `perf(render): ...`) → changelog generated.
- Version from `version.json` (Nerdbank.GitVersioning) → assembly/MSIX/MSI versions.
- Tags `v1.2.3` trigger `release.yml`.

## Definition of Done (every task)

1. Code compiles with zero warnings on x64 and ARM64 configurations.
2. Unit tests added/updated; coverage of new `Core`/`Pdf`/`Convert` code ≥ 80% lines (Coverlet report).
3. If user-facing: strings in `.resw`, `AutomationProperties.Name` set, keyboard reachable, dark/light/high
   contrast checked, screenshot attached to PR description.
4. If an Operation: CLI subcommand exists, JSON schema generated, documented in `docs/admin/cli.md`, audit entry
   emitted, policy gate wired if applicable, recipe-serializable.
5. If native/engine: fuzz corpus run 5 minutes without host crash → or crash reproduces in host only and UI
   recovers (test exists).
6. Performance-sensitive: benchmark added or existing benchmark unchanged within tolerance.
7. `docs/` updated (plan deviations → ADR).
8. App launched manually (or via UI test) and the feature exercised end to end on a real file.

## Fixture corpus (tests/fixtures)

- `pdf/`: text-simple.pdf, scanned-50p.pdf (synthetic), forms-acroform.pdf, encrypted-aes256-user.pdf,
  encrypted-owner-only.pdf, outlines-nested.pdf, pagelabels-roman.pdf, annotations-all-types.pdf,
  signed-valid.pdf, signed-tampered.pdf, attachments.pdf, layers-ocg.pdf, linearized.pdf, damaged-xref.pdf,
  huge-page-a0.pdf, cjk-fonts.pdf, rtl-arabic.pdf, transparency-groups.pdf, jpx-images.pdf, 1000-pages.pdf
  (generated at test time), pdfa-2b-valid.pdf.
- `images/`: each supported format incl. animated GIF/WebP, EXIF-rotated JPEG, 16-bit PNG, CMYK JPEG, HEIC
  (if decoder), multi-page TIFF, ICO multi-size, PSD, JXL.
- `svg/`: text, gradients, filters, external font refs, huge viewBox, malformed.
- `docs/`: docx/xlsx/pptx/odt/rtf/md/html/eml/msg/epub samples with known expected text.
- `media/`: 2-second mp4/mkv/webm, mp3/wav/flac.
- `archives/`: zip (incl. zip-slip attempt, bomb small sample), 7z, tar.gz, rar (test-only).
- `fuzz/`: seed corpus for PDF/images; crashers found are added as regression fixtures.
All fixtures must be redistributable (created by us or public domain); record provenance in `fixtures/README.md`.
