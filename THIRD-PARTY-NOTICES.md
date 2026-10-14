# Third-party notices

Modit includes the third-party software listed below. Each component is used under the license named here.
Native libraries are shipped as separate, unmodified DLLs that Modit loads dynamically.

## Native engines

| Component | Version | License | Source |
|-----------|---------|---------|--------|
| PDFium | chromium/8100 | BSD-3-Clause and Apache-2.0 | https://pdfium.googlesource.com/pdfium, binaries from https://github.com/bblanchon/pdfium-binaries |
| QPDF | 12.4.2 | Apache-2.0 | https://github.com/qpdf/qpdf |
| libvips and its bundled codecs | 8.18.7 | LGPL (see below) | https://github.com/libvips/libvips, binaries from https://github.com/kleisauke/libvips-packaging |
| Microsoft Visual C++ runtime | 14.x | Microsoft Visual C++ Redistributable license | https://learn.microsoft.com/cpp/windows/latest-supported-vc-redist |

### libvips (LGPL)

libvips and several of the libraries bundled with it (glib, librsvg, libheif, libexif, pango, fribidi,
proxy-libintl) are licensed under the GNU Lesser General Public License. Modit uses them only through the
unmodified `libvips-42.dll` loaded at run time by the separate `Modit.Engine.Host.exe` process. You may replace
that DLL with your own build of the same libvips version; Modit will load it. The complete list of libraries in
the libvips bundle and their licenses is in `licenses/libvips-THIRD-PARTY-NOTICES.md` next to the application.
The corresponding source code is available from the projects linked in that file, or on request from the
publisher of Modit for three years after the release that shipped it.

## .NET libraries

| Component | License |
|-----------|---------|
| .NET runtime and Microsoft.Extensions.* | MIT |
| Windows App SDK, WinUI 3, Win2D | MIT |
| CommunityToolkit.Mvvm | MIT |
| StreamJsonRpc, Microsoft.VisualStudio.Threading | MIT |
| MessagePack for C# | MIT |
| System.CommandLine | MIT |
| NetVips | MIT |
| Velopack | MIT |
