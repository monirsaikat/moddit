using System.Reflection;
using System.Runtime.InteropServices;

namespace Modit.Native;

/// <summary>
/// Resolves pinned native engines from <c>&lt;app&gt;\native\&lt;rid&gt;\</c>. Loading by full path makes
/// Windows search the directory of the DLL for its dependencies (the bundled MSVC runtime for QPDF),
/// and keeps the process from ever picking up a same-named DLL from PATH.
/// </summary>
internal static class NativeLibraryLocator
{
    public static string Rid { get; } = RuntimeInformation.ProcessArchitecture switch
    {
        Architecture.Arm64 => "win-arm64",
        Architecture.X64 => "win-x64",
        var other => throw new PlatformNotSupportedException($"Unsupported process architecture: {other}"),
    };

    public static void Register(Assembly assembly, string importName, string fileName)
    {
        NativeLibrary.SetDllImportResolver(assembly, (name, _, _) =>
            string.Equals(name, importName, StringComparison.Ordinal) ? Load(fileName) : IntPtr.Zero);
    }

    private static IntPtr Load(string fileName)
    {
        string baseDir = AppContext.BaseDirectory;
        string[] candidates =
        [
            Path.Combine(baseDir, "native", Rid, fileName),
            Path.Combine(baseDir, fileName),
        ];

        foreach (string candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return NativeLibrary.Load(candidate);
            }
        }

        throw new DllNotFoundException(
            $"Native engine '{fileName}' was not found. Looked in: {string.Join("; ", candidates)}");
    }
}
