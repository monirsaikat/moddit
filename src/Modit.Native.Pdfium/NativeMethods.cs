using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Modit.Native.Pdfium;

/// <summary>
/// Raw P/Invoke surface for pdfium.dll. Signatures mirror the public headers in
/// native/headers/pdfium (fpdfview.h, fpdf_doc.h, fpdf_text.h). Nothing else lives here.
/// PDFium is not thread-safe: every call must happen on the engine host's PDFium thread.
/// </summary>
public static unsafe partial class NativeMethods
{
    internal const string Lib = "pdfium";

#pragma warning disable CA2255 // The resolver must be registered before the first P/Invoke in this assembly.
    [ModuleInitializer]
    internal static void RegisterResolver() =>
        NativeLibraryLocator.Register(typeof(NativeMethods).Assembly, Lib, "pdfium.dll");
#pragma warning restore CA2255

    // ---- fpdfview.h: library ----

    [LibraryImport(Lib)]
    public static partial void FPDF_InitLibraryWithConfig(FPDF_LIBRARY_CONFIG* config);

    [LibraryImport(Lib)]
    public static partial void FPDF_DestroyLibrary();

    [LibraryImport(Lib)]
    public static partial uint FPDF_GetLastError();

    // ---- fpdfview.h: document ----

    [LibraryImport(Lib)]
    public static partial IntPtr FPDF_LoadMemDocument64(void* dataBuf, nuint size, byte* password);

    [LibraryImport(Lib)]
    public static partial IntPtr FPDF_LoadCustomDocument(FPDF_FILEACCESS* fileAccess, byte* password);

    [LibraryImport(Lib)]
    public static partial void FPDF_CloseDocument(IntPtr document);

    [LibraryImport(Lib)]
    public static partial int FPDF_GetPageCount(IntPtr document);

    [LibraryImport(Lib)]
    public static partial int FPDF_GetFileVersion(IntPtr document, out int fileVersion);

    [LibraryImport(Lib)]
    public static partial uint FPDF_GetDocPermissions(IntPtr document);

    [LibraryImport(Lib)]
    public static partial int FPDF_GetSecurityHandlerRevision(IntPtr document);

    [LibraryImport(Lib)]
    public static partial int FPDF_GetPageSizeByIndexF(IntPtr document, int pageIndex, out FS_SIZEF size);

    // ---- fpdfview.h: page ----

    [LibraryImport(Lib)]
    public static partial IntPtr FPDF_LoadPage(IntPtr document, int pageIndex);

    [LibraryImport(Lib)]
    public static partial void FPDF_ClosePage(IntPtr page);

    [LibraryImport(Lib)]
    public static partial float FPDF_GetPageWidthF(IntPtr page);

    [LibraryImport(Lib)]
    public static partial float FPDF_GetPageHeightF(IntPtr page);

    // ---- fpdfview.h: bitmap + render ----

    [LibraryImport(Lib)]
    public static partial IntPtr FPDFBitmap_CreateEx(int width, int height, int format, void* firstScan, int stride);

    [LibraryImport(Lib)]
    public static partial void FPDFBitmap_Destroy(IntPtr bitmap);

    [LibraryImport(Lib)]
    public static partial int FPDFBitmap_FillRect(IntPtr bitmap, int left, int top, int width, int height, uint color);

    [LibraryImport(Lib)]
    public static partial void FPDF_RenderPageBitmap(
        IntPtr bitmap, IntPtr page, int startX, int startY, int sizeX, int sizeY, int rotate, int flags);

    [LibraryImport(Lib)]
    public static partial void FPDF_RenderPageBitmapWithMatrix(
        IntPtr bitmap, IntPtr page, FS_MATRIX* matrix, FS_RECTF* clipping, int flags);

    // ---- fpdf_doc.h ----

    [LibraryImport(Lib)]
    public static partial uint FPDF_GetMetaText(IntPtr document, byte* tag, void* buffer, uint bufferLength);

    [LibraryImport(Lib)]
    public static partial uint FPDF_GetPageLabel(IntPtr document, int pageIndex, void* buffer, uint bufferLength);

    // ---- fpdf_text.h ----

    [LibraryImport(Lib)]
    public static partial IntPtr FPDFText_LoadPage(IntPtr page);

    [LibraryImport(Lib)]
    public static partial void FPDFText_ClosePage(IntPtr textPage);

    [LibraryImport(Lib)]
    public static partial int FPDFText_CountChars(IntPtr textPage);

    [LibraryImport(Lib)]
    public static partial int FPDFText_GetText(IntPtr textPage, int startIndex, int count, ushort* result);

    // ---- Editing (fpdf_edit.h, fpdf_flatten.h, fpdf_save.h, fpdf_transformpage.h) ----

    [LibraryImport(Lib)]
    public static partial IntPtr FPDFText_LoadStandardFont(IntPtr document, byte* font);

    [LibraryImport(Lib)]
    public static partial IntPtr FPDFText_LoadFont(IntPtr document, byte* data, uint size, int fontType, int cid);

    [LibraryImport(Lib)]
    public static partial void FPDFFont_Close(IntPtr font);

    [LibraryImport(Lib)]
    public static partial IntPtr FPDFPageObj_CreateTextObj(IntPtr document, IntPtr font, float fontSize);

    [LibraryImport(Lib)]
    public static partial int FPDFText_SetText(IntPtr textObject, ushort* text);

    [LibraryImport(Lib)]
    public static partial int FPDFPageObj_SetFillColor(IntPtr pageObject, uint r, uint g, uint b, uint a);

    [LibraryImport(Lib)]
    public static partial void FPDFPageObj_Transform(IntPtr pageObject, double a, double b, double c, double d, double e, double f);

    [LibraryImport(Lib)]
    public static partial int FPDFPageObj_GetBounds(IntPtr pageObject, out float left, out float bottom, out float right, out float top);

    [LibraryImport(Lib)]
    public static partial void FPDFPageObj_Destroy(IntPtr pageObject);

    [LibraryImport(Lib)]
    public static partial IntPtr FPDFPageObj_NewImageObj(IntPtr document);

    [LibraryImport(Lib)]
    public static partial int FPDFImageObj_SetBitmap(IntPtr* pages, int count, IntPtr imageObject, IntPtr bitmap);

    [LibraryImport(Lib)]
    public static partial int FPDFImageObj_SetMatrix(IntPtr imageObject, double a, double b, double c, double d, double e, double f);

    [LibraryImport(Lib)]
    public static partial void FPDFPage_InsertObject(IntPtr page, IntPtr pageObject);

    [LibraryImport(Lib)]
    public static partial int FPDFPage_GenerateContent(IntPtr page);

    [LibraryImport(Lib)]
    public static partial int FPDFPage_GetRotation(IntPtr page);

    [LibraryImport(Lib)]
    public static partial int FPDFPage_Flatten(IntPtr page, int flag);

    [LibraryImport(Lib)]
    public static partial int FPDFPage_GetCropBox(IntPtr page, out float left, out float bottom, out float right, out float top);

    [LibraryImport(Lib)]
    public static partial int FPDFPage_GetMediaBox(IntPtr page, out float left, out float bottom, out float right, out float top);

    [LibraryImport(Lib)]
    public static partial void FPDFPage_SetCropBox(IntPtr page, float left, float bottom, float right, float top);

    [LibraryImport(Lib)]
    public static partial int FPDF_SaveAsCopy(IntPtr document, FPDF_FILEWRITE* fileWrite, uint flags);
}

/// <summary>Write callback for <see cref="NativeMethods.FPDF_SaveAsCopy"/>; <c>unsigned long</c> is 32-bit on Windows.</summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct FPDF_FILEWRITE
{
    public int Version;
    public delegate* unmanaged[Cdecl]<FPDF_FILEWRITE*, void*, uint, int> WriteBlock;
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct FPDF_LIBRARY_CONFIG
{
    public int Version;
    public byte** UserFontPaths;
    public void* Isolate;
    public uint V8EmbedderSlot;
    public void* Platform;
    public int RendererType;
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct FPDF_FILEACCESS
{
    /// <summary>C <c>unsigned long</c> is 32-bit on Windows: custom access is limited to 4 GB.</summary>
    public uint FileLength;
    public delegate* unmanaged[Cdecl]<void*, uint, byte*, uint, int> GetBlock;
    public void* Param;
}

[StructLayout(LayoutKind.Sequential)]
public struct FS_MATRIX
{
    public float A, B, C, D, E, F;
}

[StructLayout(LayoutKind.Sequential)]
public struct FS_RECTF
{
    public float Left, Top, Right, Bottom;
}

[StructLayout(LayoutKind.Sequential)]
public struct FS_SIZEF
{
    public float Width, Height;
}

public static class PdfiumConstants
{
    public const uint ErrSuccess = 0;
    public const uint ErrUnknown = 1;
    public const uint ErrFile = 2;
    public const uint ErrFormat = 3;
    public const uint ErrPassword = 4;
    public const uint ErrSecurity = 5;
    public const uint ErrPage = 6;

    public const int RenderAnnotations = 0x01;
    public const int RenderLcdText = 0x02;
    public const int RenderPrinting = 0x800;
    public const int RenderReverseByteOrder = 0x10;

    public const int BitmapGray = 1;
    public const int BitmapBgr = 2;
    public const int BitmapBgrx = 3;
    public const int BitmapBgra = 4;

    public const int FontTrueType = 2;

    public const int FlattenForPrint = 1;
    public const int FlattenFail = 0;

    public const uint SaveNoIncremental = 1 << 1;
    public const uint SaveRemoveSecurity = 1 << 2;
    public const uint SaveSubsetNewFonts = 1 << 3;
}
