using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Modit.Native.Qpdf;
using static Modit.Native.Qpdf.NativeMethods;

namespace Modit.Engine.Host.Engines;

/// <summary>
/// Runs QPDF jobs (merge, split, encrypt, linearize, repair, check). Each job gets its own logger so
/// warnings and errors are captured per call instead of going to the process's stdout/stderr.
/// </summary>
internal static unsafe class QpdfEngine
{
    public static Task<QpdfJobResult> RunJobAsync(string jobJson, CancellationToken cancellationToken) =>
        Task.Run(() => RunJob(jobJson), cancellationToken);

    private static QpdfJobResult RunJob(string jobJson)
    {
        var sink = new LogSink();
        GCHandle sinkHandle = GCHandle.Alloc(sink);
        IntPtr job = qpdfjob_init();
        IntPtr logger = qpdflogger_create();
        try
        {
            void* userData = (void*)GCHandle.ToIntPtr(sinkHandle);
            qpdflogger_set_info(logger, QpdfConstants.LogDestCustom, &OnInfo, userData);
            qpdflogger_set_warn(logger, QpdfConstants.LogDestCustom, &OnWarn, userData);
            qpdflogger_set_error(logger, QpdfConstants.LogDestCustom, &OnError, userData);
            qpdflogger_set_save(logger, QpdfConstants.LogDestDiscard, null, null, 0);
            qpdfjob_set_logger(job, logger);

            int exitCode;
            byte[] json = Encoding.UTF8.GetBytes(jobJson + '\0');
            fixed (byte* jsonPtr = json)
            {
                exitCode = qpdfjob_initialize_from_json(job, jsonPtr);
            }

            if (exitCode == QpdfConstants.ExitSuccess)
            {
                exitCode = qpdfjob_run(job);
            }

            return new QpdfJobResult(exitCode, LogSink.Lines(sink.Warn), LogSink.Lines(sink.Error), sink.Info.ToString());
        }
        finally
        {
            qpdflogger_cleanup(&logger);
            qpdfjob_cleanup(&job);
            sinkHandle.Free();
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int OnInfo(byte* data, nuint length, void* userData) => Append(userData, data, length, 0);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int OnWarn(byte* data, nuint length, void* userData) => Append(userData, data, length, 1);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int OnError(byte* data, nuint length, void* userData) => Append(userData, data, length, 2);

    private static int Append(void* userData, byte* data, nuint length, int channel)
    {
        if (GCHandle.FromIntPtr((IntPtr)userData).Target is LogSink sink && length > 0)
        {
            string text = Encoding.UTF8.GetString(data, checked((int)length));
            StringBuilder target = channel switch { 0 => sink.Info, 1 => sink.Warn, _ => sink.Error };
            lock (sink)
            {
                target.Append(text);
            }
        }

        return 0;
    }

    private sealed class LogSink
    {
        public StringBuilder Info { get; } = new();

        public StringBuilder Warn { get; } = new();

        public StringBuilder Error { get; } = new();

        public static string[] Lines(StringBuilder builder) =>
            builder.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }
}
