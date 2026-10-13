using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Modit.Native.Qpdf;

/// <summary>
/// Raw P/Invoke surface for qpdf30.dll (qpdfjob-c.h and qpdflogger-c.h in native/headers/qpdf).
/// All strings are null-terminated UTF-8.
/// </summary>
public static unsafe partial class NativeMethods
{
    internal const string Lib = "qpdf";

#pragma warning disable CA2255 // The resolver must be registered before the first P/Invoke in this assembly.
    [ModuleInitializer]
    internal static void RegisterResolver() =>
        NativeLibraryLocator.Register(typeof(NativeMethods).Assembly, Lib, "qpdf30.dll");
#pragma warning restore CA2255

    // ---- qpdfjob-c.h ----

    [LibraryImport(Lib)]
    public static partial IntPtr qpdfjob_init();

    [LibraryImport(Lib)]
    public static partial void qpdfjob_cleanup(IntPtr* job);

    [LibraryImport(Lib)]
    public static partial void qpdfjob_set_logger(IntPtr job, IntPtr logger);

    [LibraryImport(Lib)]
    public static partial int qpdfjob_initialize_from_json(IntPtr job, byte* json);

    [LibraryImport(Lib)]
    public static partial int qpdfjob_run(IntPtr job);

    [LibraryImport(Lib)]
    public static partial void qpdfjob_register_progress_reporter(
        IntPtr job, delegate* unmanaged[Cdecl]<int, void*, void> reportProgress, void* data);

    // ---- qpdflogger-c.h ----

    [LibraryImport(Lib)]
    public static partial IntPtr qpdflogger_create();

    [LibraryImport(Lib)]
    public static partial void qpdflogger_cleanup(IntPtr* logger);

    [LibraryImport(Lib)]
    public static partial void qpdflogger_set_info(
        IntPtr logger, int destination, delegate* unmanaged[Cdecl]<byte*, nuint, void*, int> fn, void* userData);

    [LibraryImport(Lib)]
    public static partial void qpdflogger_set_warn(
        IntPtr logger, int destination, delegate* unmanaged[Cdecl]<byte*, nuint, void*, int> fn, void* userData);

    [LibraryImport(Lib)]
    public static partial void qpdflogger_set_error(
        IntPtr logger, int destination, delegate* unmanaged[Cdecl]<byte*, nuint, void*, int> fn, void* userData);

    [LibraryImport(Lib)]
    public static partial void qpdflogger_set_save(
        IntPtr logger, int destination, delegate* unmanaged[Cdecl]<byte*, nuint, void*, int> fn, void* userData,
        int onlyIfNotSet);
}

public static class QpdfConstants
{
    /// <summary>qpdf_exit_code_e (Constants.h).</summary>
    public const int ExitSuccess = 0;
    public const int ExitError = 2;
    public const int ExitWarning = 3;

    /// <summary>qpdf_log_dest_e.</summary>
    public const int LogDestDiscard = 3;
    public const int LogDestCustom = 4;
}
