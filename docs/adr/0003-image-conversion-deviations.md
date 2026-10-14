# ADR 0003: Image conversion, first cut (pulled forward from milestone 3)

Status: accepted by the implementer, **needs owner approval** for item 1 (it touches LOCKED ADR-006).
Date: 2026-10-14

## Context

Version 0.3.0 adds image conversion and images to PDF ahead of the roadmap, because the app had no converters
at all. The work follows ADR-005 (libvips through NetVips, only inside `Modit.Engine.Host`) with the
deviations below.

## Decisions

1. **SVG is rendered by librsvg through libvips, not by resvg.** ADR-006 chose resvg. resvg publishes no
   Windows C-API binary, so it needs a Rust build for x64 and ARM64 in CI (ADR 0002 #9 already deferred it).
   librsvg ships inside the NetVips native bundle, so SVG input works today with no new toolchain. Swapping
   in resvg later changes only `ImageEngine.LoadSvg`. SVG to PDF as vectors (WebView2) is not built yet;
   Images to PDF rasterizes SVG at the chosen DPI.
2. **BMP and ICO go through Windows, plus small writers of our own.** The libvips bundle (8.18.7, "web"
   build) has no BMP, ICO, JPEG XL or ImageMagick loaders. BMP and ICO are decoded by the Windows Imaging
   Component (WinRT `BitmapDecoder`) in the host. `BmpWriter` writes 24-bit BMP; `IcoWriter` writes PNG-based
   icon entries at the standard sizes up to 256 px. HEIC falls back to WIC when libvips cannot decode it, and
   reports `ConvertCodecMissing` with the Store link when the HEVC extension is absent.
3. **No JPEG XL, PSD or TGA yet.** They need a fuller libvips build or our own decoders.
4. **No converter registry, options JSON schema or recipe system yet (M3.1, M3.6).** `ImageConvertService`
   and `ImageFormats` are a direct implementation. The registry will wrap them.
5. **Conversion runs one file at a time on one host** with libvips using up to four threads, instead of the
   plan's host pool.
6. **libvips operation cache is off** (`Cache.Max = 0`). With caching on, libvips keeps input files open, and
   Windows then refuses to rename or delete them while Modit runs.
7. **Images to PDF is written by our own minimal PDF writer in the host**, not by PDFium. Plain RGB or gray
   JPEGs are embedded unchanged (DCTDecode); everything else is stored losslessly (FlateDecode) on a white
   background. The result is verified by opening it with PDFium before it gets its final name.
8. **Licenses.** The libvips bundle includes LGPL libraries, some LGPLv3 through "any later version". That is
   allowed by ADR-018 for dynamically loaded, unmodified DLLs. `THIRD-PARTY-NOTICES.md` and the bundle's own
   notice list are now copied into every build by `tools/build.ps1`.

## Consequences

- Installer grows by about 19 MB (libvips and codecs).
- New error codes 700 to 703 for conversion failures.
- `modit convert` and `modit pdf from-images` exist; exit code 1 means some inputs failed.
